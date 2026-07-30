# ADR-0017 — Object storage behind an S3-compatible abstraction, Azure Blob as the hosted implementation

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0017 |
| **Title** | Use an S3-compatible object storage abstraction, with Azure Blob Storage as the hosted implementation |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | — (new; extends NFR-PORT-002 to blob storage) |
| **Supersedes** | — |

---

## 1. Context

Several requirements need durable storage of objects that do not belong in a relational
database:

| Need | Requirement | Release |
| --- | --- | --- |
| Generated data exports — usage, cost, audit | FR-TEN-014, FR-USG-008, FR-AUD-006, NFR-COMP-006 | MVP |
| Invoice documents | FR-BILL-006 | MVP |
| Chat document attachments | FR-CHAT-009 | v1.1 |
| Cold-tier ledger archive | NFR-DATA-007 completeness at NFR-SCAL-007 volume | v1.1+ |
| Backup artifacts | NFR-DR-005 | MVP |

Exports in particular can be large: a year of Usage Records for a Company at
NFR-SCAL-007 volume is not a response body, it is a file.

**The constraint that shapes this decision is NFR-PORT-002** — no dependency that cannot
run in a customer-controlled environment. Azure Blob Storage is named in the Phase 0
infrastructure stack and cannot run in a customer environment.

## 2. Problem Statement

How can the platform use Azure Blob Storage for its own hosted deployment without making
a managed Azure service a dependency of the product, which NFR-PORT-002 forbids and
v2.1 self-hosted deployment would break on?

## 3. Decision

**Define an object storage port in the Application layer with S3-compatible semantics.
Provide two adapters: Azure Blob Storage for the hosted deployment, and a self-hostable
S3-compatible implementation as the portable default.**

```
Application: object storage port (S3-compatible semantics)
        ├── Azure Blob adapter        → hosted MaintOrbit AI deployment
        └── S3-compatible adapter     → development, CI, self-hosted (MinIO or equivalent)
```

| Property | Decision |
| --- | --- |
| Semantics | S3-compatible — the widest self-hostable standard |
| Hosted implementation | Azure Blob Storage, via its S3-compatible surface or a thin adapter |
| Portable implementation | Self-hostable S3-compatible server |
| **Default in development and CI** | **The portable implementation**, not Azure |
| Encryption | At rest and in transit; objects containing customer data encrypted before upload where they contain exportable ledger content |
| Access | Time-limited, scoped, signed URLs; never public objects |
| Lifecycle | Retention and expiry rules per object class |
| Tenant isolation | Object keys are Company-scoped; access is authorized by the application, never by object path alone |

**This is the same pattern as ADR-0008's key custodian**, and for the same reason: a
pluggable port with the portable implementation as the default is the only reliable way to
keep NFR-PORT-002 true.

**Important distinction.** Using a managed Azure service for *our own hosting* does not
violate NFR-PORT-002. The constraint is on the **product's dependencies**, not on our
operational choices. The product depends on S3-compatible object storage; we happen to
satisfy that with Azure Blob. This distinction is the whole decision — conflating the two
would either rule out reasonable operational choices for no benefit, or quietly break
self-hosting.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Azure Blob SDK used directly throughout | Simplest; full feature access | **Violates NFR-PORT-002.** Would make v2.1 self-hosted deployment a re-architecture rather than a packaging exercise |
| Store objects in PostgreSQL as binary | No new dependency at all | Genuinely viable for invoices and small exports. Rejected: large exports and cold-tier archives would bloat the database, complicate backup, and compete with the ledger for the store that is already the expected write bottleneck (ADR-0004 §6) |
| Local filesystem on the application host | Simplest for a single host | Breaks with multiple API hosts (ADR-0022 topology T1) — an object written by one host is not visible to another. Also loses durability |
| Self-hosted S3-compatible only, no Azure | One implementation everywhere | Viable, and simpler. Rejected because it means operating storage infrastructure ourselves for the hosted deployment when a managed service is available and does not compromise portability |
| Defer object storage entirely to v1.1 | Avoid the decision at MVP | Exports (FR-TEN-014, FR-AUD-006) and invoices (FR-BILL-006) are MVP requirements. Streaming exports through the API without intermediate storage is possible for small volumes but fails at NFR-SCAL-007 scale |

## 5. Pros

- **NFR-PORT-002 is preserved** — the product depends on a self-hostable standard.
- **The hosted deployment benefits from a managed service** — durability, lifecycle
  management, and tiering without operating storage infrastructure.
- **v2.1 self-hosted deployment becomes a configuration change**, not a re-architecture.
- **S3 compatibility is the widest self-hostable object storage standard**, giving
  customers real choice in their own environments.
- **Cold-tier archival becomes affordable**, supporting the completeness-at-scale strategy
  that lets the no-sampling constraint survive efficiency goal G4.2.

## 6. Cons

- **An abstraction over an abstraction.** Azure Blob's native model is not identical to
  S3's, so the adapter absorbs a translation that direct SDK use would avoid.
- **Lowest-common-denominator feature set.** Azure-specific capabilities are unavailable
  unless the port grows, and growing it risks reintroducing the coupling.
- **Two implementations to test**, and the portable one must be exercised continuously or
  it rots — the exact risk ADR-0008 identified for the key custodian.
- **Another infrastructure component in the self-hosted deployment**, adding to what a
  customer must run.
- Signed-URL semantics differ subtly between implementations and are easy to get wrong.

## 7. Consequences

- **The portable implementation is the default in development and CI.** If only the Azure
  path is exercised, the portable path will be broken when v2.1 needs it — discovered at
  the worst possible time. This is a standing engineering requirement.
- **Objects are never publicly accessible.** Access is via time-limited, scoped signed
  URLs, and the application authorizes before issuing one. Object path is not an
  authorization mechanism.
- **Export objects contain tenant data**, so key naming must be Company-scoped and access
  must go through the application's permission model — an object store has no knowledge of
  ADR-0005's row-level security.
- **Exports containing ledger content are themselves auditable events** (FR-AUD-001 covers
  data export), and their retention must be configured, not indefinite.
- **The self-hosted deployment gains a dependency** that must appear in its documentation
  and single-host topology.
- **Cold-tier archival design depends on this port** and should be planned with it rather
  than bolted on.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Portable implementation is never exercised and breaks at v2.1 | Medium | **High** | It is the development and CI default; clean-environment deployment test per NFR-PORT verification |
| R-2 | An Azure-specific capability leaks into application code | Medium | Medium | Architecture test asserting no direct Azure SDK reference outside the adapter |
| R-3 | A signed URL is issued with excessive scope or lifetime, exposing tenant data | High | Medium | Short lifetimes; single-object scope; issuance is an authorized operation and is audited |
| R-4 | Objects escape retention policy and accumulate indefinitely | Medium | Medium | Lifecycle rules per object class; retention enforcement job |
| R-5 | Export objects are not tenant-scoped in their key structure, enabling enumeration | High | Low | Company-scoped keys with unguessable components; application authorization is the real control |
| R-6 | Large export generation exhausts memory or blocks a request | Medium | Medium | Exports generated asynchronously by the Worker (ADR-0014), streamed to storage, delivered by signed URL |

## 9. Future Revisions

Revisit when:

- **Self-hosted deployment ships (v2.1).** The portable implementation becomes
  customer-facing and its documentation, sizing, and backup guidance become deliverables.
- **Cold-tier ledger archival is implemented.** Access patterns for archival differ from
  export — infrequent, large, latency-tolerant — and may warrant a distinct storage class
  or a separate port.
- **Chat attachments ship (FR-CHAT-009, v1.1).** Attachments are customer content subject
  to Content Retention policy (NFR-PRIV-001), which the current port does not model.
  Retention and deletion semantics for attachments need explicit design.
- **Multi-region deployment (v2.1)** requires regional object placement for data residency
  (NFR-PRIV-013).

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.2 container view; NFR-PORT-002 constraint |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.9 self-hosted path; managed-service distinction |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.5 tiered retention |
| [`ADR-0008-credential-encryption.md`](ADR-0008-credential-encryption.md) | Same pluggable-port pattern, same rationale |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Port-and-adapter placement |
| [`ADR-0014-hangfire.md`](ADR-0014-hangfire.md) | Asynchronous export generation |
| [`ADR-0018-docker.md`](ADR-0018-docker.md) | Portable implementation in the single-host topology |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-PORT-002/007, NFR-DR-005, NFR-COMP-006 |
