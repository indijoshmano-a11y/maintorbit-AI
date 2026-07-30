# ADR-0003 — Use ASP.NET Core 9 and C# 13 for the backend

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0003 |
| **Title** | Use ASP.NET Core 9 and C# 13 as the backend platform |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Leadership |
| **Implements** | Phase 0 technology selection |
| **Supersedes** | — |

---

## 1. Context

The backend must serve two very different workloads from one codebase:

- A **latency-critical hot path** with a 15 ms median platform overhead budget
  (NFR-PERF-001) at 500 sustained and 2,000 peak requests per second
  (NFR-SCAL-002/003), holding 10,000 concurrent streaming connections
  (NFR-SCAL-004).
- A **management surface** with conventional CRUD, reporting, and background processing
  characteristics.

It must also run in customer-controlled environments without modification
(NFR-PORT-002/007), which excludes any platform requiring a proprietary managed runtime.

The stack was selected in Phase 0. This ADR records why it is the right selection, and —
more usefully — what properties the architecture now depends on.

## 2. Problem Statement

Which server platform can meet a single-digit-millisecond overhead budget under high
concurrency, support long-lived streaming connections efficiently, and remain fully
portable to customer-hosted environments?

## 3. Decision

Use **ASP.NET Core 9** on **.NET 9** with **C# 13** for all backend services: the API
host, the Worker host, and any future extracted services.

Specific platform capabilities the architecture depends on:

| Capability | Depended on by |
| --- | --- |
| High-throughput async I/O without thread-per-request | NFR-SCAL-002/003/004 |
| Efficient long-lived connection handling | Streaming inference and SignalR |
| Low allocation on hot paths, with pooling and span-based primitives | NFR-PERF-001 …003 |
| Built-in dependency injection as the composition mechanism | ADR-0001 layering |
| First-class OpenTelemetry integration | ADR-0020 |
| Cross-platform, container-native, self-hostable | NFR-PORT-001/002/007 |
| Ahead-of-time-friendly and trimmable | Container image size, startup time |
| Nullable reference types treated as errors | Defect prevention; set in `.editorconfig` |

The `.editorconfig` already promotes nullability and async-correctness warnings
(CS8600, CS8602, CS8618, CS4014) to errors. This is a platform-level defect control and
must not be relaxed.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Node.js / TypeScript | Shared language with the frontend; large ecosystem for AI tooling | Single-threaded execution model makes the CPU-bound portions of the hot path harder to keep within budget; weaker for long-running background processing; a shared language is convenience, not architecture |
| Go | Excellent concurrency and latency characteristics; small binaries | A genuinely strong fit for the Gateway specifically. Rejected because the *management* surface — 230 requirements of CRUD, validation, reporting — is where most of the code lives, and that is where a rich framework pays. Team familiarity also weighed |
| Java / Spring Boot | Mature, enterprise-proven, strong ecosystem | Comparable capability; higher memory footprint per instance affects container density; no decisive advantage over .NET for this workload |
| Rust | Best-in-class latency and resource efficiency | Development velocity cost is not justified when the latency budget is achievable on a managed runtime. Reconsider only if ADR-0010's budget proves unachievable |
| Python | Strong AI ecosystem | The platform brokers AI rather than performing inference, so the AI ecosystem is irrelevant. Concurrency and latency characteristics are poor for this workload |

## 5. Pros

- Meets the concurrency and latency requirements without unusual engineering.
- One platform serves both the hot path and the management surface, avoiding a
  polyglot backend at a stage where the team cannot support one.
- Strong static typing supports the domain modelling conventions in ADR-0001 — value
  objects, result types, and non-nullable invariants are natural rather than bolted on.
- Container-native and fully self-hostable, satisfying NFR-PORT-002 without qualification.
- Mature ecosystem for every dependency the architecture requires: EF Core (ADR-0023),
  Hangfire (ADR-0014), SignalR (ADR-0015), FluentValidation, Mapster, OpenTelemetry.

## 6. Cons

- The hot path's 15 ms budget requires deliberate allocation discipline. A managed
  runtime's garbage collection can produce latency outliers that affect NFR-PERF-003
  (p99 ≤ 100 ms) more than the median.
- Smaller hiring pool than JavaScript or Python in some markets.
- Framework conventions can encourage patterns that conflict with ADR-0001 — controllers
  with logic, service location, and framework types leaking into the domain.
- Ecosystem licensing changes are a real risk, as MediatR demonstrated and ADR-0012
  responds to.

## 7. Consequences

- **Garbage collection behaviour must be measured against NFR-PERF-003**, not assumed.
  Server garbage collection configuration and allocation profiling on the hot path are
  operational concerns, not micro-optimizations.
- **Allocation discipline is a hot-path requirement.** Per-chunk allocation during
  streaming would breach NFR-PERF-005's 5 ms per-chunk budget.
- **The API and Worker hosts share libraries but never a process** (ADR-0014), so batch
  work cannot compete with the hot path for the thread pool.
- **Every third-party package is subject to NFR-PORT-002 review** — nothing may be added
  that cannot run in a customer environment. This is enforced by architecture test AT-12.
- **Nullable-reference-type errors stay as errors.** Downgrading them to warnings would
  silently remove a defect class the domain model depends on.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Garbage collection pauses breach the p99 latency target | High | Medium | Allocation profiling on the hot path; server GC tuning; continuous latency measurement per NFR-PERF-018 |
| R-2 | A dependency's licence changes, as MediatR's did | Medium | Medium | Minimal dependency surface; ADR-0012 precedent for in-house replacement; dependency review each release per NFR-MAINT-011 |
| R-3 | Framework conventions erode ADR-0001 layering | Medium | High | Architecture tests AT-1, AT-2; `Api` project asserted to contain no business logic |
| R-4 | A package that cannot self-host is introduced, breaking NFR-PORT-002 | High | Medium | AT-12 build-gating check on the dependency list |
| R-5 | Major version upgrades introduce breaking changes on a support timeline we do not control | Medium | High | .NET 9 support window tracked; upgrade planned rather than forced |

## 9. Future Revisions

Revisit if:

- **ADR-0010's latency budget proves unachievable** on this runtime after profiling. The
  most likely response is not replacing the platform wholesale but extracting the Gateway
  (ADR-0002 §9) and implementing that single service in a runtime with more predictable
  latency characteristics.
- **A .NET version reaches end of support** — a scheduled upgrade, not a revision of this
  decision.
- **Polyglot backend becomes justified** after extraction. An extracted Gateway is a
  small, single-purpose service and is the one component where a different runtime could
  be evaluated on its merits.

Replacing .NET for the management surface is not anticipated under any foreseeable
condition; that is where the volume of code lives and where switching cost is highest.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.2 container view |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | Project structure and pipeline |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | The latency budget this platform must meet |
| [`ADR-0012-cqrs-dispatcher.md`](ADR-0012-cqrs-dispatcher.md) | Response to ecosystem licensing risk |
| [`ADR-0014-hangfire.md`](ADR-0014-hangfire.md) | Worker host separation |
| [`ADR-0015-signalr.md`](ADR-0015-signalr.md) | Long-lived connection handling |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-PERF, NFR-SCAL, NFR-PORT |
