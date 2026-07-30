# ADR-0020 — Structured logging, OpenTelemetry, and correlation everywhere

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0020 |
| **Title** | Structured logging and OpenTelemetry, with a correlation identifier propagated to the caller |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Operations |
| **Implements** | NFR-OBS-001 … 012; RF-008 |
| **Supersedes** | — |

---

## 1. Context

Observability serves three distinct audiences here, and the third is unusual:

1. **Operations**, diagnosing platform incidents.
2. **Engineering**, understanding behaviour and regressions.
3. **Customers** — specifically the P-02 persona, whose stated abandonment trigger is
   *"an incident where platform logs cannot explain what happened."* This persona rejects
   vendors who describe failure behaviour vaguely.

NFR-OBS-006 makes this a product capability, not an internal one: customers must be able
to retrieve the **complete routing decision record** for any request by its correlation
identifier — target selected, alternatives considered, circuit breaker states, retries
with causes, fallbacks with causes, and latency at each stage.

Two constraints cut across everything: NFR-OBS-009 forbids credentials, tokens, or prompt
content in logs; NFR-PORT-002 forbids a dependency that cannot run in a customer
environment.

## 2. Problem Statement

How should the platform be instrumented so that any single request can be reconstructed
completely — by operations, by engineering, and by the customer — without leaking
credentials or content, and without a non-portable dependency?

## 3. Decision

**Structured logs, OpenTelemetry traces and metrics, and a correlation identifier
generated at ingress and returned to the caller.**

| Concern | Decision |
| --- | --- |
| Logs | Structured and machine-parseable; never free-text-only |
| Traces | OpenTelemetry, covering the full request path **including provider calls** |
| Metrics | OpenTelemetry, exported in an open format consumable by standard monitoring |
| Correlation | Generated at ingress, propagated through every component, **returned to the caller** |
| Decision records | A distinct, customer-retrievable artifact — not a log line |
| Content and credentials | **Excluded by construction**, not by filtering |
| Backend | Vendor-neutral collector; the platform depends on OpenTelemetry, not on a specific vendor |

**Three record types are deliberately distinct** and must not be conflated:

| Type | Purpose | Guarantees | Consumer |
| --- | --- | --- | --- |
| **Application log** | Diagnosis | Best-effort, sampled if needed | Operations, engineering |
| **Audit Event** | Compliance record | **Never sampled, immutable** (ADR-0011) | Customer, auditors |
| **Decision Record** | Request reconstruction | Complete per request | Customer, support |

Conflating these is the most common observability mistake in this class of system: an
audit trail implemented as log lines inherits log sampling and log retention, and quietly
fails NFR-DATA-007.

**Alerting must exist for every condition that would breach an availability or
data-integrity requirement** (NFR-OBS-008), and **runbooks must exist before an alert is
enabled** (NFR-OBS-012).

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Vendor SDK instrumentation directly | Instrument with a specific observability vendor's library | Couples application code to a vendor; **violates NFR-PORT-002** for self-hosted deployment. OpenTelemetry gives the same data with a swappable backend |
| Logs only, no traces | Structured logging with correlation | Sufficient for a single process; loses causality and timing across the Worker, Gateway, and provider calls. Cannot satisfy NFR-OBS-006's per-stage latency requirement |
| Metrics only | Aggregate signals | Excellent for detection, useless for reconstructing a single request — which is the customer-facing requirement |
| Audit events as log entries | Reuse the logging pipeline for audit | **Rejected firmly.** Audit inherits log sampling and retention, silently violating NFR-DATA-007 and FR-AUD-004. Audit is a distinct store with distinct guarantees |
| Sample traces at high volume | Standard practice for cost control | Applied to *traces* only. **Never** to audit or usage (ADR-0011) — the distinction in §3 exists precisely to make this safe |

## 5. Pros

- **NFR-OBS-006 becomes a differentiator.** Given one identifier, the platform reconstructs
  exactly what happened. Most competing products do not provide this fully, and it directly
  addresses the P-02 persona's decisive evaluation criterion.
- **Vendor-neutral instrumentation** means the backend can change without touching
  application code, and a self-hosted customer can point it at their own collector.
- **Correlation returned to the caller** makes support conversations tractable — the
  customer supplies an identifier rather than a timestamp and a description.
- **Separating audit from logs protects NFR-DATA-007** structurally rather than by policy.
- Standard metric export integrates with whatever monitoring a customer already runs.

## 6. Cons

- **Instrumentation is pervasive work** that competes with feature delivery and is easy to
  under-invest in early and expensive to retrofit.
- **Trace overhead is real**, particularly on the hot path where a 15 ms budget leaves no
  slack. Sampling reduces it but reduces reconstruction completeness in exactly the cases
  where it is wanted.
- **Decision record volume is substantial** — one per request, write-once, read-rarely —
  and becomes a storage burden at scale.
- **Three record types is three things to store, retain, and reason about.**
- Excluding content by construction constrains what can be logged during debugging, which
  is occasionally frustrating and is nonetheless correct.

## 7. Consequences

- **Correlation propagation is an integration-test concern.** A dropped identifier at any
  boundary breaks reconstruction, and the break is invisible until someone needs it. Every
  boundary must assert propagation.
- **Content and credentials are excluded by construction, not by scrubbing.** Credential
  material should not be a plain string type, so it cannot be interpolated into a log
  message by accident. Scrubbing is a second layer, not the primary control.
- **Decision records need their own retention and tiering.** They are the highest-volume,
  lowest-access record type and should not share the retention policy of audit events.
- **Trace sampling on the hot path is a deliberate trade-off** between overhead and
  completeness, and must be configurable — a customer investigating an incident may want
  full sampling temporarily.
- **Alerts require runbooks before enablement** (NFR-OBS-012). An alert with no documented
  response is noise that trains people to ignore alerts.
- **Per-Company operational metrics must not expose cross-tenant data** (NFR-OBS-010).

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A credential or prompt content reaches a log | **Critical** | Medium | Excluded by construction; typed credential material; scrubbing as a second layer; secret scanning; log review in security testing |
| R-2 | Correlation identifier dropped at a boundary | High | Medium | Integration test asserting propagation at every boundary |
| R-3 | Audit implemented as log entries, inheriting sampling | **Critical** | Medium | Distinct stores and distinct code paths; architecture review; the §3 table exists to make this explicit |
| R-4 | Trace overhead consumes hot-path latency budget | High | Medium | Sampling on the hot path; overhead measured against NFR-PERF-001 |
| R-5 | Decision record volume becomes a storage burden | Medium | **High** | Separate retention; tiering; write-once storage class |
| R-6 | Instrumentation under-invested early and retrofitted expensively | Medium | High | Instrumentation is part of the definition of done, not a follow-up |
| R-7 | Alert fatigue from alerts without runbooks | Medium | High | NFR-OBS-012 enforced — no runbook, no alert |

## 9. Future Revisions

Revisit when:

- **Decision record volume becomes material.** Tiering, compression, or a separate store
  may be warranted. This is expected at NFR-SCAL-007 volume.
- **Self-hosted deployment ships (v2.1).** Customers will want telemetry directed to their
  own collectors, and remote diagnosis without environment access becomes a real problem
  requiring its own design.
- **Multi-region deployment** requires trace correlation across regions.
- **Product analytics instrumentation is added.** Business goals G1.3, G2.1, G2.4, and
  G2.5 need measurement that does not exist yet, and it is a distinct concern from
  operational telemetry — it should not be bolted onto this pipeline without thought.
- **Compliance requires log integrity attestation.** NFR-COMP-003 tamper-evidence applies
  to audit records; if it extends to operational logs, the approach changes.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | Observability module; telemetry collector |
| [`../02-architecture/request-flow.md`](../02-architecture/request-flow.md) | §3.11 correlation across flows |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.10 decision record emission |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | Audit as a distinct, unsampled store |
| [`ADR-0019-github-actions.md`](ADR-0019-github-actions.md) | Architecture test enforcement |
| [`ADR-0021-fail-open-fail-closed.md`](ADR-0021-fail-open-fail-closed.md) | Telemetry is fail-open |
| [`ADR-0003-aspnet-core-9.md`](ADR-0003-aspnet-core-9.md) | First-class OpenTelemetry integration |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-02 — transparency as an evaluation criterion |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-OBS-001 … 012 |
