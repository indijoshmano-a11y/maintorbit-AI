# ADR-0024 — Next.js 15 with a strict split between server state and client state

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0024 |
| **Title** | Next.js 15 App Router, with TanStack Query owning server state and Redux owning client state |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | FD-001 … FD-012 |
| **Supersedes** | — |

---

## 1. Context

The web console serves two populations that are almost different applications sharing an
identity and a shell:

- **Management users** — administrators, finance, compliance — working with dense,
  filterable, permission-scoped data.
- **Every Employee** using AI Chat, which competes directly against consumer AI products
  and where, per `mvp-features.md` §4.4, *"adequate fails."*

Phase 0 selected Next.js 15, TypeScript, Tailwind, shadcn/ui, Redux Toolkit, TanStack
Query, React Hook Form, Zod, Recharts, and TanStack Table.

Redux Toolkit and TanStack Query **overlap in capability**. A project using both without
an explicit rule ends up with server data in Redux, cache invalidation implemented by
hand, and two sources of truth that drift.

## 2. Problem Statement

How should rendering responsibilities divide between server and client, and — the harder
question — which of two overlapping state libraries owns what?

## 3. Decision

### (a) Rendering — server components by default, client components by exception

A component becomes a client component only when it needs interactivity, browser APIs, or
subscription state. This keeps the JavaScript payload proportional to actual
interactivity, which is what makes NFR-PERF-009 (2 s interactive load) achievable on a
data-dense console.

**Permission-gated rendering happens on the server** — a surface an Employee may not
access is never sent to the browser. This is **defence in depth only**; FR-PERM-001
requires enforcement at execution in the backend, and the console must never be the only
gate.

### (b) State — a binding division of responsibility

| State | Owner | Rule |
| --- | --- | --- |
| Data fetched from the API | **TanStack Query** | **Never copied into Redux** |
| Server mutations | **TanStack Query** | Invalidation defined with the mutation |
| Session identity and permissions | **Redux** | Read synchronously by many components |
| Filter and time-range selections | **Redux** | Shared across siblings; drives query keys |
| Chat composition and streaming buffer | **Redux** | Ephemeral, high-frequency, local |
| Notification queue | **Redux** | Push-driven, not fetched |
| User preferences | **Redux**, persisted | Client-owned |

**The single most important rule: server data is never duplicated into Redux.** Doing so
creates two sources of truth and reproduces by hand the cache invalidation TanStack Query
exists to solve.

**Query keys include the Company identifier**, so a session change cannot serve another
Company's cached data from the browser.

### (c) Real-time — push is an invalidation signal, not a data channel

Consistent with ADR-0015. Push messages say *what changed*; the client refetches through
its normal data path. The console must function without the connection, degrading to
polling with a visible indicator.

### (d) Freshness is displayed, not hidden

FR-ANL-008 requires every analytics view to state its freshness. The client cannot compute
projection lag, so the API returns it and the UI displays it.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Single-page application, no server rendering | Client-only React | Larger initial payload on a data-dense console; harder to hit NFR-PERF-009; loses server-side permission gating |
| TanStack Query only, no Redux | One state library | Genuinely tempting. Rejected because chat composition, notification queue, and cross-sibling filter state are not server state and fit awkwardly into a fetching library |
| Redux only, no TanStack Query | One state library | Means hand-rolling caching, revalidation, staleness, and background refetch — the exact problems TanStack Query solves well |
| Redux with RTK Query | Single library covering both concerns | A coherent alternative and arguably simpler. Phase 0 selected TanStack Query; the division in §3(b) achieves the same clarity |
| Push full data payloads over SignalR | Real-time as a data channel | Creates two representations of the same entity that will eventually disagree; multiplies per-recipient authorization surface |

## 5. Pros

- **Payload proportional to interactivity**, making NFR-PERF-009 achievable.
- **One source of truth per category of state**, with an explicit rule for which is which.
- **Push-as-invalidation keeps a single representation** of every entity.
- **Frontend modules mirror backend modules**, so a developer working on Governance touches
  `modules/governance` on both sides and the domain vocabulary stays aligned.
- **Accessibility implemented in shared patterns** — table, chart, form field, empty state,
  error state — is the only durable route to WCAG 2.1 AA (NFR-USE-001) across a large
  surface.
- **Explicit freshness builds trust** with the finance persona rather than hiding eventual
  consistency until someone notices.

## 6. Cons

- **The server/client boundary must be reasoned about constantly** and is easy to get
  wrong in ways that silently inflate the bundle.
- **Two state libraries require an enforced rule**, and the wrong choice is easy to make.
  This is the highest-likelihood risk in this ADR.
- **Client and server validation will drift** — different languages, different schemas.
  No amount of discipline fully prevents it.
- **An extra round trip** after each real-time change signal.
- **Shared patterns constrain surfaces** with genuinely unusual needs.
- Exposing freshness reveals eventual consistency that users might not otherwise notice.

## 7. Consequences

- **Server data in Redux is a review finding**, not a style preference. A lint rule should
  be added if a mechanical check is feasible.
- **Client validation is never the enforcement point.** The server always revalidates, and
  its field errors must be structured well enough to attach to the correct input — an error
  on the wrong field is worse than a generic message.
- **Markdown parsing must tolerate incomplete input as a normal condition.** Streamed
  content arrives mid-token; a strict parser produces visible flicker on every chunk, which
  reads as low quality and undermines Chat's competitive position directly.
- **Chat is a distinct engineering effort, not a page.** It competes against consumer
  products and carries MVP hypothesis 2.
- **Permission gating must be on permissions, not on a closed role enumeration**, or custom
  roles (FR-PERM-006, v2.0) become a rewrite of every gated surface.
- **Chart wrappers must not assume raw data arrives in the browser.** At NFR-SCAL-007
  volume, aggregation happens server-side.
- **Strings should stay out of component bodies** even before localization (FR-X-008,
  v2.0). Retrofitting extraction across a large console is expensive; the discipline is
  cheap now.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Server data leaks into Redux, creating two drifting sources of truth | High | **High** | Explicit rule; review gate; lint rule if mechanically feasible |
| R-2 | AI Chat quality falls short of consumer products, failing MVP hypothesis 2 | **Critical** | Medium | Chat treated as a distinct effort; streaming and rendering quality measured explicitly |
| R-3 | Analytics rendering exceeds NFR-PERF-010 as volume grows | High | Medium | Server-side aggregation; pagination; virtualization; never fetch raw rows for a chart |
| R-4 | WCAG 2.1 AA missed because accessibility was pursued per component | High | Medium | Accessibility lives in shared patterns; automated audit in CI; manual audit before release |
| R-5 | Client and server validation drift, producing accept-then-reject | Medium | **High** | Server authoritative; structured field errors mapped back to inputs |
| R-6 | Server/client boundary misused, inflating the bundle | Medium | High | Bundle size budget enforced in CI (ADR-0019) |
| R-7 | Cached data served across a session change | High | Low | Company-scoped query keys; cache cleared on session change |

## 9. Future Revisions

Revisit when:

- **Localization ships (FR-X-008, v2.0).** If strings were kept out of component bodies,
  this is extraction; if not, it is a large refactor.
- **Custom roles ship (FR-PERM-006, v2.0).** Permission-based gating makes this a data
  change; role-based branching makes it a rewrite of every gated surface.
- **Chat gains attachments (v1.1) or knowledge grounding (v2.0).** Both substantially
  change the composer and the message renderer — grounding in particular adds a citation
  and provenance surface.
- **Analytics outgrows client-side rendering.** Server-rendered or pre-aggregated
  visualizations become necessary.
- **The two-library division proves burdensome in practice.** Consolidating on one is a
  reasonable outcome, and this ADR would be superseded rather than amended.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/frontend-architecture-overview.md`](../02-architecture/frontend-architecture-overview.md) | Full frontend design; FD-001 … FD-012 |
| [`../02-architecture/request-flow.md`](../02-architecture/request-flow.md) | F-4, F-6, F-7, F-8 console-side flows |
| [`ADR-0015-signalr.md`](ADR-0015-signalr.md) | Push-as-invalidation contract |
| [`ADR-0016-rest-api.md`](ADR-0016-rest-api.md) | The API this consumes |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Session handling and permission model |
| [`ADR-0019-github-actions.md`](ADR-0019-github-actions.md) | Bundle budget and accessibility gates |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-04 — the Chat quality bar |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-USE-001 … 012, NFR-PERF-009 … 012 |
