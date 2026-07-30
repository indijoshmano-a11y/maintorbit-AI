# ADR-0008 — Protect Provider Credentials with envelope encryption

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0008 |
| **Title** | Protect Provider Credentials with per-Company envelope encryption and a pluggable key custodian |
| **Status** | **Proposed** — key custodian selection outstanding (decision D-6) |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Security |
| **Implements** | AD-008, AU-009, AU-010 |
| **Supersedes** | — |

---

## 1. Context

MaintOrbit AI stores Provider Credentials on customers' behalf — the API keys that
authenticate to OpenAI, Anthropic, Google Gemini, and others.

**These are the highest-value secrets in the system.** Each carries direct spend
authority and an unrestricted data egress channel. A compromise of the credential store
would expose every customer simultaneously and is existential rather than embarrassing.
This is the exact problem the product exists to solve (credential sprawl,
[`../01-product/problem-statement.md`](../01-product/problem-statement.md) §3.1), which
makes failing at it a particular kind of failure.

Governing requirements:

- **NFR-SEC-003** — keys distinct from those protecting general application data
- **NFR-SEC-004 / FR-PROV-004** — never retrievable through any interface, by any Role,
  including the Owner
- **NFR-SEC-005** — never in logs, traces, error messages, or diagnostic output
- **NFR-SEC-019** — rotatable without customer-visible interruption
- **NFR-PORT-002** — no dependency that cannot run in a customer-controlled environment

## 2. Problem Statement

How should customer-supplied Provider Credentials be stored so that a database
compromise does not expose them, no code path can return them in plaintext, and the
scheme still works in a customer-hosted deployment with no cloud key service?

## 3. Decision

**Envelope encryption with a per-Company data encryption key, wrapped by a key-encryption
key held by a pluggable custodian outside the database.**

```
Key-encryption key (custodian, outside the database)
        └── wraps → per-Company data encryption key (ciphertext, in the database)
                            └── encrypts → Provider Credential (ciphertext, in the database)
```

| Property | Decision |
| --- | --- |
| Data key scope | **Per Company** — bounds blast radius to one customer |
| Data key storage | Encrypted, in the database |
| Key-encryption key storage | **Outside the database**, in a custodian with its own access control and audit trail |
| Custodian | **Pluggable.** A portable implementation is the **default in development and CI**; a cloud key service is an optional production provider |
| Plaintext lifetime | Transient in memory during a provider call only; never persisted, never returned |
| Retrieval interface | **None exists in code** |

**FR-PROV-004 is satisfied structurally, not by permission.** There is no "reveal
credential" operation to misconfigure. The decryption function is reachable only from the
provider execution path and yields a handle used for a call — not a value returned to a
caller.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Single platform-wide encryption key | One key encrypts all credentials | Blast radius is every customer at once. No per-Company rotation. Fails the spirit of NFR-SEC-003 |
| Database-native transparent encryption only | Rely on disk or column encryption | Protects against stolen disks, not against a compromised application or a database credential leak — the more likely path |
| Cloud key service for every operation | Every decrypt is a call to a managed key vault | **Violates NFR-PORT-002** as a hard dependency, and adds a network round trip to the hot path. Acceptable only as a *custodian* for the key-encryption key, which is what was chosen |
| Customer-held keys from day one | Customer supplies and holds the key material | Strongest posture; removes our ability to assist in recovery and adds significant onboarding friction. **Deferred to NFR-SEC-020 (v2.0)**, and the pluggable custodian is what makes it feasible then |
| Hardware security module | Dedicated cryptographic hardware | Strongest key protection; incompatible with NFR-PORT-002 self-hosting and disproportionate at current scale |
| Don't store credentials — proxy customer-held keys per request | Customer sends the key with each call | Defeats the product's purpose. Credential custody *is* the value proposition |

## 5. Pros

- **Per-Company blast radius.** Compromise of one data key exposes one Company.
- **Key-encryption key compromise requires a second breach** — it is not in the database,
  so a database dump alone is insufficient.
- **Per-Company rotation is possible** without re-encrypting every customer's data,
  satisfying NFR-SEC-019.
- **No retrieval path exists**, so FR-PROV-004 cannot be violated by a permission
  misconfiguration.
- **Pluggable custodian preserves NFR-PORT-002** and is the mechanism that makes
  customer-managed keys (NFR-SEC-020) a new implementation rather than a redesign.

## 6. Cons

- **The key-encryption key is a single point of catastrophic failure.** Its compromise
  exposes every Company's credentials.
- **Key loss means credential loss.** If the key-encryption key is lost and not recoverable,
  every stored credential becomes permanently undecryptable and every customer must
  re-enter their provider credentials.
- **Operational complexity**: key generation, storage, rotation, backup, and access
  control are all new operational responsibilities.
- **Decryption cost sits in the provider execution path.** Unwrapping per request would
  add latency, so decrypted material must be cached per connection with a short lifetime —
  itself a security consideration.
- The portable custodian is necessarily weaker than a managed key service, so the
  self-hosted security posture differs from the hosted one and must be documented honestly.

## 7. Consequences

- **The portable custodian must be the default in development and CI.** If only the
  cloud-backed path is ever exercised, the portable path will be broken when v2.1
  self-hosted deployment needs it — discovered at the worst possible time. This is
  AU-010 and it is a standing engineering requirement, not a preference.
- **Key custody becomes an operational programme**: backup, rotation schedule, access
  control, break-glass procedure, and audit. None of this exists yet.
- **A dedicated threat model is warranted** for this component specifically, separate from
  the platform-wide security review.
- **Decrypted material is cached per Provider Connection with a short lifetime**, never
  persisted. The cache is a security boundary.
- **Credential encryption keys must never appear in environment variables in production**
  — the custodian is the delivery mechanism.
- **Self-hosted deployment shifts the trust boundary**: the customer holds the
  key-encryption key, improving their posture and removing our ability to assist in
  recovery. This consequence must be documented for customers before v2.1.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Key-encryption key compromise exposes every Company's credentials | **Critical** | Low | Custodian outside the database with independent access control and audit; rotation capability; dedicated threat model; access is itself audited |
| R-2 | Key-encryption key is lost, rendering all credentials undecryptable | **Critical** | Low | Documented backup and recovery procedure, tested; this must be part of the NFR-DR-006 quarterly exercise |
| R-3 | Portable custodian is never exercised and is broken when v2.1 needs it | Medium | **High** | It is the development and CI default, not an alternative |
| R-4 | A credential appears in a log, trace, or error message | High | Medium | NFR-SEC-005; type-level separation so credential material is not a plain string; log scrubbing as a second layer; secret scanning in CI |
| R-5 | Per-connection decryption cache becomes a leak vector in a memory dump | Medium | Low | Short lifetime; never persisted; cleared after use |
| R-6 | Custodian becomes a bottleneck as Provider Connection count grows | Medium | Medium | Data keys unwrapped per Company and cached briefly, not per request |

## 9. Ratification criteria

This ADR remains **Proposed** until decision D-6 selects:

1. The **production custodian implementation** and its access control model.
2. The **portable custodian implementation** used in development, CI, and self-hosted
   deployment.
3. The **key backup and recovery procedure**, tested at least once.
4. The **rotation procedure** for both key tiers, demonstrating NFR-SEC-019 compliance
   without customer-visible interruption.

Item 3 is the one most likely to be skipped and the one whose absence is most dangerous:
risk R-2 is unmitigated without it.

## 10. Future Revisions

Revisit when:

- **Customer-managed encryption keys are implemented** (NFR-SEC-020, v2.0). If the
  custodian is genuinely pluggable, this is a new implementation; if the cloud-backed path
  has been assumed anywhere, it is a redesign.
- **Self-hosted deployment ships** (v2.1). The trust boundary changes and the operational
  consequences need customer-facing documentation.
- **A hardware security module becomes viable** — most likely driven by a regulated
  enterprise contract requirement rather than by our own assessment.
- **Provider credential formats change materially** — for example, if providers move to
  short-lived tokens or workload identity federation, much of this design becomes
  unnecessary for those providers and the abstraction should accommodate both.

## 11. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/authentication-architecture.md`](../02-architecture/authentication-architecture.md) | §3.8 credential custody |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-008; §8 decision D-6 |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.8 secret delivery; decision DD-4 |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Platform credentials, a distinct concern |
| [`ADR-0009-ai-provider-abstraction.md`](ADR-0009-ai-provider-abstraction.md) | Where decrypted credentials are used |
| [`../01-product/problem-statement.md`](../01-product/problem-statement.md) | §3.1 credential sprawl — the problem this must not reproduce |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SEC-003/004/005/019/020, NFR-PORT-002 |
