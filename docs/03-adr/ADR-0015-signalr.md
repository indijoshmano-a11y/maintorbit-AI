# ADR-0015 — Use SignalR with a Redis backplane for real-time updates

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0015 |
| **Title** | Use SignalR with a Redis backplane; treat push as invalidation, not as a data channel |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-011, BD-010, FD-004, FD-012 |
| **Supersedes** | — |

---

## 1. Context

FR-API-014 requires real-time updates for usage, cost, and Provider Connection health in
the web console without polling. FR-NOT-008 requires in-application notifications
presented in real time without a page refresh.

The API host runs multiple instances behind a load balancer (ADR-0022), so a change
detected by one instance must reach clients connected to another.

Server-side changes originate in the Worker host — projection updates, budget threshold
crossings, provider health changes — not in the API host that holds the connection.

## 2. Problem Statement

How should server-originated changes reach connected console clients across multiple API
host instances, without polling and without the pushed representation diverging from the
fetched one?

## 3. Decision

**SignalR hubs hosted in the API host, scaled out through a Redis backplane.**

Four rules govern its use:

| Rule | Statement |
| --- | --- |
| **Push is an invalidation signal, not a data channel** | Messages say *what changed*; the client refetches through its normal data path |
| **The console must function without SignalR** | Connection loss degrades to polling with a visible indicator, never to a broken page |
| **Group membership derives from server-side tenant context only** | Never from a client-supplied value |
| **Hubs carry no business logic** | They are transport; they dispatch to the same handlers as any other entry point |

**Every hub method is authorized** with the same permission evaluation as REST, because a
hub is an entry point like any other and FR-PERM-001 requires enforcement at execution.

**Subscriptions are scoped to the current view** — a user on the billing page does not
receive gateway health traffic.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Polling only | Client refetches on an interval | Simplest and most robust, and remains the **fallback**. Rejected as the primary mechanism: FR-API-014 requires no polling, and at 500 Companies with many console users, polling for freshness produces substantial constant load |
| Server-sent events | One-way server push over HTTP | Simpler than SignalR and sufficient for invalidation signals. Rejected because SignalR provides transport negotiation, automatic reconnection, and the backplane abstraction — meaningful given rolling deployment breaks connections routinely |
| Raw WebSockets | Direct protocol use | All of SignalR's problems with none of its solutions — reconnection, fallback transport, and cross-instance distribution would all be hand-built |
| Push full data payloads | Messages carry the changed entity | **Creates two representations of the same entity** — one from fetch, one from push — which will eventually disagree. Also multiplies authorization surface, since each pushed payload must be filtered per recipient |
| Third-party realtime service | Managed push infrastructure | Violates NFR-PORT-002 |

## 5. Pros

- **Meets FR-API-014 and FR-NOT-008** without polling load.
- **The backplane makes multi-instance delivery transparent** — a Worker publishes once
  and every connected client receives it regardless of which instance holds the connection.
- **Push-as-invalidation keeps one representation.** The client's data always comes from
  its normal fetch path, so pushed and fetched views cannot diverge.
- **Authorization is simplified** by push-as-invalidation: the signal carries no data, so
  the refetch applies normal permission filtering. A data-carrying push would need
  per-recipient filtering at publish time.
- **Automatic reconnection** handles the connection churn that rolling deployment produces.
- Reuses Redis, already required for three other roles (ADR-0006).

## 6. Cons

- **Long-lived connections consume host resources** — roughly 2,000 SignalR connections
  per host in the ADR-0022 connection budget, competing with streaming inference for
  memory and sockets.
- **Rolling deployment breaks every connection on the replaced instance**, so reconnection
  quality is a user-visible concern rather than an edge case.
- **Adds a fourth role to Redis**, extending the dependency concentration in ADR-0006.
- **An extra round trip after each change signal**, since the client must refetch.
- **Group naming is a security boundary**, which is not obvious from the API surface and
  is easy to get wrong.

## 7. Consequences

- **Group membership must derive from resolved server-side tenant context.** A defect
  allowing a client to join another Company's group is a cross-tenant exposure — verified
  by architecture test and treated as a security concern, not a correctness one.
- **Every hub method carries an authorization requirement** (AT-11). A hub that skips
  authorization bypasses FR-PERM-001 entirely.
- **The console must never treat the connection as required.** Rendering, navigation, and
  data display must all work with the connection down (FD-012).
- **Connection budgeting is a capacity dimension** in ADR-0022 and
  `scalability-strategy.md` §3.6, with per-Company limits required by NFR-SCAL-010 —
  otherwise one Company can consume connection capacity others need.
- **Reconnection must be non-disruptive.** Users should not perceive deployment.
- **Backplane failure degrades real-time only.** It must not affect inference or
  management operations — which follows from it being a distinct Redis role.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Group membership derived from client input allows cross-tenant subscription | **Critical** | Low | Server-side derivation only; architecture test; security review of hub code |
| R-2 | Connection volume constrains API host density | Medium | Medium | Connection budgeting; per-Company limits; horizontal scaling |
| R-3 | Deployment connection churn produces visible disruption | Medium | High | Automatic reconnection; polling fallback; connection state never blocks rendering |
| R-4 | A hub method ships without authorization | High | Medium | AT-11 build-gating |
| R-5 | Backplane load affects other Redis roles | Medium | Medium | Role separation per ADR-0006 stage 2 |
| R-6 | Invalidation storms during bulk changes overwhelm clients with refetches | Medium | Medium | Batched and debounced invalidation signals; client-side coalescing |

## 9. Future Revisions

Revisit if:

- **Connection volume becomes the binding constraint on API host density.** The likely
  response is separating hub hosting from the Gateway — a natural companion to extracting
  the Gateway (ADR-0002 §9), since the two have opposite resource profiles.
- **Multi-region deployment (v2.1)** requires cross-region message distribution, which the
  current backplane does not address.
- **Push-as-invalidation proves insufficient** for a genuinely high-frequency surface. AI
  Chat streaming already does **not** use SignalR — it uses the streaming HTTP response
  path — and that split should be preserved rather than unified.
- **Mobile clients are added.** Connection lifecycle on mobile networks differs enough to
  warrant reassessment.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-011 |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.8 real-time delivery |
| [`../02-architecture/frontend-architecture-overview.md`](../02-architecture/frontend-architecture-overview.md) | §3.5 client-side real-time; FD-004, FD-012 |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | Backplane role |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Hub authorization and tenant scoping |
| [`ADR-0022-deployment-topology.md`](ADR-0022-deployment-topology.md) | Connection budgeting; rolling deployment churn |
| [`ADR-0024-frontend-stack.md`](ADR-0024-frontend-stack.md) | Client consumption pattern |
| [`../01-product/product-requirements.md`](../01-product/product-requirements.md) | FR-API-014, FR-NOT-008 |
