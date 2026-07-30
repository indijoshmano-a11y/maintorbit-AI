# Backend Technologies

| Field | Value |
| --- | --- |
| Document | Backend Technologies |
| Version | 1.0 |
| Status | Draft — versions require verification |
| Owner | Engineering |
| Last updated | 2026-07-30 |
| Audience | Backend Engineering, Security, Architecture Review |
| Phase | 4 — Technology Standards |

---

> **Version verification applies throughout.** See
> [`technology-stack.md`](technology-stack.md) §1. In particular, the runtime version
> question in §3 of that document is unresolved and affects every package here that
> tracks the runtime major.
>
> **These packages are derived from architectural decisions, not from an existing
> manifest.** No application code exists yet. This is the intended dependency set, and it
> becomes the reference against which the first `Directory.Packages.props` is written.

---

## 1. Purpose

This document inventories every NuGet package MaintOrbit AI intends to take a dependency
on, with the rationale, lifecycle, and risk for each.

Its function is to make the dependency set a **decision** rather than an accumulation.
Every package here should be traceable to a requirement or an ADR; a package that appears
in the build without appearing here is either scope creep or a documentation gap.

---

## 2. Scope

**In scope:** NuGet packages for the API host, Worker host, and test projects; the
runtime and language; selection rationale and risk per package.

**Out of scope:** npm packages ([`frontend-technologies.md`](frontend-technologies.md)),
infrastructure ([`infrastructure-technologies.md`](infrastructure-technologies.md)),
external services ([`third-party-services.md`](third-party-services.md)), how packages are
added ([`dependency-policy.md`](dependency-policy.md)).

---

## 3. Runtime and language

### 3.1 .NET runtime

| Field | Value |
| --- | --- |
| **Purpose** | Application runtime for the API host, Worker host, and all backend libraries |
| **Why chosen** | Meets the concurrency and latency requirements (NFR-PERF-001, NFR-SCAL-002/004) without unusual engineering; one platform serves both the latency-critical hot path and the CRUD-heavy management surface; container-native and fully self-hostable (NFR-PORT-002); strong static typing supports the domain modelling conventions in ADR-0001 |
| **Alternatives considered** | Node.js — single-threaded model makes hot-path CPU work harder to bound. Go — genuinely strong for the Gateway, weaker where most of the code lives. Java/Spring — comparable, higher memory per instance. Rust — best latency, unjustified velocity cost. Python — irrelevant AI ecosystem here, since the platform brokers inference rather than performing it |
| **Version** | **10 LTS recommended** — Phase 0 specified 9. See [`technology-stack.md`](technology-stack.md) §3 |
| **Support lifecycle** | LTS = 36 months from GA; STS = 18 months. .NET 10 supported to approximately November 2028 |
| **Risks** | Garbage collection pauses affecting NFR-PERF-003 p99; ecosystem licence changes; framework conventions eroding ADR-0001 layering |
| **Upgrade strategy** | LTS to LTS, planned, never lapsing. Runtime major upgrades are architecture-reviewed and gated on full CI plus load test plus failure injection. Six months' calendar notice before support end |
| **Replacement strategy** | Not anticipated for the management surface — that is where the code volume and switching cost live. If ADR-0010's latency budget proves unachievable, the response is extracting the Gateway and implementing that one service in a runtime with more predictable latency, not replacing the platform |
| **Security considerations** | Patches must be applied promptly and the runtime must never be out of support. Nullable-reference and async-correctness warnings promoted to errors in `.editorconfig` — a defect control that must not be relaxed |
| **Performance considerations** | Allocation discipline is a hot-path requirement; per-chunk allocation during streaming would breach NFR-PERF-005. Server GC configuration and allocation profiling are operational concerns, not micro-optimizations |
| **Cross references** | [ADR-0003](../03-adr/ADR-0003-aspnet-core-9.md), [ADR-0010](../03-adr/ADR-0010-gateway-hot-path.md) |

### 3.2 C#

| Field | Value |
| --- | --- |
| **Purpose** | Implementation language |
| **Why chosen** | Bound to the runtime selection |
| **Version** | 13 with .NET 9; 14 with .NET 10 |
| **Support lifecycle** | Tied to the runtime |
| **Risks** | New language features adopted for novelty rather than clarity |
| **Upgrade strategy** | `LangVersion` follows the runtime; feature adoption is a coding-standards decision |
| **Security considerations** | Nullable reference types treated as errors prevents a defect class the domain model relies on |
| **Cross references** | [`coding-standards.md`](coding-standards.md) |

---

## 4. Framework and hosting

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.AspNetCore.App` *(shared framework)* | Web framework, hosting, routing, DI | Runtime major | MIT | 🟢 | Framework reference, not a package reference |
| `Microsoft.Extensions.Hosting` | Worker host generic hosting | Runtime major | MIT | 🟢 | Worker entry point |
| `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | SignalR backplane | Runtime major | MIT | 🟢 | [ADR-0015](../03-adr/ADR-0015-signalr.md) |
| `Microsoft.AspNetCore.OpenApi` | OpenAPI document generation | Runtime major | MIT | 🟢 | Specification must stay in sync — FR-API-012 |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | Bearer token validation | Runtime major | MIT | 🟢 | [ADR-0007](../03-adr/ADR-0007-authentication-strategy.md) |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | OAuth2 / OIDC | Runtime major | MIT | 🟢 | Google, Microsoft |
| `Microsoft.AspNetCore.DataProtection` | Key management for framework-protected payloads | Runtime major | MIT | 🟡 | **Distinct from ADR-0008 credential encryption** — must not be confused |
| `Microsoft.AspNetCore.HealthChecks` | Liveness and readiness | Runtime major | MIT | 🟢 | NFR-OBS-005; gates rolling deployment |

**Note on `DataProtection`.** It protects framework payloads such as antiforgery tokens.
It is **not** the mechanism protecting Provider Credentials — that is envelope encryption
under [ADR-0008](../03-adr/ADR-0008-credential-encryption.md) with a separate key
hierarchy, as NFR-SEC-003 requires. Conflating them would silently weaken the platform's
highest-value protection.

---

## 5. Data access

### 5.1 Entity Framework Core

| Field | Value |
| --- | --- |
| **Purpose** | Command-side persistence, migrations, and — decisively — **interceptors** |
| **Why chosen** | Interceptors guarantee cross-cutting coverage structurally. The tenant interceptor is a *security control* (NFR-SEC-007), and a security control developers must remember to apply is not a control. A micro-ORM would require manual tenant handling on every query — the exact failure mode ADR-0005 was written to exclude |
| **Alternatives considered** | Dapper — faster and more explicit, loses interceptors and unit of work. Raw ADO.NET — same objection, more severely. Full repository abstraction over EF — re-implements EF's own abstractions |
| **Version** | Matching runtime major |
| **Support lifecycle** | Tied to the runtime |
| **Risks** | Inefficient generated queries; interceptor behaviour is non-local and harder to debug; direct SQL for analytics bypasses global filters |
| **Upgrade strategy** | With the runtime major, as a single planned change |
| **Replacement strategy** | Not anticipated. Analytics moving to a separate store (ADR-0004 §9) would *reduce* EF's scope and simplify the codebase to a single idiom |
| **Security considerations** | **The tenant interceptor must set the session variable at connection checkout and clear it at return.** A pooled connection returned with a stale tenant is a cross-tenant exposure — this makes pooling mode a security decision (DD-2) |
| **Performance considerations** | No lazy loading. Explicit projection. Direct SQL permitted for analytics aggregation only. **EF is not in the Gateway hot path at all** (ADR-0010) |
| **Cross references** | [ADR-0023](../03-adr/ADR-0023-persistence-ef-core.md), [ADR-0005](../03-adr/ADR-0005-multi-tenant-strategy.md) |

| Package | Purpose | Version | Licence | Risk |
| --- | --- | --- | --- | --- |
| `Microsoft.EntityFrameworkCore` | Core ORM | Runtime major | MIT | 🟢 |
| `Microsoft.EntityFrameworkCore.Design` | Migration tooling *(build-time only)* | Runtime major | MIT | 🟢 |
| `Microsoft.EntityFrameworkCore.Relational` | Relational abstractions | Runtime major | MIT | 🟢 |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL provider | Matching EF major | PostgreSQL | 🟢 |
| `Npgsql` | Underlying data provider | Matching | PostgreSQL | 🟢 |

**Npgsql lifecycle note.** Npgsql versions track EF Core majors closely. A runtime upgrade
therefore moves three packages together — runtime, EF Core, Npgsql — which is acceptable
because they are one coordinated release train, and is the exception to "one major
upgrade at a time."

### 5.2 Redis client

| Field | Value |
| --- | --- |
| **Purpose** | Cache, atomic counters, streams, SignalR backplane |
| **Why chosen** | The mature .NET client; multiplexed connection model suits the hot path's sub-millisecond requirement |
| **Alternatives considered** | Custom client — no reason. Higher-level caching abstractions — hide the atomic operations that quota and budget enforcement depend on |
| **Version** | 2.x |
| **Support lifecycle** | Rolling; active maintenance |
| **Risks** | Connection multiplexer misconfiguration causes latency outliers; client behaviour during failover must be tested, not assumed |
| **Upgrade strategy** | Minor and patch batched; major on review |
| **Replacement strategy** | Straightforward — the client is confined to the caching and counter infrastructure. **Valkey is protocol-compatible**, so a server change does not require a client change |
| **Security considerations** | TLS in transit; credentials from configuration, never source |
| **Performance considerations** | Charged against NFR-PERF-007/008. Connection pooling and multiplexer sharing matter more than most tuning |
| **Cross references** | [ADR-0006](../03-adr/ADR-0006-redis.md), [`infrastructure-technologies.md`](infrastructure-technologies.md) §4 |

| Package | Purpose | Version | Licence | Risk |
| --- | --- | --- | --- | --- |
| `StackExchange.Redis` | Redis and Valkey client | 2.x | MIT | 🟢 |

---

## 6. Background processing

### 6.1 Hangfire

| Field | Value |
| --- | --- |
| **Purpose** | Background and scheduled work: batch persistence, cost calculation, projections, outbox relay, catalog refresh, health probing, notifications, retention, reconciliation |
| **Why chosen** | PostgreSQL storage adds no infrastructure dependency (NFR-PORT-002); built-in retry, scheduling, and an operational dashboard meaningful to the P-02 persona; fits a mixed fire-and-forget plus scheduled workload |
| **Alternatives considered** | Hosted services — no retry, scheduling, or visibility. Quartz.NET — strong scheduling, weaker queueing. Broker-backed queue — better ceiling, more operational surface. Cloud job service — violates NFR-PORT-002 |
| **Version** | 1.8.x |
| **Support lifecycle** | Rolling; actively maintained |
| **Risks** | **LGPL v3 licence** — obligations in a redistributed self-hosted product need legal review (TR-4/TD-3). PostgreSQL storage polls, adding constant load to the expected write bottleneck. Lower throughput ceiling than a broker |
| **Upgrade strategy** | Minor and patch batched; major on review |
| **Replacement strategy** | If a durable log is introduced for ADR-0011 ingestion or ADR-0013 extraction transport, moving job queueing onto it **reduces** total operational surface. These three decisions should be evaluated together |
| **Security considerations** | **The dashboard is an administrative surface** — authenticated, authorized, audited, and never publicly routed. Job payloads may be sensitive in aggregate |
| **Performance considerations** | Runs in a **separate host** so batch work never competes with the Gateway. Named queues with dedicated allocation; ingestion is protected from every other job class |
| **Cross references** | [ADR-0014](../03-adr/ADR-0014-hangfire.md) |

| Package | Purpose | Version | Licence | Risk |
| --- | --- | --- | --- | --- |
| `Hangfire.Core` | Job framework | 1.8.x | **LGPL v3** | 🟡 |
| `Hangfire.AspNetCore` | Host integration, dashboard | 1.8.x | **LGPL v3** | 🟡 |
| `Hangfire.PostgreSql` | PostgreSQL storage *(community)* | 1.20.x | LGPL v3 | 🟡 |

> **`Hangfire.PostgreSql` is community-maintained**, not maintained by the Hangfire
> authors. It is the storage provider for the entire background processing tier. Its
> maintenance health should be reviewed before committing, and it belongs on the
> long-term-maintenance watch list in §12.

---

## 7. Validation, mapping, resilience

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `FluentValidation` | Request and command validation | 11.x / 12.x | Apache 2.0 | 🟢 | Pipeline position 4 ([ADR-0012](../03-adr/ADR-0012-cqrs-dispatcher.md)); every command handler must have one (AT-6) |
| `FluentValidation.DependencyInjectionExtensions` | Registration | Matching | Apache 2.0 | 🟢 | |
| `Mapster` | Object mapping | 7.x | MIT | 🟢 | **Chosen over AutoMapper, which moved to a commercial model.** Compile-time generation avoids runtime reflection cost |
| `Mapster.DependencyInjection` | Registration | Matching | MIT | 🟢 | |
| `Polly` | Retry, circuit breaker, timeout | 8.x | BSD-3 | 🟢 | Underpins ADR-0009 resilience. **Circuit state must be shared in Redis**, not per-instance (GD-004) |
| `Microsoft.Extensions.Http.Resilience` | HTTP client resilience | Runtime major | MIT | 🟢 | Wraps Polly for provider calls |

**No mediator package.** [ADR-0012](../03-adr/ADR-0012-cqrs-dispatcher.md) decided against
MediatR on licensing grounds; dispatch is in-house. This is deliberate and is the reason
no mediator appears in this inventory.

---

## 8. Security and cryptography

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.AspNetCore.Cryptography.KeyDerivation` | Password hashing | Runtime major | MIT | 🟢 | Memory-hard algorithm required; parameters are a security decision |
| `Otp.NET` | TOTP multi-factor | 1.x | MIT | 🟡 | Small library; **evaluate maintenance health** — §12 |
| `Azure.Security.KeyVault.Keys` | Key custodian — hosted deployment | 4.x | MIT | 🟡 | **Behind a port.** Portable custodian is the CI default (AU-010) |
| `Azure.Identity` | Azure authentication | 1.x | MIT | 🟡 | Hosted deployment only |
| `System.Security.Cryptography` *(framework)* | Envelope encryption primitives | Runtime | MIT | 🟢 | Framework, not a package |

**Credential material must not be a plain string type.** NFR-SEC-005 forbids credentials
appearing in logs, traces, or error output, and the reliable way to achieve that is
construction — a type that cannot be interpolated into a log message — rather than
scrubbing after the fact.

---

## 9. Observability

| Package | Purpose | Version | Licence | Risk |
| --- | --- | --- | --- | --- |
| `OpenTelemetry` | Core SDK | 1.x | Apache 2.0 | 🟢 |
| `OpenTelemetry.Extensions.Hosting` | Host integration | 1.x | Apache 2.0 | 🟢 |
| `OpenTelemetry.Instrumentation.AspNetCore` | Request instrumentation | 1.x | Apache 2.0 | 🟢 |
| `OpenTelemetry.Instrumentation.Http` | Outbound instrumentation | 1.x | Apache 2.0 | 🟢 |
| `OpenTelemetry.Instrumentation.EntityFrameworkCore` | Query instrumentation | 1.x | Apache 2.0 | 🟢 |
| `OpenTelemetry.Instrumentation.StackExchangeRedis` | Redis instrumentation | 1.x | Apache 2.0 | 🟡 |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | OTLP export | 1.x | Apache 2.0 | 🟢 |
| `Serilog.AspNetCore` | Structured logging | 8.x / 9.x | Apache 2.0 | 🟢 |
| `Serilog.Sinks.Console` | Console sink — containers log to stdout | 6.x | Apache 2.0 | 🟢 |

**Vendor-neutral by decision.** [ADR-0020](../03-adr/ADR-0020-observability.md) forbids
instrumenting with a specific observability vendor's SDK, because a self-hosted customer
must be able to point telemetry at their own collector.

**Redis instrumentation is marked 🟡** because it sits in the hot path — its overhead must
be measured against NFR-PERF-001 rather than assumed negligible.

---

## 10. Object storage and external integration

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `Azure.Storage.Blobs` | Object storage — hosted | 12.x | MIT | 🟡 | **Behind the ADR-0017 port.** AT-12 asserts no direct reference outside the adapter |
| `AWSSDK.S3` *or* `Minio` | S3-compatible — portable | Current | Apache 2.0 | 🟡 | **The CI and development default** — this is what keeps NFR-PORT-002 true |
| `Stripe.net` *(provisional)* | Payment processing | Current | Apache 2.0 | 🟡 | Pending TD-4. Worker-only; never in the request path |
| `MailKit` | SMTP delivery | 4.x | MIT | 🟢 | Provider-neutral; self-hostable |

**No AI provider SDKs are listed deliberately.** [ADR-0009](../03-adr/ADR-0009-ai-provider-abstraction.md)
confines provider integration to adapters, and using each vendor's SDK would mean four
independently-versioned dependencies with divergent release cadences in the most critical
path in the system. **Direct HTTP against documented provider APIs is preferred**, using
the framework's HTTP client with `Microsoft.Extensions.Http.Resilience`. This is a
deliberate choice to accept more implementation work in exchange for fewer moving parts
where it matters most.

---

## 11. Testing and build

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `xunit` | Test framework | 2.x / 3.x | Apache 2.0 | 🟢 | |
| `xunit.runner.visualstudio` | Test discovery | Matching | Apache 2.0 | 🟢 | |
| `Microsoft.NET.Test.Sdk` | Test host | Current | MIT | 🟢 | |
| `NSubstitute` | Mocking | 5.x | BSD-3 | 🟢 | **Chosen over Moq**, which introduced a contentious telemetry dependency |
| `Shouldly` *or* `FluentAssertions` **v7** | Assertions | See note | Apache 2.0 | 🟡 | **FluentAssertions v8+ requires a commercial licence.** Pin to v7 or use Shouldly — TR-11 |
| `Testcontainers.PostgreSql` | Integration test database | 3.x / 4.x | MIT | 🟢 | Real PostgreSQL — essential for testing row-level security |
| `Testcontainers.Redis` | Integration test cache | Matching | MIT | 🟢 | |
| `Microsoft.AspNetCore.Mvc.Testing` | Functional test host | Runtime major | MIT | 🟢 | |
| `NetArchTest.Rules` *or* `ArchUnitNET` | **Architecture tests AT-1 … AT-12** | Current | MIT / Apache 2.0 | 🟡 | **Load-bearing** — ADR-0001 and ADR-0002 are conventions without this. §12 |
| `Respawn` | Test database reset | 6.x | MIT | 🟢 | |
| `Bogus` | Test data generation | 35.x | MIT | 🟢 | |
| `coverlet.collector` | Coverage | 6.x | MIT | 🟢 | NFR-MAINT-004 |

> **`FluentAssertions` licence change — TR-11.** Version 8 and later require a paid
> licence for commercial use; version 7 remains Apache 2.0. Verify current terms before
> adoption. Shouldly is a permissively-licensed alternative with no such constraint, and
> is the lower-risk default for a commercial product.

> **The architecture test package is disproportionately important.** It enforces AT-1 …
> AT-12, which is what makes [ADR-0001](../03-adr/ADR-0001-clean-architecture.md) and
> [ADR-0002](../03-adr/ADR-0002-modular-monolith.md) real rather than aspirational. Both
> candidates are relatively small community projects. This is a genuine concentration of
> risk in a low-profile dependency — see §12.

---

## 12. Packages requiring long-term maintenance attention

Packages where our reliance exceeds the package's apparent maintenance capacity, or where
licence or supply risk is material. Each needs a named owner and periodic review.

| Package | Why it needs attention | Consequence if abandoned | Mitigation |
| --- | --- | --- | --- |
| **`NetArchTest` / `ArchUnitNET`** | Small community project; **enforces the boundaries that make ADR-0001 and ADR-0002 real** | Module and layer boundaries silently become advisory; extraction premise erodes | Rules are simple enough to reimplement over reflection APIs; the *rules* are the asset, not the library |
| **`Hangfire.PostgreSql`** | Community-maintained storage provider for the entire background tier | Background processing needs a new storage provider or a broker migration | Migration path to a broker already anticipated (ADR-0014 §9) |
| **Hangfire (LGPL v3)** | Copyleft licence in a redistributed product | Legal exposure at v2.1 self-hosted | TD-3 legal review before v2.1 |
| **`Otp.NET`** | Small library on the authentication path | MFA implementation must be replaced | TOTP is a documented standard; the algorithm is straightforward to implement or substitute |
| **`FluentAssertions`** | v8+ commercial licence | Licence cost or forced migration across every test | Pin v7 or adopt Shouldly now, before thousands of assertions exist |
| **`Azure.Storage.Blobs` / `Azure.Identity`** | Cloud-coupled; portability risk if they leak beyond the adapter | NFR-PORT-002 violated; v2.1 becomes a re-architecture | Behind ADR-0017 port; AT-12; portable implementation is the CI default |
| **AI provider HTTP integration** *(in-house)* | Not a package — **code we own in the most critical path** | Ongoing maintenance as provider APIs change | Deliberate. Narrow port (ADR-0009); adapters are small and independently testable |
| **In-house CQRS dispatcher** | Not a package — **code we own in every handler signature** | Maintenance burden if it accretes features | ADR-0012 fixes its scope; extension requires architecture review |

**The pattern worth noticing:** the highest-attention items are not the large frameworks.
They are small libraries carrying disproportionate architectural weight, and code we chose
to own. Both are defensible choices; neither is free.

---

## 13. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Runtime out of support (TR-1) | **Critical** | Certain if unaddressed | Decision TD-1 |
| R-2 | Architecture test library abandoned, boundaries erode | High | Low | Rules are the asset; reimplementable |
| R-3 | Hangfire LGPL obligations block redistribution | Medium | Medium | TD-3 legal review |
| R-4 | Azure SDK usage leaks outside the storage adapter | High | Medium | AT-12; portable default in CI |
| R-5 | A dependency changes licence mid-project | Medium | High | Minimal surface; licence re-checked each release |
| R-6 | In-house provider HTTP integration is more work than anticipated | Medium | Medium | Accepted; narrow port limits scope per adapter |
| R-7 | Transitive vulnerability in the dependency tree | Medium | High | Build-gating scan (NFR-SEC-011); lockfiles |
| R-8 | Test assertion library licence change discovered after wide adoption | Low | Medium | Decide before writing tests, not after |

---

## 14. Cross references

| Document | Relationship |
| --- | --- |
| [`technology-stack.md`](technology-stack.md) | Master inventory; the runtime finding |
| [`frontend-technologies.md`](frontend-technologies.md) | npm inventory |
| [`infrastructure-technologies.md`](infrastructure-technologies.md) | PostgreSQL, Redis/Valkey servers |
| [`dependency-policy.md`](dependency-policy.md) | How these are added and reviewed |
| [`package-policy.md`](package-policy.md) | Central version management |
| [`support-lifecycle.md`](support-lifecycle.md) | End-of-support calendar |
| [`coding-standards.md`](coding-standards.md) | C# conventions |
| [`../03-adr/`](../03-adr/) | Decisions these packages implement |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SEC, NFR-PERF, NFR-PORT, NFR-MAINT |
