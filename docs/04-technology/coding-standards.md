# Coding Standards

| Field | Value |
| --- | --- |
| Document | Coding Standards |
| Version | 1.0 |
| Status | Draft — pending engineering review |
| Owner | Engineering |
| Last updated | 2026-07-30 |
| Audience | All engineers |
| Phase | 4 — Technology Standards |

---

## 1. Purpose

This document defines how code is written in MaintOrbit AI. It exists to remove
recurring arguments, to make review focus on substance rather than style, and — for a
subset of rules — to prevent defect classes that matter.

**Not all rules here carry equal weight.** They are marked:

| Marker | Meaning |
| --- | --- |
| 🔒 | **Enforced mechanically** — a build gate or analyzer fails. Not negotiable |
| ⚠️ | **Correctness or security rule** — a review gate. Deviation requires a recorded reason |
| 📐 | **Convention** — consistency for its own sake. Follow it; do not argue about it |

Most rules are 📐. The 🔒 and ⚠️ rules are the ones worth reading carefully.

---

## 2. Scope

**In scope:** C#, TypeScript, SQL, naming, testing, documentation, error handling,
security-sensitive coding rules.

**Out of scope:** which packages to use
([`backend-technologies.md`](backend-technologies.md),
[`frontend-technologies.md`](frontend-technologies.md)), why architectural decisions were
made ([`../03-adr/`](../03-adr/)), Git workflow (`README.md` and
`docs/05-development/git-workflow/`).

---

## 3. Universal rules

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| U-1 | Terminology matches [`../01-product/glossary.md`](../01-product/glossary.md) exactly, with no synonyms and no abbreviations of platform terms | ⚠️ | NFR-USE-008, FR-X-007. `ProvConn` and `usg_rec` are prohibited |
| U-2 | All timestamps are stored in UTC | ⚠️ | FR-X-003. Display conversion happens at the edge only |
| U-3 | No `DateTime.Now` / `DateTime.UtcNow` / `new Date()` outside the time abstraction | 🔒 | AT-9. Makes time testable |
| U-4 | Files end with a newline; no trailing whitespace; UTF-8; LF endings | 🔒 | `.editorconfig` |
| U-5 | No commented-out code | 📐 | Version control exists |
| U-6 | No secrets in source, ever | 🔒 | NFR-SEC-012; build-gating scan |
| U-7 | Comments explain *why*, not *what* | 📐 | The code says what |
| U-8 | Public behaviour changes require a test | ⚠️ | |

---

## 4. C#

### 4.1 Mechanically enforced

Set in `.editorconfig`; the build treats these as errors.

| # | Rule | Marker |
| --- | --- | --- |
| C-1 | Nullable reference types enabled; **CS8600, CS8602, CS8618 are errors** | 🔒 |
| C-2 | **CS4014 (unawaited async) is an error** | 🔒 |
| C-3 | File-scoped namespaces | 🔒 |
| C-4 | `using` directives outside the namespace, `System` first | 🔒 |
| C-5 | Braces always, even for single statements | 🔒 |
| C-6 | Interfaces prefixed `I`; private fields prefixed `_camelCase` | 🔒 |
| C-7 | 4-space indent, 120-column guide | 🔒 |

**C-1 and C-2 must not be downgraded to warnings.** Nullability errors prevent a defect
class the domain model depends on, and an unawaited async call in a handler silently
discards work — including, potentially, an audit write.

### 4.2 Domain modelling

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| C-8 | **Entities are never constructible in an invalid state.** Creation via a factory method returning a result, not a public constructor | ⚠️ | An invalid entity should be unrepresentable. AT-8 |
| C-9 | **State changes go through the aggregate root**, never a child entity directly | ⚠️ | Prevents inconsistent partial updates |
| C-10 | **Value objects for meaningful values** — money, token counts, identifiers, email addresses | ⚠️ | Prevents confusing input and output token counts, or two identifier types |
| C-11 | **Money is a value object over `decimal`**, never `float` or `double` | 🔒 | NFR-DATA-003's 2% tolerance cannot survive representation error |
| C-12 | Domain events raised inside the aggregate, dispatched **after** commit | ⚠️ | An event for uncommitted work is a lie |
| C-13 | Expected failures return a result; exceptions are for the genuinely exceptional | ⚠️ | Makes failure visible in signatures |
| C-14 | No lazy loading; all loading explicit | 🔒 | Prevents surprise queries in latency-budgeted paths |

### 4.3 Async

| # | Rule | Marker |
| --- | --- | --- |
| C-15 | Async methods suffixed `Async`; return `Task` or `ValueTask`, never `void` except event handlers | 📐 |
| C-16 | `CancellationToken` accepted and propagated on every async public method | ⚠️ |
| C-17 | No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` | 🔒 |
| C-18 | `ConfigureAwait` not required in ASP.NET Core; do not add it reflexively | 📐 |

**C-16 matters for FR-GW-025 and FR-CHAT-013** — request cancellation must propagate to
the provider, and a token that stops halfway through the call chain makes that impossible.

### 4.4 Hot-path rules

Apply **only** to Gateway hot-path code (ADR-0010). They are unusual and deliberately so.

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| C-19 | **No synchronous relational access** | 🔒 | ADR-0010; the budget does not permit it |
| C-20 | **No per-chunk allocation during streaming** | ⚠️ | NFR-PERF-005's 5 ms per-chunk budget |
| C-21 | **No per-chunk logging** | ⚠️ | Same |
| C-22 | Every outbound call has an explicit timeout | 🔒 | NFR-AVAIL-009; no unbounded wait |
| C-23 | Every dependency is classified fail-open or fail-closed **in the type system** | ⚠️ | ADR-0021; a new dependency must state its category |

### 4.5 Security-sensitive

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| C-24 | **Credential material is never a plain `string`** — use a type that cannot be interpolated into a log message | ⚠️ | NFR-SEC-005. Construction, not scrubbing, is the reliable control |
| C-25 | **No code path returns a Provider Credential in plaintext to a caller** | ⚠️ | FR-PROV-004 is satisfied structurally — there is no "reveal" operation to misconfigure |
| C-26 | Every tenant-scoped entity carries the tenant discriminator | 🔒 | AT-4 |
| C-27 | Repositories are invoked only inside dispatcher-mediated handlers | 🔒 | AT-10; otherwise the pipeline's audit and authorization guarantees are bypassable |
| C-28 | Direct SQL only in Analytics projections | ⚠️ | AT rule; broadening it broadens the tenant-safety review surface |
| C-29 | SignalR hub group names derive from server-side tenant context, never client input | ⚠️ | Cross-tenant subscription vector |
| C-30 | Every hub method carries an authorization requirement | 🔒 | AT-11 |

### 4.6 Naming

| Element | Convention |
| --- | --- |
| Projects | `MaintOrbit.<Layer>` |
| Namespaces | Mirror the folder path |
| Classes, methods, properties | PascalCase |
| Interfaces | `I` prefix |
| Private fields | `_camelCase` |
| Async methods | `Async` suffix |
| Commands / queries | `<Verb><Noun>Command` / `<Verb><Noun>Query` |
| Handlers | `<Message>Handler` |
| Validators | `<Message>Validator` |
| DTOs | `<Noun>Request` / `<Noun>Response` |
| Domain events | Past tense + `DomainEvent` |
| Integration events | Past tense + `IntegrationEvent` |
| EF configurations | `<Entity>Configuration` |

---

## 5. TypeScript and React

### 5.1 Language

| # | Rule | Marker |
| --- | --- | --- |
| T-1 | `strict` mode enabled; **no `any`** without a recorded reason | 🔒 |
| T-2 | No non-null assertion `!` without a recorded reason | ⚠️ |
| T-3 | Types derived from Zod schemas, never declared separately alongside them | ⚠️ |
| T-4 | `const` by default; `let` only when reassigned; never `var` | 🔒 |
| T-5 | Named exports; default exports only where a framework requires them | 📐 |
| T-6 | 2-space indent, single quotes | 🔒 |

**T-3 prevents the most common drift in this codebase:** a Zod schema and a TypeScript
interface describing the same shape, edited independently until they disagree.

### 5.2 React and Next.js

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| T-7 | **Server components by default**; `'use client'` is a deliberate exception | ⚠️ | FD-001; bundle budget is a build gate |
| T-8 | **Server data is never copied into Redux** | ⚠️ | FD-003. Assessed as the highest-likelihood frontend defect |
| T-9 | Query keys include the Company identifier | ⚠️ | FD-005; prevents cross-Company cache reuse after a session change |
| T-10 | Real-time messages **invalidate**; they never carry data | ⚠️ | FD-004 |
| T-11 | Permission gating is on **permissions**, not on a closed role enumeration | ⚠️ | Otherwise FR-PERM-006 custom roles become a rewrite of every gated surface |
| T-12 | Never re-implement a shared pattern — table, chart, form field, empty state, error state | 📐 | Accessibility lives in the patterns (FD-009) |
| T-13 | shadcn/ui primitives are **not edited**; customize via tokens | ⚠️ | FD-008; keeps upstream updates viable |
| T-14 | User-facing strings stay out of component bodies | 📐 | Cheap now; expensive to retrofit for FR-X-008 localization |
| T-15 | **Model output is sanitized before rendering** | ⚠️ | Untrusted content; the most direct XSS vector in the console |

### 5.3 Naming

| Element | Convention |
| --- | --- |
| Component files | PascalCase — `ProviderCard.tsx` |
| Hooks | `use` prefix — `useProviderList.ts` |
| Services, utilities | camelCase |
| Types and interfaces | PascalCase, **no `I` prefix** |
| Zod schemas | `<name>Schema` |
| Redux slices | `<domain>Slice.ts` |
| Query keys | Module-scoped tuple — `['providers', 'list', companyId]` |
| Route folders | kebab-case |
| Constants | `SCREAMING_SNAKE_CASE` |

---

## 6. SQL and data

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| S-1 | `snake_case` tables and columns; plural table names | 📐 | Phase 0 convention |
| S-2 | Primary key `id`; foreign key `<singular>_id` | 📐 | |
| S-3 | Indexes `ix_<table>_<cols>`; unique `ux_<table>_<cols>` | 📐 | |
| S-4 | **One schema per module**; no foreign key crosses a module schema | 🔒 | ADR-0002 R-6 |
| S-5 | **Every tenant-scoped table has a row-level security policy** | 🔒 | ADR-0005; a table without one is a leak |
| S-6 | **Migrations are backward-compatible with the previous application version** | ⚠️ | Rolling deployment runs both concurrently; expand-and-contract is mandatory |
| S-7 | Retention by **partition drop**, never mass deletion | ⚠️ | Mass deletion produces bloat and sustained write load |
| S-8 | Ledger tables are time-partitioned | ⚠️ | NFR-SCAL-007/008 |

---

## 7. Error handling

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| E-1 | **Every user-facing error states what happened, why, and what to do next** | ⚠️ | FR-X-001 |
| E-2 | Errors carry structured meaning from origin; never a bare string thrown from depth | ⚠️ | A string cannot be translated at the boundary into actionable guidance |
| E-3 | Provider errors are **normalized, with the original preserved** | ⚠️ | FR-GW-006; both must survive to the caller |
| E-4 | Retry and fallback eligibility is a property of the error **category**, not a per-call decision | ⚠️ | GD-009; keeps resilience deterministic |
| E-5 | Never swallow an exception silently | 🔒 | |
| E-6 | Every permission denial produces an audit event | 🔒 | FR-PERM-004 |
| E-7 | A failure to write an audit or usage record is an **incident** — recorded and alerted | ⚠️ | FR-AUD-011, NFR-DATA-008 |

---

## 8. Testing

| # | Rule | Marker | Rationale |
| --- | --- | --- | --- |
| X-1 | Test method names read `Method_Scenario_ExpectedResult` | 📐 | |
| X-2 | Domain and application logic coverage ≥ 80% | 🔒 | NFR-MAINT-004 — a signal, not a target |
| X-3 | **Every background job has an idempotency test** | ⚠️ | Hangfire retries; a non-idempotent job corrupts the ledger |
| X-4 | **Tenant isolation is verified by test on every build** | 🔒 | NFR-SEC-008 |
| X-5 | **Hot path and dispatcher pipeline are tested against the same authorization and audit expectations** | ⚠️ | ADR-0010 R-3; otherwise the two paths drift |
| X-6 | Integration tests run against real PostgreSQL and Redis via containers | ⚠️ | Row-level security cannot be tested against an in-memory substitute |
| X-7 | **Every fail-open and fail-closed classification is failure-injection tested** | ⚠️ | NFR-AVAIL-015 requires *observed* behaviour, not asserted |
| X-8 | Flaky tests are defects — quarantined and fixed, never retried into passing | ⚠️ | A flaky gate trains people to ignore gates |

**X-2 is marked 🔒 but deserves a caveat.** Coverage measures execution, not correctness.
It is a floor that catches untested code, not evidence that tested code is right.

---

## 9. Definition of done

Per [`../01-product/mission.md`](../01-product/mission.md) §4.9, a feature ships complete
or not at all:

- [ ] Backend implementation with permission enforcement **at execution**
- [ ] Tenant isolation verified by test
- [ ] Audit events emitted for every relevant action
- [ ] Usage metering where applicable
- [ ] Frontend implementation meeting WCAG 2.1 AA
- [ ] Error states implemented per E-1
- [ ] Unit, integration, and functional tests
- [ ] Architecture tests passing
- [ ] API specification updated where the public surface changed
- [ ] User-facing documentation
- [ ] Relevant NFR targets verified under load

---

## 10. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | 🔒 rules are downgraded under delivery pressure | High | **High** | Downgrading requires architecture review and a recorded reason |
| R-2 | ⚠️ rules erode because they depend on reviewer attention | Medium | **High** | Promote to 🔒 wherever a mechanical check is feasible |
| R-3 | Standards grow until nobody reads them | Medium | High | The 🔒/⚠️/📐 marking exists so the important rules stay findable |
| R-4 | Hot-path rules are applied outside the hot path, adding ceremony | Low | Medium | §4.4 is explicitly scoped |
| R-5 | Coverage treated as a correctness measure | Medium | High | Stated in §8; supplement with mutation or property-based testing in the domain over time |

---

## 11. Future considerations

- **Promote ⚠️ rules to 🔒 wherever possible.** Each one converted is a rule that stops
  depending on human attention. T-8 (server data in Redux) and C-24 (credential typing) are
  the highest-value candidates.
- **Mutation testing in the domain layer** would give a better correctness signal than
  coverage, particularly around invariants and cost calculation.
- **A glossary lint rule** would make U-1 self-enforcing rather than review-dependent.
- **Custom roles (v2.0) will test T-11.** Permission-based gating makes it a data change;
  role branching makes it a rewrite.
- **Standards should be revised, not accumulated.** A rule that has never caught a defect
  in a year is a rule to delete.

---

## 12. Cross references

| Document | Relationship |
| --- | --- |
| [`technology-stack.md`](technology-stack.md) | Languages and runtimes these govern |
| [`backend-technologies.md`](backend-technologies.md) | Packages referenced here |
| [`frontend-technologies.md`](frontend-technologies.md) | Packages referenced here |
| [`../03-adr/ADR-0001-clean-architecture.md`](../03-adr/ADR-0001-clean-architecture.md) | Layering these conventions support |
| [`../03-adr/ADR-0010-gateway-hot-path.md`](../03-adr/ADR-0010-gateway-hot-path.md) | §4.4 hot-path rules |
| [`../03-adr/ADR-0021-fail-open-fail-closed.md`](../03-adr/ADR-0021-fail-open-fail-closed.md) | C-23 |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §8 architecture tests |
| [`../01-product/glossary.md`](../01-product/glossary.md) | U-1 normative vocabulary |
| `../../.editorconfig` | Mechanical enforcement of 🔒 rules |
