# ADR-0012 — Build an in-house CQRS dispatcher instead of adopting MediatR

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0012 |
| **Title** | Build a minimal in-house CQRS dispatcher with a fixed behaviour pipeline |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-004 |
| **Supersedes** | — |

---

## 1. Context

The management path needs a consistent way to execute use cases with cross-cutting
concerns applied uniformly: correlation, tenant context, authorization, validation,
transaction management, outbox dispatch, audit emission, and telemetry.

Applying these per-handler would make coverage a function of developer discipline —
unacceptable when FR-AUD-001 requires an audit event for *every* qualifying operation and
FR-PERM-001 requires authorization at execution.

MediatR is the conventional .NET answer. Its licensing terms changed, and the abstraction
appears in **every handler signature in the system** — the most expensive possible place
to carry exposure to a dependency's commercial terms.

## 2. Problem Statement

How should command and query dispatch with an ordered cross-cutting pipeline be provided,
without embedding a commercially-exposed dependency in every handler signature?

## 3. Decision

**Build a minimal in-house dispatcher.** The required surface is small: a dispatcher, a
handler interface, and an ordered behaviour pipeline.

**The pipeline order is fixed, because ordering is a correctness property:**

| # | Behaviour | Why here |
| --- | --- | --- |
| 1 | Correlation | Everything downstream must log with the identifier |
| 2 | Tenant context | Authorization and data access both depend on it |
| 3 | **Authorization** | Must precede validation, so an unauthorized caller learns nothing about the shape of valid input |
| 4 | **Validation** | Must precede the transaction, so invalid input never opens one |
| 5 | Transaction | Commands only; queries never open a write transaction |
| 6 | Handler | — |
| 7 | **Outbox dispatch** | Must follow commit, so events are never published for rolled-back work |
| 8 | **Audit** | Must observe the handler's actual outcome, including failure |
| 9 | Telemetry | Outermost measurable boundary |

**Scope is fixed at dispatch plus ordered behaviours.** Extending it toward notification
publishing, streaming, or a general mediator pattern requires architecture review — the
failure mode for in-house infrastructure is accretion into an unmaintained framework.

**The Gateway hot path does not use this pipeline** (ADR-0010). That is the only
exception.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Adopt MediatR under its current licence | Pay for the commercial licence | Viable and honest. Rejected because the abstraction is in every handler signature, making future licence changes maximally expensive to escape. The required surface is genuinely small |
| Another third-party mediator library | Wolverine, Brighter, or similar | Larger surface than needed; several carry opinions about messaging and persistence that conflict with ADR-0013; same category of future exposure |
| No dispatcher — direct service calls from endpoints | Simplest | Cross-cutting concerns become per-handler, making audit and authorization coverage dependent on developer discipline. This is precisely what FR-AUD-001 cannot tolerate |
| Middleware-only, no dispatcher | Apply concerns in transport middleware | Concerns would apply at transport rather than execution, failing FR-PERM-001. Background jobs and hub methods would bypass them entirely |

## 5. Pros

- **No commercial exposure in every handler signature.**
- **The surface is small enough to own confidently** — a dispatcher, a handler interface,
  and an ordered chain.
- **The pipeline is ours to order**, and ordering here is load-bearing for correctness
  rather than convenience.
- **Uniform application across entry points** — REST endpoints, SignalR hub methods, and
  background jobs all dispatch the same way, so guarantees do not depend on how a use case
  was invoked.
- Removes ecosystem features we do not need and would otherwise have to resist using.

## 6. Cons

- **Code we own and must test** that would otherwise be free and battle-tested.
- **No ecosystem familiarity.** New engineers who know MediatR must learn ours, and
  external documentation does not apply.
- **Risk of accretion.** In-house infrastructure tends to grow features until it becomes
  an unmaintained framework.
- **Subtle failure modes are ours to discover** — exception propagation through the chain,
  async context flow, and disposal ordering are all places where a mature library has
  already found the bugs.

## 7. Consequences

- **Every command handler must be covered by a validator**, verified by architecture test
  AT-6 — otherwise validation coverage is optional in practice.
- **Every use case is invoked through the dispatcher**, verified by AT-10 (no repository
  invoked outside a dispatcher-mediated handler). Without this, the pipeline's guarantees
  are bypassable.
- **Audit emission is a pipeline concern, not a handler concern.** Handlers may enrich an
  audit event; they do not decide whether one is emitted.
- **Queries do not open write transactions** and do not participate in the outbox. They
  may bypass the domain and read projections directly (BD-002).
- **The pipeline must be tested as infrastructure**, including its failure paths —
  exception propagation, transaction rollback with outbox suppression, and audit emission
  on handler failure.
- **Custom roles (FR-PERM-006, v2.0) affect the authorization behaviour.** It must
  evaluate permissions, not branch on a closed role enumeration.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | The dispatcher accretes features and becomes an unmaintained framework | Medium | Medium | Scope fixed at dispatch plus ordered behaviours; extension requires architecture review |
| R-2 | A subtle defect in exception or transaction handling corrupts state | High | Low | Treated as infrastructure with full failure-path test coverage, not as application code |
| R-3 | Pipeline ordering is changed without recognizing it as a correctness change | High | Medium | Ordering documented here and in the architecture; a test asserts the order |
| R-4 | A use case bypasses the dispatcher, losing audit and authorization | High | Medium | AT-10 build-gating |
| R-5 | Onboarding cost for engineers expecting MediatR | Low | High | Documented in coding standards; the surface is small enough to learn quickly |

## 9. Future Revisions

Revisit if:

- **The dispatcher's maintenance cost becomes material** — measured in defects or
  engineering time, not in discomfort. Adopting a licensed library at that point is a
  reasonable outcome and this ADR would be superseded.
- **Licensing conditions change again** such that a third-party option becomes clearly
  preferable, including a well-maintained permissively-licensed alternative reaching
  maturity.
- **Module extraction begins.** An extracted service might dispatch differently — its
  scope is smaller and its pipeline needs may be lighter.
- **The pipeline needs to differ per module.** Currently one order serves all twelve
  modules; if that stops being true, the design needs revisiting rather than
  special-casing.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.4 pipeline; §8 architecture tests |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-004 |
| [`../02-architecture/request-flow.md`](../02-architecture/request-flow.md) | F-7 management operation sequence |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | The one permitted bypass |
| [`ADR-0013-outbox-eventing.md`](ADR-0013-outbox-eventing.md) | Outbox dispatch behaviour |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Where handlers and ports sit |
| [`ADR-0003-aspnet-core-9.md`](ADR-0003-aspnet-core-9.md) | Ecosystem licensing risk this responds to |
| [`../01-product/product-requirements.md`](../01-product/product-requirements.md) | FR-AUD-001, FR-PERM-001/002 |
