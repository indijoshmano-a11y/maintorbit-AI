# Engineering Standards

| Field | Value |
| --- | --- |
| Document | Engineering Standards |
| Version | 1.0 |
| Status | Draft — pending engineering review |
| Owner | Engineering |
| Last updated | 2026-07-30 |
| Audience | All engineers |
| Phase | 8 — Development Standards |

---

> ## Relationship to the Phase 4 coding standards
>
> [`../04-technology/coding-standards.md`](../04-technology/coding-standards.md) already
> defines **language-level rules** — C#, TypeScript, SQL syntax, naming, and the
> 🔒 / ⚠️ / 📐 enforcement markers.
>
> **This document does not restate them.** It defines **engineering practice**: how Clean
> Architecture is implemented, how projects are structured, how framework features are used,
> and how code is reviewed and maintained. Where a language rule applies, it is referenced by
> its identifier rather than duplicated — two documents stating the same rule will eventually
> state it differently.
>
> | Concern | Document |
> | --- | --- |
> | Syntax, naming, formatting, language features | **Phase 4** |
> | Architecture implementation, framework use, review, debt | **This document** |

---

## 1. Purpose

This document defines how engineers at MaintOrbit AI build software: the implementation rules
that make the architecture real, the framework conventions that keep it consistent, and the
review and maintenance practices that keep it from eroding.

Its function is to make the decisions in Phases 2–7 **operational**. An architecture that
exists only in documentation is an aspiration; this document is how it reaches the code.

## 2. Scope

**In scope:** general engineering principles, Clean Architecture implementation rules, project
structure, framework conventions for ASP.NET Core and EF Core, API implementation, exception
handling, logging, validation, configuration, dependency injection, performance, security
coding practice, code review, documentation expectations, and technical debt policy.

**Out of scope:** language syntax and naming
([`../04-technology/coding-standards.md`](../04-technology/coding-standards.md)); Git workflow
([`git-workflow.md`](git-workflow.md)); testing
([`testing-strategy.md`](testing-strategy.md)); completion criteria
([`definition-of-done.md`](definition-of-done.md)); CI/CD pipeline definitions.

**This document introduces no new architecture.** Every rule traces to a prior decision.

---

## 3. General engineering principles

| # | Principle | Consequence |
| --- | --- | --- |
| **EP-1** | **Structure over discipline** | Where a rule can be enforced by a type, a test, or a compiler, it is. A rule requiring developers to remember is not a rule |
| **EP-2** | **Boring in the data path** | The Gateway sits in customers' production request paths. Novelty is spent on product surfaces, never there |
| **EP-3** | **Make the safe thing the easy thing** | If the correct pattern is more work than the incorrect one, the incorrect one wins over time |
| **EP-4** | **Fail loudly, fail safely** | Silent degradation is worse than failure because it is discovered later |
| **EP-5** | **Ship the complete slice** | API, permissions, audit, metering, UI, tests, and documentation together |
| **EP-6** | **Write for the reader** | Code is read far more than written; optimize for the engineer debugging it at 2am |
| **EP-7** | **Delete more than you add** | The cheapest code to maintain is the code that does not exist |

**EP-1 is the organizing principle of this entire document.** Wherever a standard can become
an architecture test, an analyzer rule, or a type constraint, it should — and the sections
below note where that is possible but not yet done.

---

## 4. Clean Architecture implementation rules

Per [ADR-0001](../03-adr/ADR-0001-clean-architecture.md). **Dependencies point inward.**

### 4.1 Layer rules

| Layer | May reference | Must never contain |
| --- | --- | --- |
| **Domain** | `Shared` only | EF Core types, HTTP types, provider SDKs, `IServiceProvider` |
| **Application** | `Domain`, `Shared` | Concrete persistence, concrete provider clients, transport concerns |
| **Infrastructure** | `Application`, `Domain`, `Shared` | Business rules, invariant enforcement |
| **Api** | `Application`, `Infrastructure`, `Shared` | **Business logic of any kind** |
| **Shared** | Nothing | Module-specific types |

**Infrastructure depends on Application** because ports are declared there and implemented in
Infrastructure. This inversion is counter-intuitive to engineers expecting dependencies to
follow runtime call order, and it is the most common onboarding question — it is what keeps
the domain testable without a database.

**Enforced by AT-1 and AT-2**, which are build gates.

### 4.2 Module rules

Per [ADR-0002](../03-adr/ADR-0002-modular-monolith.md):

| # | Rule |
| --- | --- |
| **MR-1** | A module references another module's **published contracts only** — never its entities, repositories, or internal services |
| **MR-2** | A module never queries another module's data store, including by join |
| **MR-3** | Cross-module communication is by contract call (synchronous) or integration event (asynchronous) |
| **MR-4** | Integration events are versioned, serializable, and carry **no domain object references** |
| **MR-5** | Shared reference data is duplicated by projection, not joined |
| **MR-6** | A module owns its schema; **no other module holds a foreign key into it** |
| **MR-7** | **The module dependency graph must remain acyclic** |

**Enforced by AT-3 and AT-7.** MR-7 is a hard build failure — a cycle forecloses extraction
permanently, which is the entire premise of the modular monolith.

### 4.3 The one permitted exception

**The Gateway hot path bypasses the dispatcher pipeline** ([ADR-0010](../03-adr/ADR-0010-gateway-hot-path.md)).

| Rule | Statement |
| --- | --- |
| It is **named, bounded, and documented** | Not precedent |
| It implements equivalent guarantees directly | Authorization, audit, tenant scoping |
| **A shared test suite asserts equivalence** with the pipeline | Otherwise the two paths drift |
| **Any new exception requires its own ADR** | |

### 4.4 Domain modelling

| # | Rule | Reference |
| --- | --- | --- |
| DM-1 | Entities are never constructible in an invalid state — factory methods returning results, not public constructors | C-8, AT-8 |
| DM-2 | State changes go through the aggregate root | C-9 |
| DM-3 | Value objects for meaningful values — money, token counts, identifiers | C-10 |
| DM-4 | **Money is a value object over `decimal`, never floating point** | C-11 |
| DM-5 | Domain events raised inside the aggregate, dispatched **after** commit | C-12 |
| DM-6 | Expected failures return results; exceptions are for the genuinely exceptional | C-13 |
| DM-7 | No lazy loading | C-14 |

---

## 5. Project structure conventions

Per the Phase 0 repository structure. **Uniform across all twelve modules** — an engineer who
has worked in one can navigate any other.

```
Domain/Modules/<Module>/
    Entities · ValueObjects · Enums · Events · Errors
    Repositories (interfaces only) · Services · Specifications

Application/Modules/<Module>/
    Commands · Queries · Contracts · Validators
    Mappings · EventHandlers · Interfaces · Jobs

Infrastructure/Modules/<Module>/
    Persistence/Configurations · Persistence/Repositories · Services

Api/Endpoints/<Module>/
```

| Element | Visibility | Rule |
| --- | --- | --- |
| `Contracts/` | **Public across modules** | The only types another module may reference |
| Integration events | **Public across modules** | Versioned; identifiers and primitives only |
| Entities, value objects | **Module-internal** | Never cross a boundary |
| Repositories | **Module-internal** | Never resolved outside the owning module (AT-5) |
| Commands, queries | **Module-internal** | Another module calls a contract, not a handler |

**Empty folders are acceptable** where a module has no instance of a concept. Uniformity of
navigation is worth more than the absence of empty directories.

---

## 6. Naming conventions

Defined in [`../01-product/glossary.md`](../01-product/glossary.md) §11 (normative) and
[`../04-technology/coding-standards.md`](../04-technology/coding-standards.md) §4.6, §5.3, §6.

**The binding rule restated because it is the one most often broken:** platform terms keep
their glossary spelling in every context, adjusted only for the casing convention of the
language. **No abbreviations** — `ProvConn`, `usg_rec`, and similar are prohibited.

| Context | Form |
| --- | --- |
| C# / TypeScript type | `ProviderConnection`, `UsageRecord` |
| Database table, column | `provider_connections`, `usage_records` |
| API path segment | `/provider-connections`, `/usage-records` |
| JSON field | `providerConnectionId`, `usageRecordId` |
| UI label | "Provider connection", "Usage record" |

**Prohibited terms** (glossary §10) apply to code identifiers as well as prose: `User` →
`Employee`, `Organization` → `Company`, unqualified `ApiKey` → `PlatformApiKey` or
`ProviderCredential`.

---

## 7. C# conventions

**Defined in [`../04-technology/coding-standards.md`](../04-technology/coding-standards.md)
§4.** Summarized here by identifier only:

| Group | Rules | Enforcement |
| --- | --- | --- |
| Mechanical | C-1 … C-7 — nullability and async as **errors**, file-scoped namespaces, brace style | 🔒 `.editorconfig` |
| Domain modelling | C-8 … C-14 | ⚠️ Review + AT-8 |
| Async | C-15 … C-18 — `Async` suffix, `CancellationToken` propagation, **no `.Result` or `.Wait()`** | 🔒 / ⚠️ |
| **Hot path** | C-19 … C-23 — **no synchronous relational access**, no per-chunk allocation or logging, explicit timeouts, fail-open/closed in the type system | 🔒 / ⚠️ |
| Security | C-24 … C-30 — credential material never a plain string, no plaintext credential return path, tenant discriminator, repositories only in handlers | ⚠️ / 🔒 |

**Two must never be relaxed:**

- **C-1 and C-2 (nullability and unawaited async as errors).** An unawaited async call in a
  handler silently discards work — potentially an audit write.
- **C-24 (credential material is never a plain `string`).** This is the control that actually
  prevents NFR-SEC-005 violations. Log scrubbing is a second layer, applied after the fact and
  inevitably incomplete.

---

## 8. ASP.NET Core conventions

| # | Rule | Rationale |
| --- | --- | --- |
| **AC-1** | **The `Api` project contains no business logic** — composition, transport, and hubs only | ADR-0001; AT rule |
| **AC-2** | Endpoints are thin: bind, dispatch, return. **No orchestration** | |
| **AC-3** | **Authorization is evaluated in the pipeline at execution, not in endpoint attributes alone** | Otherwise jobs and hub methods bypass it |
| AC-4 | Endpoint attributes provide fast rejection as defence in depth | Not the enforcement point |
| **AC-5** | **Every SignalR hub method carries an authorization requirement** | AT-11 |
| **AC-6** | **Hub group names derive from server-side tenant context only** | Client-named groups are a cross-tenant vector |
| AC-7 | Hubs contain no business logic — they dispatch to the same handlers as any entry point | BD-010 |
| AC-8 | Middleware order is fixed and documented; changing it is a correctness change | |
| **AC-9** | **Health checks distinguish liveness from readiness** | NFR-OBS-005; readiness gates rolling deployment |
| AC-10 | Long-running work is dispatched to the Worker host, never executed in a request | ADR-0014 |
| **AC-11** | **Every outbound call has an explicit timeout** | NFR-AVAIL-009; no unbounded wait |
| AC-12 | Response compression is not applied to streaming paths | Buffering destroys streaming latency |

**AC-6 is easy to get wrong and hard to notice.** Group naming is a security boundary that is
not obvious from the API surface — a client able to name its own group could subscribe across
tenants.

---

## 9. Entity Framework Core conventions

Per [ADR-0023](../03-adr/ADR-0023-persistence-ef-core.md).

| # | Rule | Rationale |
| --- | --- | --- |
| **EF-1** | **The transaction boundary is the command** — one command, one transaction, one commit | |
| **EF-2** | **No distributed transactions across modules** — commit, then publish an integration event | BD-004 |
| **EF-3** | Cross-cutting concerns live in **interceptors**, never in handler code | BD-005 |
| **EF-4** | **The tenant interceptor sets the session variable at checkout and clears it at return** | The highest-risk path in the system |
| EF-5 | Entity configuration is explicit, in `IEntityTypeConfiguration` classes — never by convention alone | |
| **EF-6** | **No lazy loading. Explicit loading and projection only** | C-14 |
| EF-7 | Reads use no-tracking projections | |
| **EF-8** | **Direct SQL is permitted in Analytics projections only**, parameterized, and is a review gate | BD-009 |
| **EF-9** | **Migrations are backward-compatible with the previous application version** | Rolling deployment runs both |
| **EF-10** | **Expand-and-contract for any removal or rename** — three releases, not one | V-17 |
| **EF-11** | **A row-level security policy is created in the same migration as the table** | A table without one is a leak |
| EF-12 | Queries against ledger tables must be time-bounded | Partition pruning |
| EF-13 | No `IQueryable` escapes the Infrastructure layer | Prevents query composition in unexpected places |

**EF-4 deserves particular care.** A pooled connection returned with a stale tenant variable,
then reused by another Company's request, is a **cross-tenant data exposure**. It is not an
application defect — it is an interaction between correct application code and a
correctly-configured pooler, and it presents as an ordinary successful query. **Pooling mode
selection is a security decision, and it is still open (DD-2).**

---

## 10. API implementation guidelines

Per [`../07-api/api-specification.md`](../07-api/api-specification.md).

| # | Rule |
| --- | --- |
| **API-1** | **The tenant is derived server-side from the credential — never from a path, parameter, header, or body** |
| **API-2** | **Cross-tenant references return `404`, never `403`** — `403` confirms existence |
| **API-3** | One error envelope across every endpoint, including the Gateway |
| **API-4** | Errors state what happened, why, and what to do next |
| **API-5** | **Clients branch on `type`, never on `detail`** — so `detail` may be improved freely, and `type` may not change |
| **API-6** | Unknown request fields are **rejected**; unknown response fields must be **tolerated** by clients |
| **API-7** | Monetary values are string-encoded decimals, never JSON numbers |
| **API-8** | Keyset pagination on high-volume collections; offset only on small bounded lists |
| **API-9** | **`Retry-After` on every `429`** |
| **API-10** | **`X-Correlation-Id` returned on every response** |
| **API-11** | Freshness stated on every projection-derived response |
| **API-12** | The machine-readable specification is **generated from or verified against** the implementation |
| **API-13** | **No browser origins on the Gateway** — it discourages API keys in client-side code |

**API-12 is the one that decays silently.** A hand-maintained specification drifts, and a
drifted specification misleads integrators about security behaviour.

---

## 11. Exception handling

| # | Rule | Rationale |
| --- | --- | --- |
| **EX-1** | **Expected failures return a result; exceptions are for the genuinely exceptional** | Makes failure visible in signatures |
| **EX-2** | **Errors carry structured meaning from their origin** | A string thrown from depth cannot be translated at the boundary into actionable guidance |
| **EX-3** | **Never swallow an exception silently** | 🔒 |
| EX-4 | Catch narrowly; never `catch (Exception)` except at a defined boundary | |
| EX-5 | Boundary handlers normalize to the API error envelope | |
| **EX-6** | **Provider errors are normalized, with the original preserved** | Both must survive to the caller (FR-GW-006) |
| **EX-7** | **Retry and fallback eligibility is a property of the error category**, not a per-call decision | Keeps resilience deterministic |
| **EX-8** | **Every permission denial produces an audit event** | Denials are the escalation-attempt signal |
| **EX-9** | **A failure to write an audit or usage record is an incident** — recorded, alerted, reconciled | Fail-open does not mean unnoticed |
| EX-10 | Exceptions never carry credentials, content, or cross-tenant identifiers | |

---

## 12. Logging standards

| # | Rule |
| --- | --- |
| **LG-1** | **Structured logging only** — no interpolated message strings |
| **LG-2** | **Never log credentials, tokens, prompt content, or completion content** |
| **LG-3** | **Absent by construction, not masked after the fact** — credential material is a type that cannot be formatted into a message |
| **LG-4** | **The correlation identifier appears in every log entry** |
| LG-5 | No cross-tenant identifiers |
| LG-6 | Log levels are meaningful: `Error` requires action, `Warning` is a degraded condition, `Information` is a business event, `Debug` is diagnostic |
| **LG-7** | **Application logs may be sampled. Audit Events and Usage Records may not** |
| LG-8 | No per-chunk logging on streaming paths |

**LG-7 is the distinction that silently breaks compliance if got wrong.** An audit trail
implemented as log entries inherits log sampling and log retention — failing NFR-DATA-007
without any error appearing anywhere.

---

## 13. Validation standards

| # | Rule |
| --- | --- |
| **VL-1** | **FluentValidation, in the pipeline, before the transaction opens** |
| **VL-2** | **Every command handler has a validator** — AT-6 |
| **VL-3** | **Allowlist, never denylist** |
| VL-4 | All failures returned together, not one at a time |
| VL-5 | Field paths precise enough to attach to the correct input |
| **VL-6** | **Client-side validation is never the enforcement point** — the server always revalidates |
| VL-7 | Domain invariants are enforced in the domain, not in validators |

**VL-7 draws a line that blurs easily.** A validator checks that input is well-formed. A domain
invariant checks that a state transition is legal. "Email must be present" is validation;
"a Company must always have exactly one Owner" is an invariant, and putting it in a validator
means it is only enforced on the paths that happen to run one.

---

## 14. Configuration management

| # | Rule | Reference |
| --- | --- | --- |
| **CF-1** | **All configuration from the environment; no environment-specific build artifacts** | NFR-PORT-003 |
| **CF-2** | Strongly-typed options, validated at startup — **fail fast on invalid configuration** | |
| **CF-3** | **No secret in source or in an image**; injected at container start | NFR-SEC-012 |
| **CF-4** | **The key-encryption key is never an environment variable in production** — custodian only | SM-c |
| **CF-5** | **Provider Credentials never appear in platform configuration** | They are customer property, encrypted in the database |
| CF-6 | `.env.example` documents structure, never values | |
| CF-7 | Feature flags are configuration, not code branches, and are removed once settled | |

---

## 15. Dependency injection rules

| # | Rule | Rationale |
| --- | --- | --- |
| **DI-1** | **Constructor injection only** — no service location, no `IServiceProvider` in domain or application code | Makes dependencies visible |
| **DI-2** | Registration happens in the composition root, per module | |
| DI-3 | Lifetimes: singleton for stateless, scoped for per-request, transient rarely | |
| **DI-4** | **Never inject a scoped service into a singleton** — captive dependency | Analyzer-detectable |
| **DI-5** | **Background jobs create their own scope** and establish tenant context explicitly | No inbound request to derive it from |
| DI-6 | Ports are registered against their interface, never the concrete type | |
| DI-7 | A constructor with many dependencies is a design signal, not a formatting problem | |

---

## 16. Performance guidelines

| Context | Rule |
| --- | --- |
| **Hot path** | **No synchronous relational access.** No per-chunk allocation. No per-chunk logging. Explicit timeouts. Every dependency classified fail-open or fail-closed |
| Management path | Explicit projection; no lazy loading; time-bounded ledger queries |
| Background | Batched writes; bounded queues; idempotent jobs |
| Frontend | Server components by default; bundle budget enforced |
| **Measurement** | **Optimize against measurement, never intuition** |

**Budgets, not aspirations:**

| Target | Value |
| --- | --- |
| Gateway overhead p50 / p95 / p99 | 15 / 50 / 100 ms |
| First streamed token | ≤ 50 ms |
| Per-chunk overhead | ≤ 5 ms |
| Authentication + authorization | ≤ 10 ms p95 |
| Management API | ≤ 300 ms p95 |
| Analytics, 30-day range | ≤ 3 s p95 |

**Exceeding an allocation is a defect, not a tuning opportunity.** The hot-path budget has no
slack — any new capability requires taking time from an existing stage.

---

## 17. Security coding practices

Per [`../05-security/security-architecture.md`](../05-security/security-architecture.md).

| # | Rule |
| --- | --- |
| **SC-1** | **Deny by default** — no permission grant means refusal |
| **SC-2** | **Authorization evaluated at execution**, in the pipeline |
| **SC-3** | **Never branch on a role name** — evaluate permissions |
| **SC-4** | **Effective permission is role ∩ key scope**, never union |
| **SC-5** | **Every tenant-scoped entity carries the discriminator** — AT-4 |
| **SC-6** | **Credential material is never a plain `string`** |
| **SC-7** | **No code path returns a Provider Credential in plaintext** — to any role |
| **SC-8** | **Parameterized queries always**, including Analytics |
| **SC-9** | **Prompt content is never interpolated into a query, command, or log** |
| **SC-10** | **Model output is sanitized before rendering** |
| **SC-11** | Cryptographically secure RNG for every security value |
| **SC-12** | **Certificate validation is never disabled — including in development** |
| **SC-13** | **No modification or deletion path for audit records exists in code** |
| SC-14 | Idempotency keys are Company-scoped |
| SC-15 | Signed URLs are authorized before issuance; object path is never the authorization |

**SC-7 and SC-13 are satisfied structurally rather than by permission.** There is no "reveal
credential" operation and no audit update operation to misconfigure — the capability does not
exist. That is the difference between a control that can be got wrong and one that cannot.

---

## 18. Code review checklist

Reviewers work through this. Items marked **⚑** block merge.

### Correctness
- [ ] Does it do what the ticket describes, and nothing beyond it?
- [ ] Are edge cases and failure paths handled, not just the happy path?
- [ ] ⚑ Are expected failures returned as results rather than thrown?

### Architecture
- [ ] ⚑ Does it respect layer and module boundaries? *(AT-1, AT-2, AT-3 also check)*
- [ ] ⚑ Do cross-module references use published contracts only?
- [ ] Is business logic in the domain or application layer, not in `Api` or `Infrastructure`?
- [ ] ⚑ Is a new hot-path exception introduced without an ADR?

### Security
- [ ] ⚑ Is authorization evaluated at execution, with correct permission and scope?
- [ ] ⚑ Does every new tenant-scoped table carry the discriminator **and a policy in the same migration**?
- [ ] ⚑ Could any credential or content reach a log, trace, or error message?
- [ ] ⚑ Are queries parameterized?
- [ ] Does the change alter what an actor can do? If so, has the permission matrix been checked?

### Data
- [ ] ⚑ Is the migration backward-compatible with the previous version?
- [ ] ⚑ Is money `decimal`, never floating point?
- [ ] Are ledger queries time-bounded?

### Observability
- [ ] Is the correlation identifier propagated?
- [ ] ⚑ Does a qualifying operation emit an audit event?
- [ ] Are logs structured, with no content or credentials?

### Tests
- [ ] ⚑ Do tests cover the behaviour, not just the lines?
- [ ] ⚑ Is every background job idempotency-tested?
- [ ] Are failure paths tested, not only success?

### Craft
- [ ] Would a new engineer understand this in six months?
- [ ] Is anything here that could be deleted?

**Reviewers should approve or request changes, not both.** A review that lists concerns and
approves anyway teaches authors that the concerns are optional.

---

## 19. Documentation expectations

| Change | Documentation required |
| --- | --- |
| New public API surface | API specification updated **in the same pull request** |
| Architecturally significant decision | **ADR** in `docs/03-adr/` |
| New module or boundary change | Architecture documentation updated |
| Schema change | Database design updated |
| New security control | Security architecture and checklist updated |
| New configuration | `.env.example` structure updated |
| User-visible behaviour | User-facing documentation |
| Non-obvious *why* | A comment — explaining reasoning, not restating the code |

**Documentation lands with the change, not after it.** A pull request that changes the public
API without updating the specification is incomplete, not "documentation pending."

**Comments explain *why*, not *what*.** The code says what it does; a comment that restates it
is maintenance debt that will eventually contradict the code.

---

## 20. Technical debt policy

**Debt is acceptable. Undeclared debt is not.**

| Rule | Statement |
| --- | --- |
| **TD-1** | Deliberate debt is recorded with a **reason, an owner, and a trigger for repayment** |
| **TD-2** | A `TODO` without a tracking reference is deleted or resolved in review |
| **TD-3** | **Security and correctness debt is not accepted** — a known cross-tenant risk or an unenforced audit path is a defect, not debt |
| **TD-4** | Repayment capacity is reserved each cycle rather than requested when convenient |
| **TD-5** | **Disabling an architecture test or a build gate requires architecture review and a recorded reason** |
| TD-6 | Debt is reviewed quarterly; items untouched for a year are either scheduled or accepted permanently and closed |

**TD-5 is the one that matters most.** The architecture tests are what make
[ADR-0001](../03-adr/ADR-0001-clean-architecture.md) and
[ADR-0002](../03-adr/ADR-0002-modular-monolith.md) real rather than advisory. A suppressed test
is a lost boundary, and the erosion is invisible until extraction is attempted years later.

---

## 21. Design decisions

| # | Decision | Rationale |
| --- | --- | --- |
| **ES-1** | **This document defines practice; Phase 4 defines language rules** | Two documents stating the same rule will eventually state it differently |
| **ES-2** | **Rules are enforced mechanically wherever possible** | EP-1; a rule requiring memory is not a rule |
| **ES-3** | **The hot-path exception is bounded and requires an ADR to extend** | Prevents "the Gateway does it" becoming precedent |
| **ES-4** | **Validators check input; the domain enforces invariants** | An invariant in a validator is only enforced on paths that run it |
| **ES-5** | **Documentation lands with the change** | "Documentation pending" never arrives |
| **ES-6** | **Security and correctness debt is not accepted as debt** | It is a defect with a different name |
| **ES-7** | **Blocking review items are marked ⚑** | Separates "must fix" from "consider" |
| **ES-8** | **Reviewers approve or request changes, not both** | Approving with concerns makes concerns optional |

## 22. Trade-offs

| # | Gained | Given up |
| --- | --- | --- |
| T-1 | Two documents each fit for purpose | Engineers must know which covers what |
| T-2 | Mechanical enforcement | CI time; gates to maintain; **a passing test verifies what it was written to verify, not the intent** |
| T-3 | Strict layer and module rules | More ceremony for simple changes |
| T-4 | Result types over exceptions | More verbose signatures |
| T-5 | Interceptors for cross-cutting concerns | Non-local behaviour, harder to trace when debugging |
| T-6 | Comprehensive review checklist | Review takes longer; risk of mechanical box-ticking |
| T-7 | Debt must be declared | Overhead on small compromises |

## 23. Future improvements

- **Promote ⚠️ rules to 🔒 wherever mechanically checkable.** Highest-value candidates:
  credential typing (SC-6), role-name branching (SC-3), and server-data-in-Redux on the
  frontend.
- **A glossary lint rule** would make naming self-enforcing rather than review-dependent.
- **Custom analyzers** for the DI captive-dependency rule and the hot-path allocation rules.
- **Mutation testing in the domain layer** — a better correctness signal than coverage,
  particularly around invariants and cost calculation.
- **An architecture test enumerating elevated-database-role paths**, so unreviewed elevation
  fails the build.
- **Prune annually.** A rule that has caught nothing in a year is a candidate for deletion; a
  standards document that only grows stops being read.

## 24. Cross references

| Document | Relationship |
| --- | --- |
| [`../04-technology/coding-standards.md`](../04-technology/coding-standards.md) | **Language-level rules — not restated here** |
| [`git-workflow.md`](git-workflow.md) | How changes reach `main` |
| [`testing-strategy.md`](testing-strategy.md) | How the rules are verified |
| [`definition-of-done.md`](definition-of-done.md) | Completion criteria |
| [`../03-adr/ADR-0001-clean-architecture.md`](../03-adr/ADR-0001-clean-architecture.md) | §4.1 layer rules |
| [`../03-adr/ADR-0002-modular-monolith.md`](../03-adr/ADR-0002-modular-monolith.md) | §4.2 module rules |
| [`../03-adr/ADR-0010-gateway-hot-path.md`](../03-adr/ADR-0010-gateway-hot-path.md) | §4.3 the exception |
| [`../03-adr/ADR-0023-persistence-ef-core.md`](../03-adr/ADR-0023-persistence-ef-core.md) | §9 EF conventions |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §8 architecture tests AT-1 … AT-12 |
| [`../05-security/security-architecture.md`](../05-security/security-architecture.md) | §17 security practice |
| [`../06-database/database-design.md`](../06-database/database-design.md) | §9 EF and migration rules |
| [`../07-api/api-specification.md`](../07-api/api-specification.md) | §10 API implementation |
| [`../01-product/glossary.md`](../01-product/glossary.md) | **Normative naming** |
| `../../.editorconfig` | Mechanical enforcement |
