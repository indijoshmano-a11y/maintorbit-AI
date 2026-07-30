# ADR-0016 — REST for management; an OpenAI-compatible interface for the Gateway

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0016 |
| **Title** | Versioned REST for the management API; an OpenAI-compatible interface for Gateway inference |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Product |
| **Implements** | FR-GW-004, FR-GW-005, FR-API-010/011/012 |
| **Supersedes** | — |

---

## 1. Context

The platform exposes two externally-consumed interfaces with genuinely different
purposes:

**The Gateway interface** carries inference traffic. Its consumers are existing customer
applications already integrated with a provider SDK. Migration friction here is the
primary obstacle to the coverage goal — traffic already integrated directly with a
provider migrates only when migration is nearly free.

**The management API** serves the console, the Extension, and customer automation. Its
consumers are the platform's own clients and customer scripts.

The P-03 persona's stated adoption criterion is *"a single base-URL and credential change
to migrate"* and NFR-USE-005 states it as a requirement.

## 2. Problem Statement

What interface style should each surface use, and how should the Gateway interface
minimize migration friction for applications already integrated with a provider?

## 3. Decision

**Two distinct interface decisions.**

### (a) Gateway — OpenAI-compatible at MVP, native interface at v1.1

The Gateway exposes a request interface **compatible with the OpenAI chat completions
API**, so an existing integration migrates by changing base URL and credential only
(FR-GW-004).

A **provider-neutral native interface** follows at v1.1 (FR-GW-005) for new integrations.
Compatibility mode remains permanently as the migration path.

> This is a pragmatic choice, not an endorsement. It creates a tension with the
> neutrality pillar: adopting one provider's shape as the external interface gives that
> provider's model of the world a privileged position. It is accepted because
> **migration friction is the primary obstacle to coverage**, and coverage is what the
> product's value depends on. The mitigation is that the *internal* port is
> provider-neutral (ADR-0009) — no provider is privileged inside the system — and the
> native interface arrives at v1.1.

### (b) Management API — versioned REST over HTTP with JSON

| Property | Decision |
| --- | --- |
| Style | Resource-oriented REST, JSON payloads |
| Versioning | **URL segment** (`/api/v1/`), per Phase 0 conventions |
| Naming | kebab-case paths, plural nouns; camelCase JSON fields and query parameters |
| Errors | Single documented structure across every endpoint (FR-API-011) |
| Specification | Machine-readable, **kept in sync with the implementation** (FR-API-012) |
| Deprecation | Documented policy with a minimum notice period (FR-API-010) |
| Real-time | SignalR, not REST polling (ADR-0015) |

**Endpoint definitions, payload shapes, and error bodies are Phase 4 deliverables** in
`docs/04-api/`. This ADR fixes the style and the constraints, not the surface.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| **Gateway: native interface only, no compatibility mode** | A clean provider-neutral interface from the start | Architecturally cleaner and better for neutrality. **Rejected on product grounds**: every customer would face a rewrite to adopt the platform, directly attacking coverage. The neutrality cost is real and accepted knowingly |
| **Gateway: compatibility with several provider shapes** | Accept OpenAI, Anthropic, and Gemini request shapes | Multiplies the translation surface and the test matrix. The OpenAI shape is the de facto migration standard; supporting three would triple work for diminishing return |
| **Management: GraphQL** | Single flexible query endpoint | Attractive for the console's varied data needs. Rejected: query cost is hard to bound at NFR-SCAL-007 volume, caching is harder, and the permission model must be enforced per-field rather than per-operation — a materially larger authorization surface |
| **Management: gRPC** | Binary, contract-first | Excellent for internal service communication and a candidate after extraction. Poor fit for browser clients and customer scripting, which are the actual consumers |
| **Management: RPC-style HTTP** | Action-oriented endpoints | Simpler for some operations, but loses the uniformity that makes a large API learnable |

## 5. Pros

- **Base-URL-only migration** removes the largest single obstacle to adoption
  (NFR-USE-005), and it is the mechanism by which existing traffic reaches the platform.
- **REST is universally understood**, needs no client library, and is scriptable directly
  by the P-02 and P-05 personas.
- **URL versioning is unambiguous** — the version is visible in every log line and support
  request.
- **Per-operation authorization** is simpler to enforce and audit than per-field.
- **A machine-readable specification** supports client generation (FR-API-015, v1.1) and
  keeps documentation honest.

## 6. Cons

- **The compatibility interface constrains the Gateway's evolution.** Capabilities that do
  not fit the OpenAI shape must be exposed through the native interface or through
  ADR-0009's opaque pass-through.
- **Two Gateway interfaces to maintain** from v1.1 onward, with two test matrices.
- **A neutrality tension**, stated above and worth restating: an external interface shaped
  by one provider is a form of privilege, even with a neutral internal port.
- **REST over-fetches for the console**, which frequently needs composite views — mitigated
  by purpose-built read endpoints rather than by adopting GraphQL.
- **URL versioning encourages whole-API version bumps** rather than granular evolution.

## 7. Consequences

- **The specification must be generated from or verified against the implementation.** A
  hand-maintained specification drifts, and FR-API-012 requires it to stay in sync.
- **Contract tests are required for every public endpoint** (NFR-MAINT-005), because
  external consumers depend on stability.
- **Breaking changes require a new version**; within a version, changes must be
  backward-compatible (NFR-MAINT-008).
- **The error structure must be uniform and must carry structured meaning**, since FR-X-001
  requires every error to state what happened, why, and what to do next — and ADR-0009
  requires both the normalized and original provider error to survive to the caller.
- **Compatibility-mode divergence must be documented.** Where our behaviour differs from
  the provider API being emulated — and it will, for governance rejections, budget
  rejections, and platform errors that have no provider equivalent — the difference must be
  explicit rather than discovered.
- **Purpose-built read endpoints serve the console** rather than forcing it to compose
  many resource calls.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | The emulated provider API changes, breaking compatibility | High | **High** | Compatibility is versioned and pinned to a stated provider API version; divergence documented; the native interface is the long-term path |
| R-2 | Specification drifts from implementation | Medium | High | Generated or verified in CI; contract tests |
| R-3 | Compatibility mode constrains Gateway capabilities | Medium | High | Native interface at v1.1; opaque pass-through (ADR-0009) |
| R-4 | Console over-fetching degrades NFR-PERF-009 | Medium | Medium | Purpose-built read endpoints; server-side aggregation |
| R-5 | Error responses in compatibility mode confuse clients expecting provider semantics | Medium | High | Documented divergence; platform errors clearly identifiable as platform errors |
| R-6 | Whole-API version bumps become disruptive | Low | Medium | Backward-compatible evolution within a version; deprecation policy with notice |

## 9. Future Revisions

Revisit when:

- **The native interface ships (v1.1).** Adoption should be tracked. If it stays low, the
  compatibility interface is the real product interface and the neutrality tension becomes
  permanent rather than transitional — worth acknowledging explicitly rather than
  maintaining a fiction.
- **The emulated provider API changes materially.** Compatibility becomes a versioned
  commitment with its own maintenance cost, and at some point it may need to pin to an
  older version rather than track.
- **Module extraction begins.** Internal service-to-service communication is a separate
  question and gRPC becomes a reasonable candidate there; it does not affect the external
  surface.
- **Embeddings, multimodal, or batch inference arrive.** Each may not fit the chat
  completions shape and may require native-interface-only exposure.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.4 compatibility and native interfaces; §3.9 error taxonomy |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.2 container view |
| [`ADR-0009-ai-provider-abstraction.md`](ADR-0009-ai-provider-abstraction.md) | The neutral internal port, distinct from this external interface |
| [`ADR-0015-signalr.md`](ADR-0015-signalr.md) | Real-time, deliberately not REST polling |
| [`ADR-0024-frontend-stack.md`](ADR-0024-frontend-stack.md) | Primary consumer of the management API |
| [`ADR-0025-extension-auth.md`](ADR-0025-extension-auth.md) | Second consumer |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-USE-005, NFR-MAINT-005/008 |
| [`../01-product/mvp-features.md`](../01-product/mvp-features.md) | §4.3 — why compatibility is a coverage feature |
| `../04-api/` | Phase 4 — the surface this ADR governs |
