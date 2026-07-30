# Security Implementation Checklist

| Field | Value |
| --- | --- |
| Document | Security Implementation Checklist |
| Version | 1.0 |
| Status | Draft — pending security review |
| Owner | Engineering & Security |
| Last updated | 2026-07-30 |
| Audience | Engineering, Security, Operations, QA |
| Phase | 5 — Security Architecture |

---

## 1. Purpose

This is the verification checklist for the controls specified in
[`security-architecture.md`](security-architecture.md). It exists so that security
implementation is **auditable rather than assumed**.

**Every item is written to be actionable** — something an engineer can do and someone else can
verify. An item that cannot be checked is not on this list.

**Marking:**

| Marker | Meaning |
| --- | --- |
| 🔴 | **Release blocker** — general availability does not proceed without it |
| 🟠 | Required before general availability, not necessarily before beta |
| 🟡 | Required by the stated release |
| ⚙️ | **Mechanically verified** — a build gate or automated test enforces it |

---

## 2. Scope

**In scope:** actionable implementation and verification items across backend, frontend,
database, infrastructure, AI Gateway, VS Code Extension, CI/CD, deployment, and operations.

**Out of scope:** the reasoning behind each item — see
[`security-architecture.md`](security-architecture.md),
[`threat-model.md`](threat-model.md), and [`compliance.md`](compliance.md).

**Verification happens at four points**, and each item is assigned to the earliest point that
can catch it:

| Point | Catches | Limitation |
| --- | --- | --- |
| **Build gate** ⚙️ | Structural violations — layering, discriminators, secrets, licences | Cannot verify runtime behaviour |
| **Test suite** | Behavioural properties — isolation, fail-open/closed, idempotency | Cannot verify production configuration |
| **Release gate** | Whole-system properties — penetration test, unresolved decisions | Too late to be cheap |
| **Continuous operation** | Drift — expired certificates, untested backups, stale vendored code | Depends on the schedule being honoured |

---

## 3. Backend

### 3.1 Authentication

| | Item |
| --- | --- |
| 🔴 ⚙️ | Hash passwords with Argon2id; record the parameters in configuration and set an annual review reminder |
| 🔴 | Check every new and changed password against a known-compromised credential corpus |
| 🔴 | Issue JWT access tokens with a **15-minute** expiry |
| 🔴 | **Exclude roles and permissions from token claims** — resolve them server-side per request |
| 🔴 | Validate signature, expiry, issuer, audience, **and token type** on every token |
| 🔴 | Reject a refresh token presented on an access-token path |
| 🔴 | Store refresh tokens as hashes; issue a new one on every use |
| 🔴 | **On refresh-token reuse, revoke the entire session family, raise a security event, and notify the Employee** |
| 🟠 | Implement a grace window accepting the immediately-previous refresh token; measure its length against real client behaviour |
| 🔴 | Use OAuth2 authorization code with PKCE (SHA-256) for every flow; **do not implement the implicit flow** |
| 🔴 | Validate OAuth2 `state` and OIDC `nonce` as single-use values bound to the session |
| 🔴 | Configure redirect URIs as an **exact-match allowlist** — no prefix or wildcard matching |
| 🔴 | Implement TOTP MFA; hash recovery codes; make them single-use |
| 🔴 | Reject a TOTP code already used within its window |
| 🟠 | Require step-up authentication for: creating or rotating a Provider Connection, changing authentication policy, transferring ownership, enabling content retention, terminating another Employee's sessions |
| 🔴 | Lock accounts after configurable failures **and notify the account holder** |
| 🔴 | Rate-limit authentication endpoints per account **and** per source address |

### 3.2 Session

| | Item |
| --- | --- |
| 🔴 | Implement three independent expiry timers: access token, idle timeout, **absolute lifetime** |
| 🔴 | **Reset idle timeout on user interaction only — not on background polling or SignalR traffic** |
| 🔴 | Revoke **all** sessions on password change |
| 🔴 | Scope sessions to a device; record first-seen, last-active, client, address, coarse location |
| 🔴 | Let an Employee list and individually revoke their own sessions, and terminate all others |
| 🟠 | Notify the Employee on first sign-in from a new device |
| 🔴 | On logout: tombstone the session, revoke the token family, clear the cookie or keychain entry, clear Redux state, clear the query cache, disconnect SignalR, write an audit event |
| 🔴 | Make logout idempotent and always visibly successful to the user, even if server-side revocation fails |

### 3.3 Authorization

| | Item |
| --- | --- |
| 🔴 ⚙️ | Deny any operation with no explicit permission grant |
| 🔴 | Evaluate authorization **in the behaviour pipeline at execution**, not in endpoint attributes alone |
| 🔴 ⚙️ | Add an architecture test asserting **no authorization code branches on a role name** |
| 🔴 | Compute effective permission as the **intersection** of role permissions and key scopes |
| 🔴 | Evaluate scope using only the current Company's data |
| 🔴 | Ensure **no role can read another Employee's conversation content** |
| 🔴 ⚙️ | Emit an audit event on **every** authorization denial |
| 🔴 ⚙️ | Add architecture test AT-10: no repository invoked outside a dispatcher-mediated handler |
| 🔴 ⚙️ | Add architecture test AT-11: every SignalR hub method carries an authorization requirement |

### 3.4 Credentials and cryptography

| | Item |
| --- | --- |
| 🔴 | **Implement no operation that returns a Provider Credential in plaintext to a caller** |
| 🔴 | Encrypt with AES-256-GCM; **generate a provably unique nonce per key** |
| 🔴 | Verify the full authentication tag on every decryption; raise a security event on failure |
| 🔴 | **Bind the Company identifier and DEK version into the AAD** |
| 🔴 | Generate one data encryption key per Company; wrap it with the KEK |
| 🔴 | Store DEK version **and algorithm identifier** alongside every ciphertext |
| 🔴 ⚙️ | **Define a credential type that cannot be formatted into a string**; add a test asserting credential material is never a plain string |
| 🔴 | On rotation, keep both credentials valid until in-flight requests drain, then destroy the old ciphertext |
| 🔴 | Hash Platform API Key secrets with SHA-256; add a non-secret identifying prefix for constant-time lookup |
| 🔴 | Use a cryptographically secure RNG for tokens, nonces, identifiers, and recovery codes |
| 🔴 ⚙️ | Add a build check rejecting MD5, SHA-1 for security use, ECB, and unauthenticated CBC |
| 🔴 | **Never disable certificate validation — including in development configuration** |

### 3.5 API

| | Item |
| --- | --- |
| 🔴 | Validate all input crossing a trust boundary against an explicit allowlist schema, **before** opening a transaction |
| 🔴 ⚙️ | Use parameterized queries everywhere, **including Analytics direct SQL**; add a review gate on new raw SQL |
| 🔴 | **Never interpolate prompt content into a query, command, or log message** |
| 🔴 | Rate-limit per Company, Team, and Key; include retry guidance in the rejection |
| 🟠 | Accept a Company-scoped idempotency key on mutating operations; return the recorded outcome on replay |
| 🔴 | Configure an explicit CORS origin allowlist; **do not permit browser origins on the Gateway** |
| 🔴 | Apply anti-CSRF tokens to cookie-carried credentials; set `SameSite` as defence in depth |
| 🔴 | Strip credentials, content, and cross-tenant identifiers from all error responses |
| 🟠 | Neutralize formula injection in CSV and tabular exports |
| 🔴 | Express fail-open / fail-closed classification in the type system so a new dependency must state its category |
| 🟠 | Authenticate and authorize the Hangfire dashboard; do not route it publicly |

### 3.6 Audit

| | Item |
| --- | --- |
| 🔴 | **Implement no update or delete operation on audit records** |
| 🔴 ⚙️ | Add a test asserting audit emission is never sampled under load |
| 🔴 | Emit audit events from the pipeline, not from individual handlers |
| 🔴 ⚙️ | **Add a shared test suite asserting the Gateway hot path and the dispatcher pipeline produce equivalent authorization and audit outcomes** |
| 🔴 | Store references to content in audit records — never the content itself |
| 🔴 | Alert as an incident when an audit write fails |
| 🔴 | Emit an audit event when a retention period is changed |
| 🔴 | Emit an audit event on every data export, recording actor, scope, and destination |
| 🟡 | Implement hash-chained tamper-evidence — v1.1 |

---

## 4. Frontend

| | Item |
| --- | --- |
| 🔴 | Deploy a strict CSP with **no `unsafe-inline` and no `unsafe-eval`**; use nonces for any required inline script |
| 🔴 | **Sanitize model completions before rendering them into the DOM** |
| 🔴 | Hold the access token in memory only — **never in `localStorage` or `sessionStorage`** |
| 🔴 | Store the refresh token in an `HttpOnly`, `Secure`, `SameSite` cookie |
| 🔴 | Include the Company identifier in every TanStack Query key |
| 🔴 | Clear the query cache and Redux state on session change and on logout |
| 🔴 | Treat client-side validation as UX only; rely on server revalidation |
| 🔴 | Treat server-rendered permission gating as defence in depth — **never as the enforcement point** |
| 🔴 | Gate UI on **permissions**, not on role names |
| 🔴 ⚙️ | Add a build check that no secret reachable in a server component is serialized into the client bundle |
| 🔴 ⚙️ | Apply all security headers: HSTS, `nosniff`, frame denial, referrer policy, permissions policy, `no-store` on authenticated responses |
| 🟠 | Display a persistent, non-dismissible indicator of what the Company can observe about Chat usage |
| 🟠 | Show device metadata in the Employee's own session list |
| 🟠 ⚙️ | Run an automated accessibility audit in CI |

---

## 5. Database

| | Item |
| --- | --- |
| 🔴 ⚙️ | Create a row-level security policy on **every** tenant-scoped relation |
| 🔴 ⚙️ | Create the policy in the **same migration** that creates the table |
| 🔴 ⚙️ | Add architecture test AT-4: every tenant-scoped entity carries the tenant discriminator |
| 🔴 | Set the tenant session variable at connection checkout and **clear it at connection return** |
| 🔴 | **Prototype and load-test the connection pooling mode against session-scoped RLS before schema design** |
| 🔴 ⚙️ | Add a test per relation asserting that with no tenant context set, **zero rows** are returned |
| 🔴 ⚙️ | Add a cross-tenant isolation test per relation, running on every build |
| 🔴 ⚙️ | Add an architecture test enumerating the code paths permitted to request the elevated database role |
| 🔴 | Emit an audit event on every use of the elevated role |
| 🔴 | Ensure the application database role **cannot** bypass row-level security |
| 🔴 | Establish tenant context explicitly in every Hangfire job before any data access |
| 🔴 | Enable disk encryption at rest and enforce TLS in transit |
| 🔴 | Implement retention as partition drop — **never a mass `DELETE`** |
| 🔴 | Create one schema per module; add no foreign keys across module schemas |
| 🔴 | Make every migration backward-compatible with the previous application version; use expand-and-contract for removals |
| 🔴 | Configure streaming replication with automatic failover, and continuous archiving for point-in-time recovery |

---

## 6. Infrastructure

| | Item |
| --- | --- |
| 🔴 | Configure TLS with current protocol versions only and forward-secret cipher suites; disable obsolete versions |
| 🔴 | Enable HSTS with a long max-age and `includeSubDomains`; **defer preload until the domain strategy is stable** |
| 🔴 | Enforce TLS between the application tier and PostgreSQL, Redis, object storage, and the key custodian |
| 🔴 | **Disable Nginx response buffering on Gateway and Chat streaming paths** |
| 🔴 | **Set Nginx timeouts longer than application timeouts** so the application owns its failure semantics |
| 🔴 | Automate certificate renewal **and configure expiry alerting through an independent mechanism** |
| 🔴 | Run all containers as non-root with a read-only root filesystem where practical |
| 🔴 | Remove public SSH; use a bastion or just-in-time access with MFA, and audit every session |
| 🔴 | Place application VMs on a private network; expose only the load balancer |
| 🔴 | **Deliver the KEK through the custodian — never as an environment variable in production** |
| 🔴 | Inject all secrets at container start; bake none into images |
| 🔴 | Use no secret in more than one environment |
| 🔴 | **Copy no production data into any non-production environment** |
| 🔴 | **Configure the Redis streams instance with no eviction policy** |
| 🔴 | Separate the Redis streams instance from the cache instance before production traffic |
| 🔴 | Configure Redis replication with automatic failover and append-only persistence with per-second sync |
| 🔴 | Scope every Redis key and every object storage key to a Company |
| 🔴 | Encrypt backups, store them separately from primary storage, and audit every access |
| 🟠 | Define infrastructure as code rather than configuring VMs manually |

---

## 7. AI Gateway

| | Item |
| --- | --- |
| 🔴 | Check the revocation tombstone set on **every** cache hit |
| 🔴 | Cap cache TTL at 60 seconds for all authorization-relevant state |
| 🔴 | Set tombstone lifetime to **twice** the cache TTL ceiling |
| 🔴 | Include the Company identifier in every hot-path cache key |
| 🔴 | Fail **closed** on authentication, authorization, tenant context, quota, budget, and governance |
| 🔴 | Fail **open** on metering, audit emission, analytics, and telemetry — **and alert on every occurrence** |
| 🔴 | Emit usage, audit, and decision records for **failed** requests as well as successful ones |
| 🔴 | Evaluate governance policies before forwarding to a provider |
| 🔴 | Default every new governance policy to monitor mode |
| 🔴 | Hold decrypted credentials transiently; persist them nowhere, including caches |
| 🔴 | Bound and document the per-Company DEK cache lifetime as a security decision |
| 🔴 | Propagate the correlation identifier to every component and return it to the caller |
| 🔴 | **Record usage for tokens already consumed when a client disconnects mid-stream** |
| 🟠 | Apply idempotency to mutating Gateway operations |

---

## 8. VS Code Extension

| | Item |
| --- | --- |
| 🔴 | Authenticate via OAuth2 with PKCE — **implement no pasted-API-key path** |
| 🔴 | Store the refresh credential in VS Code `SecretStorage` (OS keychain); **never in settings files** |
| 🔴 | Hold the access credential in process memory only |
| 🔴 | Derive the extension credential from a **Session**, so existing revocation paths apply |
| 🔴 | **Grant the webview no credentials and no network access**; enforce a CSP on it |
| 🔴 | Route every command through **one shared context pipeline** |
| 🔴 | **Implement no opportunistic workspace traversal** — gather only selected, opened-and-acted-on, attached, or rule-covered content |
| 🔴 | Apply exclusion filters honouring the workspace ignore configuration before transmission |
| 🟠 | Detect and strip common secret shapes; **disclose the removal and present it as best-effort, not a guarantee** |
| 🔴 | **Display exactly what will be sent before sending it** |
| 🔴 | Enforce size limits client-side and disclose truncation |
| 🔴 | Enforce governance server-side; surface the reason clearly in the editor |
| 🔴 | Distinguish developer-fixable errors from organizational limits in every message |
| 🟠 | Check platform version compatibility on activation and report mismatches clearly |
| 🔴 | Activate lazily — on command invocation or panel open, never on editor startup |

---

## 9. CI/CD

| | Item |
| --- | --- |
| 🔴 ⚙️ | Run secret scanning as a build gate |
| 🔴 ⚙️ | Run dependency vulnerability scanning; **fail the build on unresolved critical findings** |
| 🔴 ⚙️ | Run tenant isolation tests on every build |
| 🔴 ⚙️ | Run architecture tests AT-1 … AT-12 as build gates |
| 🔴 ⚙️ | Add AT-12: reject any dependency that cannot run in a customer-controlled environment |
| 🔴 | **Pin every third-party action by commit SHA — never by tag** |
| 🔴 | Configure package source mapping so a package name resolves only from its expected source |
| 🔴 | Commit lockfiles; restore from them in CI rather than resolving fresh |
| 🔴 | Scope deployment credentials to least privilege and rotate them quarterly |
| 🔴 | Build images once and promote them; never rebuild per environment |
| 🟠 | **Run the portable key custodian and portable object storage as the CI default** |
| 🟠 | Add a licence scan failing the build on a disallowed licence class |
| 🟠 | Scan container images for vulnerabilities |

---

## 10. Deployment

| | Item |
| --- | --- |
| 🔴 | Run migrations to completion **before** starting any new application container |
| 🔴 | Abort the rollout if a migration fails |
| 🔴 | Gate return-to-rotation on readiness health checks |
| 🔴 | Verify rollback to the previous image without data loss |
| 🔴 | **Complete an independent penetration test before general availability** |
| 🔴 | **Publish a vulnerability disclosure process before general availability** |
| 🔴 | Run failure-injection tests against every fail-open and fail-closed classification and record observed behaviour |
| 🔴 | **Deploy only a runtime within its vendor support window** |
| 🟠 | **Publish an availability commitment that matches the deployed topology** |
| 🟠 | Rebuild container base images on a schedule, independent of application code changes |

---

## 11. Operations

| | Item |
| --- | --- |
| 🔴 | Write a runbook for every alert **before enabling it** |
| 🔴 | Configure and test P1 alerts: cross-tenant access attempt, elevated-role use outside enumerated paths, key recovery invocation, GCM tag failure, deprovisioning verification failure, audit write failure |
| 🔴 | Monitor KEK access frequency and pattern; alert on deviation |
| 🔴 | Run a reconciliation job comparing ingestion stream offsets against persisted record counts |
| 🔴 | Run a deprovisioning verification job confirming no credential of a removed Employee remains resolvable |
| 🔴 | Document the incident response process with P1–P4 severities and named owners |
| 🔴 | **Create the KEK backup: encrypted, stored independently of the custodian and database** |
| 🔴 | **Document the KEK restore procedure and execute it successfully at least once** |
| 🔴 | **Establish key escrow with split custody; document who authorizes recovery and how custodian succession works** |
| 🔴 | Alert immediately and unconditionally on any key recovery invocation |
| 🔴 | Test backup restoration quarterly and record the results |
| 🟠 | Exercise key rotation quarterly rather than only when required |
| 🟠 | Publish a subprocessor list |
| 🟠 | Run anomaly detection in observe mode before enabling any action |
| 🟠 | Review vendored components against upstream quarterly, with a named owner |
| 🟠 | Re-verify dependency licences semi-annually |

---

## 12. Release gates

**General availability does not proceed with any of these unresolved.**

| # | Gate |
| --- | --- |
| **G-1** | Tenant isolation strategy ratified; **connection pooling mode verified against session-scoped RLS** |
| **G-2** | **KEK backup created, restore procedure tested, escrow with split custody established** |
| **G-3** | Runtime within its vendor support window |
| **G-4** | Published availability commitment matches the deployed topology |
| **G-5** | Gateway behaviour during a Redis outage decided and documented |
| **G-6** | Ingestion durability position resolved and honestly stated in customer material |
| **G-7** | Independent penetration test completed; critical findings resolved |
| **G-8** | Vulnerability disclosure process published and monitored |
| **G-9** | **Zero cross-tenant exposures in testing** |
| **G-10** | **Zero usage or audit records lost in testing** |

**G-9 and G-10 are pass/fail with no tolerance.** Any non-zero value blocks release regardless
of every other result.

---

## 13. Design decisions

| # | Decision | Rationale |
| --- | --- | --- |
| CL-a | **Every item is phrased as an action** | An item that cannot be done and verified is not a control |
| CL-b | **⚙️ applied wherever a mechanical check is possible** | An item depending on memory is not a control |
| CL-c | **Release gates separated from ordinary items** | Some block; most inform |
| CL-d | **G-9 and G-10 admit no tolerance** | Cross-tenant exposure and ledger loss have no partial credit |
| CL-e | **Items assigned to the earliest verification point that can catch them** | A control verified only at release has already been built around |

---

## 14. Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| K-1 | Completion asserted rather than evidenced | Convert to a dated per-release verification record with an owner — also what a SOC 2 Type II examination requires |
| K-2 | Items skipped silently under schedule pressure | Require a recorded reason and named owner for every exception |
| K-3 | Checklist fatigue in a long document | 🔴/🟠/🟡 marking keeps blocking items findable |
| K-4 | **False confidence from ⚙️ items** | A mechanical check verifies what it was written to verify, not the intent. Architecture tests can pass while a boundary is meaningfully broken |
| K-5 | Drift between this checklist and the architecture | This document derives from `security-architecture.md`; changes there require changes here |
| K-6 | Release gates contested at the deadline | Recorded now, before schedule pressure exists, so the argument happens early |

**K-6 is the reason the gates are written down at all.** Every one will be questioned when a
date is at risk. Recording them in advance makes that conversation about accepting a stated
risk rather than discovering one.

---

## 15. Trade-offs

| # | Gained | Given up |
| --- | --- | --- |
| T-1 | Comprehensive coverage | Length; risk of fatigue |
| T-2 | Mechanical verification where possible | CI time; more gates to maintain |
| T-3 | Explicit release gates | Some will be contested under pressure |
| T-4 | Actionable phrasing throughout | Longer items than a keyword list |

---

## 16. Future improvements

- **Automate more items.** The highest-value candidates are permission-gating patterns,
  credential typing, and vendored-component drift.
- **Convert to a per-release verification record** so completion is evidenced rather than
  asserted.
- **Add per-feature security review checkpoints** so verification is continuous rather than a
  pre-release event.
- **Track exceptions explicitly** — an item skipped with a recorded reason and an owner is
  manageable; a silent skip is not.
- **Prune annually.** An item that has caught nothing in a year is a candidate for removal; a
  checklist that only grows stops being read.

---

## 17. Cross references

| Document | Relationship |
| --- | --- |
| [`security-architecture.md`](security-architecture.md) | Source of every item |
| [`threat-model.md`](threat-model.md) | Residual risks these verify |
| [`compliance.md`](compliance.md) | Regulatory obligations these support |
| [`../04-technology/coding-standards.md`](../04-technology/coding-standards.md) | Enforced coding rules overlapping ⚙️ items |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §8 architecture tests |
| [`../03-adr/ADR-0019-github-actions.md`](../03-adr/ADR-0019-github-actions.md) | Build gates |
| [`../01-product/mvp-features.md`](../01-product/mvp-features.md) | §7 definition of done |
