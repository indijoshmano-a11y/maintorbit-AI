# ADR-0007 — Authentication strategy: sessions, keys, and triple-redundant revocation

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0007 |
| **Title** | Separate human and machine authentication, with triple-redundant revocation |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Security |
| **Implements** | AU-001 … AU-004, AU-007, AU-008, AU-013 |
| **Supersedes** | — |

---

## 1. Context

Five distinct actor types authenticate to MaintOrbit AI: employees in the console,
developers via the Gateway, the VS Code Extension, automated workloads, and — from
v1.2 — identity providers via SAML with SCIM provisioning.

Three requirements shape the design more than any others:

- **FR-AUTH-010 / FR-PERM-005** — session termination and role changes must take effect
  across all surfaces within **60 seconds**.
- **FR-AUTH-018** — deprovisioning an Employee must revoke **every** credential they
  hold, including Platform API Keys they created. The P-07 persona's stated abandonment
  trigger is discovering that a deprovisioned employee's key still works.
- **NFR-PERF-007** — authentication and authorization must complete in ≤ 10 ms p95.

The last two are in direct tension. ADR-0010 forbids relational reads in the hot path, so
authentication is served from cache — and a cached credential remains usable until its
entry expires.

## 2. Problem Statement

How can authentication be served from cache to meet a 10 ms budget while guaranteeing
that revocation takes effect within 60 seconds and that deprovisioning revokes everything?

## 3. Decision

**Separate human and machine authentication. A request carries a Session or a Platform
API Key, never both.**

| Mechanism | Surfaces | Produces | Release |
| --- | --- | --- | --- |
| Password with strength and breach checking | Console | Session | MVP |
| OAuth2 authorization code with PKCE | Console, Extension | Session | MVP |
| TOTP multi-factor | Console | Session step-up | MVP |
| Platform API Key | Gateway, Developer API | Request-scoped context | MVP |
| SAML 2.0 | Console | Session | v1.2 |
| Service identity | Gateway | Request-scoped context | v1.1 |

**Revocation uses three deliberately redundant mechanisms**, because revocation is a
control where partial failure is unacceptable and each mechanism fails differently:

| Mechanism | Speed | Fails when |
| --- | --- | --- |
| **Tombstone in Redis, checked on every cache hit** | Immediate | Redis unavailable — but then the Gateway is already down and rejecting everything, so the failure is safe |
| **Invalidation event** | Sub-second typically | Event delivery delayed or lost |
| **Cache time-to-live ceiling of 60 s** | ≤ 60 s | Never — it is a hard bound |

Tombstone lifetime is **twice** the cache time-to-live ceiling, so a tombstone can never
expire while a stale cache entry survives.

**Additional decisions:**

- **Access credential lifetime is 15 minutes**, refreshed against the session record.
- **Effective permission is the intersection** of the Employee's role permissions and the
  Platform API Key's scopes — never the union.
- **Roles are presets over permissions**, not hard-coded branches, so custom roles
  (FR-PERM-006, v2.0) become a data change rather than a rewrite.
- **Key last-used tracking is derived or coarse**, never a per-request write, which would
  violate ADR-0010.
- **Employee lifecycle transitions are first-class operations**, not console-only flows,
  so SCIM (v1.2) becomes an adapter rather than a rewrite.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Self-contained tokens with no revocation check | Signed tokens validated without a lookup — fastest possible | **Cannot satisfy FR-AUTH-010 or FR-AUTH-018.** A signed token remains valid until expiry regardless of revocation. Short expiry helps but forces constant refresh |
| Database lookup per request | Always authoritative | Violates ADR-0010's budget; a relational round-trip consumes most of the 10 ms allocation |
| Cache with time-to-live only, no tombstone | Simpler | Leaves up to a 60-second window where a revoked key still works. Technically within FR-PERM-005, but fails the spirit of FR-AUTH-018 and the P-07 persona's explicit test |
| Third-party identity platform | Auth0, Okta, or similar for all identity | Removes significant work. **Rejected on NFR-PORT-002** — a customer-hosted deployment cannot depend on a vendor's identity service. Also cedes control over the revocation semantics this design depends on |
| Long-lived API keys pasted into the Extension | Simplest extension integration | Puts a durable secret in a settings file that may be committed or synchronized. Rejected — see ADR-0025 |

## 5. Pros

- **Revocation is effectively immediate** via tombstone, with two independent backstops.
- **Meets the 10 ms budget** because the common path is an in-process cache hit plus one
  Redis set-membership check.
- **Deprovisioning cascades correctly** — Employee, all Sessions, and all Platform API
  Keys they created are tombstoned together.
- **No cross-Company identity** eliminates a large class of multi-tenant authorization
  defects.
- **Session/key separation prevents confused-deputy defects**, where a request authorized
  under one identity performs work attributed to another.
- Permission presets make custom roles a future data change, not a rewrite.

## 6. Cons

- **The tombstone check costs one Redis round-trip on every request** that would otherwise
  be served entirely from in-process memory. This is charged to the stage 1 latency
  allocation and is why it is 2 ms rather than sub-millisecond.
- **Three revocation mechanisms is three things to maintain and test.**
- **No cross-Company identity means duplicate accounts** for consultants working with
  several customers — a real usability cost, accepted deliberately.
- **Redis becomes part of the authentication path**, extending ADR-0006's dependency
  concentration into security.
- Building identity in-house is significant work that a third-party platform would have
  provided.

## 7. Consequences

- **Deprovisioning must be verified, not assumed.** A verification job confirms that no
  credential belonging to a deprovisioned Employee remains resolvable. A silent partial
  failure here is precisely the defect the P-07 persona expects to find.
- **The deprovisioning cascade must enumerate credential types generically**, not by a
  hard-coded list, so a credential type added later is not missed.
- **Records are retained; access is revoked.** FR-TEN-008 requires historical usage and
  audit records to survive Employee removal, attributed to the removed identity. Deleting
  an Employee never deletes their ledger history.
- **Authorization must not branch on a closed role enumeration.** If it does, FR-PERM-006
  becomes a rewrite.
- **SignalR group membership derives from server-side context only** — a client naming
  its own group could subscribe across tenants.
- **The Extension credential derives from a Session**, so every revocation path applies to
  it without a separate mechanism (ADR-0025).

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A cache invalidation defect leaves a revoked credential effective | **Critical** | Medium | Three redundant mechanisms; verification job; tombstone lifetime exceeds TTL |
| R-2 | Deprovisioning misses a credential type introduced later | High | Medium | Generic enumeration rather than a hard-coded list; verification job |
| R-3 | Key lookup requires a scan as key volume grows | Medium | Medium | Non-secret identifying prefix for lookup, secret portion verified against the stored hash — **a structural decision required before schema design** |
| R-4 | Authorization implemented as role conditionals, making custom roles a rewrite | Medium | Medium | Permission presets from the start; review gate on new authorization code |
| R-5 | Service identities (v1.1) have no human owner and therefore no deprovisioning trigger | Medium | High | Requires an expiry and attestation model — the mechanism that makes FR-AUTH-018 work does not apply |
| R-6 | Redis unavailability blocks authentication as well as the Gateway | High | Medium | Inherited from ADR-0006; failure direction is safe (reject) |

## 9. Future Revisions

Revisit when:

- **SAML and SCIM arrive (v1.2).** This ADR anticipates them — pluggable per-Company
  authentication and lifecycle-as-operations — but the SCIM identity reconciliation model
  will warrant its own ADR.
- **Service identities are implemented (v1.1).** They break the deprovisioning model and
  need a distinct lifecycle decision.
- **Hardware security keys are added (FR-AUTH-020, v2.0).** Origin-bound credentials do
  not fit the shared-secret shape of TOTP and require a different credential model.
- **Custom roles arrive (FR-PERM-006, v2.0).** If permission presets were implemented as
  designed, this is a data change; if not, this ADR should be superseded by one recording
  the authorization rewrite.
- **A parent-organization construct is introduced** (FR-TEN-016). Cross-Company identity
  would become unavoidable, invalidating AU-001 — a significant security-model change.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/authentication-architecture.md`](../02-architecture/authentication-architecture.md) | §3.2 mechanisms; §3.4 revocation; §3.6 authorization |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.3 tombstone check in the hot path |
| [`ADR-0005-multi-tenant-strategy.md`](ADR-0005-multi-tenant-strategy.md) | Tenant context originates here |
| [`ADR-0008-credential-encryption.md`](ADR-0008-credential-encryption.md) | Provider Credential custody, a separate concern |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Cache-only constraint driving this design |
| [`ADR-0025-extension-auth.md`](ADR-0025-extension-auth.md) | Extension credential derivation |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-07 abandonment trigger |
| [`../01-product/product-requirements.md`](../01-product/product-requirements.md) | FR-AUTH-001 … 020, FR-PERM-001 … 007 |
