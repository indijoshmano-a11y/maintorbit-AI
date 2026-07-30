# ADR-0018 — Package every component as an immutable container

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0018 |
| **Title** | Package every component as an immutable container, orchestrated by Docker Compose |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Operations |
| **Implements** | DP-001, DP-002, DP-009, DP-010, DP-012 |
| **Supersedes** | — |

---

## 1. Context

NFR-PORT-001 requires every component to run in a container. NFR-PORT-003 requires
configuration by environment with no environment-specific build artifacts. NFR-PORT-004
requires the whole platform to run on a single host for development and evaluation.
NFR-PORT-007 requires deployment in a customer environment without product modification
at v2.1.

The target hosting is Azure VMs (ADR-0022), not a managed container platform. The team is
small.

## 2. Problem Statement

How should the platform be packaged and orchestrated so that the same artifacts run on a
developer's machine, in CI, in our hosted deployment, and eventually in a customer's own
environment?

## 3. Decision

**Every component is an immutable container image. Orchestration is Docker Compose.**

| Image | Responsibility |
| --- | --- |
| `maintorbit-api` | Gateway hot path, management surface, SignalR hubs |
| `maintorbit-worker` | Hangfire server, batch persistence, projections, scheduled jobs |
| `maintorbit-web` | Next.js server |
| `maintorbit-nginx` | TLS termination, routing, static assets, connection limits |
| `migration-runner` | Schema migration, run to completion |

Supporting services — PostgreSQL, Redis, an S3-compatible object store — are standard
images in the development and single-host topologies, and may be managed services in the
hosted deployment.

**Binding rules:**

| # | Rule | Rationale |
| --- | --- | --- |
| 1 | **Images are built once and promoted**, never rebuilt per environment | The tested artifact is the deployed artifact (NFR-PORT-003) |
| 2 | **Migrations run as a separate step**, never at application startup | Multiple instances would race to migrate the same database |
| 3 | **All containers run as non-root**, read-only root filesystem where practical | Baseline hardening |
| 4 | **Explicit health checks distinguishing liveness from readiness** | NFR-OBS-005; required for rolling deployment gating |
| 5 | **API and Worker are separate images** from the same solution | ADR-0014; batch work must not compete with the latency budget |
| 6 | **The single-host topology is maintained continuously** | It is the self-hosted evaluation product, not a convenience |

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Kubernetes from day one | Full orchestration with scheduling, self-healing, rolling updates | Better operational properties at scale. Rejected now: the team is small, the topology is two VMs, and Kubernetes is a substantial operational commitment for benefits that only appear at higher instance counts. **Expected eventual destination — see §9** |
| Deploy directly to VMs, no containers | Publish binaries, run as system services | **Violates NFR-PORT-001** and makes NFR-PORT-007 self-hosting far harder. Loses environment reproducibility |
| Managed container service | Azure Container Apps or similar | Reduces operations; conflicts with the stated Azure VM approach and weakens the self-hosting story if depended upon |
| Docker Swarm | Lighter orchestration than Kubernetes | Declining ecosystem investment; Compose is simpler and Kubernetes is the credible next step |
| Single combined image | One image running all processes | Defeats the API/Worker separation that protects the latency budget; couples scaling of unrelated components |

## 5. Pros

- **The same artifacts run everywhere** — developer machine, CI, hosted deployment,
  customer environment.
- **Compose is simple to operate and to reason about**, which matters for a small team
  and for customers who must run it themselves.
- **NFR-PORT-004 is satisfied directly** — one command starts the whole platform on one
  host.
- **Immutable promotion eliminates a class of environment-drift defects.**
- **The single-host topology doubles as the self-hosted evaluation product**, which is
  what makes v2.1 a packaging exercise rather than a re-architecture.

## 6. Cons

- **No automatic rescheduling on host failure.** If a VM dies, its containers do not move;
  the load balancer routes around it and capacity is reduced until the host returns.
- **Scaling is manual.** Adding an instance is a deliberate operation, not an autoscaling
  policy.
- **Rolling deployment is scripted, not orchestrated** — drain, replace, health-check,
  return to rotation, per VM (ADR-0022 §3.7).
- **Compose becomes operationally limiting** beyond a handful of instances; manual
  placement and update sequencing get error-prone.
- **Supporting services in containers** for the single-host topology have different
  durability characteristics from managed equivalents, which must be clear in
  documentation.

## 7. Consequences

- **Migrations must be backward-compatible with the previous application version.** During
  rolling deployment both versions run against the same schema. Expand-and-contract is
  mandatory, not advisory.
- **A failed migration aborts the rollout** before any new container starts, so the
  migration step is a deployment gate.
- **Health checks are load-bearing infrastructure**, not diagnostics. Readiness gates
  return-to-rotation; a wrong readiness check produces either dropped requests or a stalled
  deployment.
- **Image size and startup time affect deployment duration** and therefore the availability
  budget (ADR-0022 §3.3). Trimming and layer discipline are operational concerns.
- **Secrets are injected at container start**, never baked into images (NFR-SEC-012).
- **Infrastructure should be defined as code** — manually configured VMs cannot be
  reproduced reliably, which undermines both disaster recovery and the self-hosted path.
  This is unresolved decision DD-5.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A non-backward-compatible migration breaks live traffic during rollout | High | Medium | Expand-and-contract mandatory; migration review gate; tested against the previous version in CI |
| R-2 | Single-host topology rots, making v2.1 a re-architecture | Medium | **High** | It is the local development configuration and is exercised in CI |
| R-3 | Compose becomes operationally limiting as instance count grows | Medium | Medium | Kubernetes migration path per §9, driven by operational pain rather than fashion |
| R-4 | Host failure removes capacity with no automatic rescheduling | Medium | Medium | Multiple VMs behind a load balancer (ADR-0022 T1); capacity headroom |
| R-5 | A secret is baked into an image | High | Low | Secret scanning is build-gating (NFR-SEC-012); configuration by environment only |
| R-6 | Container images accumulate vulnerabilities between rebuilds | Medium | High | Dependency and image scanning on every build (ADR-0019); base image updates on a schedule |

## 9. Future Revisions

Revisit when **operational pain justifies it**, not on schedule:

- **Instance count exceeds what manual placement can manage reliably** — roughly the point
  where a rolling deployment script becomes fragile.
- **Automatic rescheduling on host failure becomes worth its cost** — driven by an actual
  availability incident rather than by anticipation.
- **Multi-region deployment (v2.1)** multiplies the placement problem.

The expected destination is Kubernetes. Whatever is chosen **must remain self-hostable**
(NFR-PORT-002), which is satisfied by Kubernetes but not by cloud-proprietary
orchestration. Note that adopting Kubernetes affects customers too: a self-hosted
deployment requiring a Kubernetes cluster is a materially higher bar than one requiring
Docker Compose, so the single-host Compose topology should be retained as the evaluation
path even if the hosted deployment moves.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.1 containers; §3.7 deployment process; §3.9 self-hosted path |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.2 container view |
| [`ADR-0022-deployment-topology.md`](ADR-0022-deployment-topology.md) | Where these containers run |
| [`ADR-0019-github-actions.md`](ADR-0019-github-actions.md) | What builds and promotes them |
| [`ADR-0014-hangfire.md`](ADR-0014-hangfire.md) | API and Worker separation |
| [`ADR-0017-object-storage.md`](ADR-0017-object-storage.md) | Portable implementation in the single-host topology |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-PORT-001 … 007 |
| `../../docker/` | Compose files and Dockerfiles |
