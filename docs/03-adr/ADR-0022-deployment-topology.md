# ADR-0022 — Deploy on Azure VMs; two application hosts minimum for production

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0022 |
| **Title** | Deploy on Azure VMs with a minimum of two application hosts behind a load balancer |
| **Status** | **Proposed** — availability target conflict unresolved (decision DD-1) |
| **Date** | 2026-07-30 |
| **Deciders** | Leadership, Engineering, Operations |
| **Implements** | DP-004; addresses `deployment-architecture.md` §3.3 |
| **Supersedes** | — |

> **This ADR records a conflict between a stated infrastructure choice and a stated
> requirement.** It is Proposed, not Accepted, because §7 shows that a single-VM
> deployment cannot meet NFR-AVAIL-001. A decision is required — the conflict cannot be
> engineered away.

---

## 1. Context

Phase 0 selected **Azure VM** hosting with Docker Compose. NFR-AVAIL-001 requires
**≥ 99.9% monthly Gateway availability**. NFR-AVAIL-006 requires planned maintenance
without Gateway downtime. NFR-AVAIL-014 requires deployment without request loss.

Two properties of the architecture compound the problem:

- **Fail-closed dependencies set the availability floor** (ADR-0021). The Gateway cannot
  exceed the availability of Redis, because quota and budget checks fail closed.
- **On a single host, a Redis restart is a full Gateway outage** — routine maintenance
  becomes downtime.

## 2. Problem Statement

What deployment topology can satisfy a 99.9% availability target on Azure VM
infrastructure, and is that target achievable at all on the single-host configuration the
stack implies?

## 3. Decision

**Four staged topologies. Topology T1 — two application VMs in an availability set behind
an Azure Load Balancer, with a replicated data tier — is the minimum production
configuration.**

| Stage | Configuration | Use |
| --- | --- | --- |
| **T0** | Single host, all containers | **Development and evaluation only.** Also the self-hosted evaluation product (ADR-0018 rule 6) |
| **T1** | Two application VMs, load balancer, PostgreSQL primary + standby, Redis primary + replica | **Minimum production** |
| **T2** | Horizontally scaled application VMs; separated data tier; Redis roles separated | Growth |
| **T3** | Multi-region with residency selection | v2.1 — NFR-PRIV-013, NFR-DR-009 |

**T0 must never carry production traffic.** Every component is a single point of failure
and deployment requires downtime.

## 4. Alternatives Considered

| Alternative | Availability achievable | Cost | Assessment |
| --- | --- | --- | --- |
| **A — Two VMs in an availability set (T1)** | 99.9% achievable | ~2× compute plus load balancer | **Recommended.** The minimum honest configuration for the stated target |
| **B — Single VM, amend target to 99.5%** | 99.5% — 3.6 h/month | Lowest | Defensible for private beta **if published**. Weakens the P-02 persona's evaluation |
| **C — Single VM, claim 99.9%** | Not achievable | Lowest | **Unacceptable.** Overstating availability in a governance product is what `mission.md` §6 forbids, and the P-06 persona treats detected overstatement as disqualifying |
| **D — Managed platform services** | 99.95%+ | Higher; changes operating model | Conflicts with the stated Azure VM approach. Reconsider at T2 — it does **not** violate NFR-PORT-002, since the product depends on PostgreSQL and Redis, not on a vendor's managed offering |

## 5. Pros of the chosen topology

- **Meets NFR-AVAIL-001 honestly**, with budget left for unplanned events.
- **Planned maintenance becomes zero-downtime** — hosts are drained sequentially.
- **Rolling deployment satisfies NFR-AVAIL-014** without request loss.
- **Data tier replication satisfies NFR-AVAIL-013** — survives loss of any single node.
- **Redis replication removes the routine-maintenance outage** that makes T0 untenable.
- Straightforward to operate with Docker Compose; no orchestration platform required yet.

## 6. Cons

- **Roughly double the compute cost**, plus a load balancer, before serving a single
  additional request.
- **No automatic rescheduling on host failure** (ADR-0018 §6) — capacity is reduced until
  the host returns.
- **Manual scaling.** Adding a host is a deliberate operation.
- **Rolling deployment is scripted, not orchestrated**, and the script is a source of
  operational risk.
- Two hosts is the minimum, not a comfortable margin: during a rolling deployment, the
  system is briefly running on one.

## 7. The availability arithmetic

99.9% monthly is a budget of **43 minutes 12 seconds**, covering everything —
infrastructure failure, deployment, patching, dependency failure, and incident recovery.

| Consumer of the budget | Single VM (T0) | Two VMs (T1) |
| --- | --- | --- |
| Azure single-instance VM SLA | Consumes essentially the whole budget alone | Not applicable — availability set |
| Host OS patching, monthly | 5–15 min | 0 — drained sequentially |
| Application deployment, weekly | 8–20 min | 0 — rolling |
| Container restart on failure | 1–3 min per event | 0 — other host serves |
| PostgreSQL maintenance | 5–10 min | 0 — standby promotion |
| **Redis restart** | **1–2 min, Gateway halts** | 0 — replica promotion |
| **Total before any incident** | **Exceeds the budget** | **Within budget** |

**The finding is unambiguous: a single VM cannot meet 99.9%.** Planned maintenance alone
exhausts the monthly allowance before a single unplanned event. This is arithmetic, not
pessimism, and it cannot be resolved by better engineering within a single host.

## 8. Consequences

- **Decision DD-1 must be made before beta**, not before general availability. The
  published availability figure must match the achievable figure, and customer-facing
  material depends on it.
- **Redis replication with automatic failover is required from T1**, not deferred —
  otherwise ADR-0021's fail-closed classification makes routine maintenance an outage.
- **Rolling deployment requires backward-compatible migrations** (ADR-0018 §7), because
  both versions run concurrently against one schema.
- **Health checks gate return-to-rotation**, making them load-bearing infrastructure rather
  than diagnostics.
- **SignalR connections break on container replacement** (ADR-0015), so reconnection
  quality is a user-visible concern.
- **Infrastructure should be defined as code** (decision DD-5). Manually configured VMs
  cannot be reproduced reliably, undermining both disaster recovery and the self-hosted
  path.
- **Availability commitments must not be contractual until T1 is running and measured.**
  Segment 3.2 customers will want service level agreements with financial remedies;
  NFR-AVAIL-002's 99.95% target should be achieved and demonstrated before any such
  commitment.

## 9. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Ships on a single VM while claiming 99.9% | **Critical** | **High** | Decision DD-1 before beta; published availability must match achievable availability |
| R-2 | Redis failover exceeds Gateway tolerance, producing a visible outage | High | Medium | Automatic failover; client reconnection with retry; measured in failure-injection testing |
| R-3 | Rolling deployment script fails mid-rollout, leaving mixed versions | High | Medium | Health gating; automatic rollback on failed health check; backward-compatible migrations |
| R-4 | Two hosts provides no margin during deployment | Medium | High | Capacity headroom sized for single-host operation; three hosts once traffic justifies it |
| R-5 | Host patching windows accumulate into budget breaches | Medium | Medium | Sequential draining makes patching zero-downtime |
| R-6 | Backup restoration untested and fails when needed | High | Medium | NFR-DR-006 quarterly restoration exercise with recorded results |
| R-7 | Cost of T1 is questioned and single-VM is reinstated informally | High | Medium | This ADR records the arithmetic; reverting requires amending NFR-AVAIL-001 explicitly |

## 10. Future Revisions

Revisit when:

- **Decision DD-1 is made.** This ADR moves to Accepted with the chosen option recorded.
- **NFR-AVAIL-002's 99.95% target applies (v1.2).** T1 is unlikely to be sufficient;
  additional hosts, faster failover, and possibly managed data services become necessary.
- **Instance count outgrows Compose** (ADR-0018 §9). Orchestration changes the topology
  model.
- **Multi-region is required (v2.1).** T3 changes the data tier fundamentally —
  replication topology, write routing, and consistency all become regional concerns.
- **Managed data services are reconsidered at T2.** They would improve availability and
  reduce operational burden without violating NFR-PORT-002.

## 11. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.2 topologies; §3.3 the availability conflict; decision DD-1 |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | Scaling within and beyond T1 |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.6 failure impact |
| [`ADR-0018-docker.md`](ADR-0018-docker.md) | What runs on these hosts |
| [`ADR-0021-fail-open-fail-closed.md`](ADR-0021-fail-open-fail-closed.md) | Fail-closed dependencies set the availability floor |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | Replication requirement |
| [`ADR-0015-signalr.md`](ADR-0015-signalr.md) | Connection churn during rolling deployment |
| [`../01-product/mission.md`](../01-product/mission.md) | §6 — honesty about limitations |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-AVAIL-001 … 015, NFR-DR |
