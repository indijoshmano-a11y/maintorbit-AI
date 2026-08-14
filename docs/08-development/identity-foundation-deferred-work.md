# Identity Foundation — Deferred Work Register

**Status:** Phase 11 complete (milestones 11.1–11.24), verified 2026-08-08.
**Documentation reconciled:** Milestone 12.1 — §2's defects are resolved; §5 classifies every
Phase 11 assumption.
**Audit store built:** Milestone 12.2 — I-1 is closed; D-4 is closed; new deferred work in §1.
**Partition maintenance built:** Milestone 12.3 — I-9 is closed; the Worker foundation exists.
**Search and export built:** Milestone 12.4 — I-10 is closed; AU-5 and AU-6 are met; D-13 is open.
**Scope:** the `identity` module only. Nothing here describes work that was attempted and failed;
everything is work deliberately not started, or a documentation defect found while building.

This register exists because the alternative is a milestone report nobody re-reads. Each entry
says what is missing, what currently stands in its place, and what breaks if it is forgotten.

---

## 1. Implementation deferred

Phase 11 built the seam in each of these cases and stopped there. The seam is real — it composes,
it is registered, and it is exercised by tests — but the thing behind it is not the production
article.

| # | Deferred | What stands in its place | Consequence if forgotten |
| --- | --- | --- | --- |
| ~~**I-1**~~ | ~~The **`auditing` module** — `audit_events` table, RLS policy, append-only enforcement~~ | — | ✅ **Closed by 12.2.** `auditing.audit_events` exists: partitioned monthly, tenant-scoped, `REVOKE UPDATE, DELETE`, no update or delete path in code. `LoggingAuditSink` was **removed**, not kept as a fallback. The remaining audit gaps are I-9 … I-11 below |
| **I-2** | **Email delivery** for password reset and email verification | `UndeliveredPasswordResetNotifier` / `UndeliveredEmailVerificationNotifier` log the gap rather than sending | Both flows are complete and correct server-side but cannot reach an Employee. Verification gates account activation (FR-AUTH-013), so this blocks real onboarding |
| **I-13** | **Hangfire** and the ADR-0014 job framework | A single `BackgroundService` with a timer, in `MaintOrbit.Worker` | ADR-0014 chose Hangfire, and it remains the decision for the nine job classes in its table. 12.3 has one job whose retry is "run again tomorrow", so a framework, its PostgreSQL schema, and a package whose LGPL obligations are open (TD-3) would be cost without benefit. **Revisit when the second job class arrives** — the maintenance sits behind a port, so scheduling it differently changes one file |
| **I-14** | **Worker health probe** | `WorkerHealth` exposes the last cycle's outcome in-process; nothing serves it | A background role has no listener, and adding one to answer a probe would make it a web host. The value is already computed; what is missing is a transport an orchestrator can read |
| **I-3** | The **ADR-0012 CQRS dispatcher** and its nine ordered behaviours | Handlers are registered one per use case against `ICommandHandler`/`IQueryHandler` and invoked directly from endpoints | Audit emission, validation, and transaction boundaries are each implemented per-handler. §3.3 warns this makes coverage "a function of developer discipline" — `AuditEmissionTests` is the deliberate stand-in for the pipeline's guarantee (see D-4 below) |
| **I-4** | The **ADR-0013 transactional outbox** | Handlers call `SaveChangesAsync` directly; audit emission is fail-open per ADR-0021 | An audit event can be lost if the sink fails after the transaction commits. This is classified fail-open deliberately, and every loss is logged as an AU-8 incident (EventId 1600) — but it is not the at-least-once delivery the outbox would give |
| **I-5** | **Platform API Keys** — listed in CLAUDE.md §7 as owned by `identity` | Nothing. `Authentication/ApiKeys/` is an empty directory | Not required by any Phase 11 milestone. Named here only so the empty directory is not mistaken for a partial implementation |
| **I-6** | **OAuth2 / SSO** | Nothing. `Authentication/OAuth2/` is an empty directory | As above |
| **I-7** | **Rate limiting** on authentication endpoints | Failed-login counting and account lockout (11.18) bound per-account guessing | Lockout bounds attempts against *one* account. It does nothing against spraying one password across many accounts, which is the attack it most resembles |
| **I-8** | **Per-Company data keys** (SD-012 `dek_version` is on the row already) | `DeploymentDataKeyStore` returns one deployment-wide key for every Company | The envelope scheme and the version column are correct and ready. Only the key *source* is deployment-wide, so a per-Company key rotation is a store swap, not a schema change |
| ~~**I-9**~~ | ~~**Partition management** — creation ahead of need~~ | — | ✅ **Closed by 12.3.** `MaintOrbit.Worker` creates every missing month to a configurable horizon on a daily cycle, idempotent and serialised by a PostgreSQL advisory lock. **Dropping expired partitions is implemented but disabled by default — see I-11** |
| ~~**I-10**~~ | ~~**Audit search and export**~~ | — | ✅ **Closed by 12.4.** `GET /api/v1/audit-events` and `/export`, `audit.read`, keyset pagination, isolated by RLS on the tenant-scoped connection. **AU-5 and AU-6 met; AU-9 met for freshness but unmeasured under load** (needs I-12). Export's format, async threshold, and `audit.export` action remain open — see §2 D-13 |
| **I-11** 🔴 | **Legal holds** (`legal_holds`, FR-GOV-011) | Nothing | **Now the blocker on automated retention.** 12.3 built the drop and left it off: a partition may hold events under a hold, and with no way to ask, an automated drop could destroy the evidence a hold exists to preserve. Storage grows until this exists or an operator enables dropping deliberately. The design in `06-database` §4.10 is one prose line — it needs specifying before it can be built |
| **I-12** | The **durable stream and batch writer** (§3.3), and `stream_entry_id` | Emission writes synchronously, straight through, after the audited operation commits | The column and its unique index exist and are null on every row. Until the stream exists there is no redelivery to deduplicate — and no buffer between a write burst and the database |

---

## 2. Documentation defects found while building

These are defects in `docs/`, not in the code. Each was worked around by following the frozen
decision the document contradicts, and each was reported in the milestone that hit it. Correcting
them was outside every Phase 11 milestone's scope; **Milestone 12.1 was that scope**, and the
resolution column records what happened to each.

| # | Document | Defect | Resolution |
| --- | --- | --- | --- |
| **D-1** | `06-database` §3.3 | `employees.company_id → companies.id` marked "same schema". `companies` belongs to `tenancy`, so this is a cross-module foreign key — which §3.3 itself, and CLAUDE.md §9, forbid. Implemented without the FK | ✅ **12.1** — row corrected to "identifier only", with a note recording that the code was always right |
| **D-2** | `04-technology` §8 | Named `Microsoft.AspNetCore.Cryptography.KeyDerivation` (PBKDF2 only) for password hashing, contradicting SD-010's Argon2id. Implemented per SD-010 | ✅ **12.1** — replaced with `Konscious.Security.Cryptography.Argon2` 1.3.1 |
| **D-3** | `05-security/04-tenant-security` §3.4 | The cross-Company access path table omits authentication, which is itself a cross-Company read — an Employee's email must be resolved before their tenant is known | ✅ **12.1** — added as **path 13**, naming `ICredentialDirectory`'s four lookups as the complete enumeration |
| **D-4** | `05-security/12-audit-and-compliance` §3.3 | Assigns audit emission to pipeline position 8 of a pipeline that does not exist (see I-3), and defines no vocabulary of audit action names | ✅ **Closed by 12.2.** §3.4 now ratifies the thirteen actions, five targets, three outcomes and three actor types, with their form and the reason they live in Shared. The pipeline half remains marked target-not-build |
| **D-5** | `06-database` §4.2 | **Three tables had no definition at all**: `password_reset_tokens`, `email_verification_tokens`, `company_authentication_policies` | ✅ **12.1** — all three defined from the verified schema |
| **D-6** | `06-database` §4.2 | **Six tables described in prose with no column definition**: `mfa_enrollments`, `mfa_recovery_codes`, `permissions`, `role_definitions`, `role_permissions`, `employee_roles` | ✅ **12.1** — all six given definitions; the three reference tables gained an explanation of why they carry no RLS |

Also corrected in 12.1, found during the same audit rather than during Phase 11:

| # | Document | Defect | Resolution |
| --- | --- | --- | --- |
| **D-7** | `06-database` §4.2 | `employee_credentials` key columns omitted `company_id`, `algorithm`, `password_version`, `require_password_change`, `failed_login_count`, `lockout_until_utc` | ✅ Completed from the verified schema |
| **D-8** | `06-database` §4.2 | `employees.status` documented lowercase (`invited, active…`); the check constraint requires `Invited`, `Active`, `Suspended`, `Removed` | ✅ Corrected, and the constraint named |
| **D-9** | `06-database` §4.2 | `refresh_tokens` omitted `company_id` and `expires_at_utc`; `sessions` listed no constraints | ✅ Completed |
| **D-10** | `06-database` §4.2 | `federated_identities` and `platform_api_keys` documented as though they exist; no migration creates either | ✅ Moved under "Designed, not yet built", cross-referenced to I-5 and I-6 |
| **D-11** | `04-technology` §8 | `Otp.NET` named for TOTP; no such package is referenced — RFC 6238 is implemented over the framework's `HMACSHA1` | ✅ Corrected, with the reasoning and the SHA-1 clarification |
| **D-13** | `05-security/12-audit-and-compliance` §3.6, `api-specification` §5.5 | **Export has three unspecified decisions**, recorded rather than invented in 12.4: FR-AUD-006 requires "a documented machine-readable format" and no document names one; §5.5 says export is "asynchronous above a threshold" with no threshold and no mechanism; and AC-i requires export to be audited but names no action. Implemented as JSON reusing the search representation, synchronous and bounded, with `audit.export` as an assumption | ⚠️ **Open** — needs Product |
| **D-12** | `04-technology` §4 | Seven referenced packages absent from the inventory, including `Microsoft.IdentityModel.JsonWebTokens` and the six `Microsoft.Extensions.*` abstractions that make ADR-0001's dependency rule expressible | ✅ Added |

> **Recurrence prevention.** `DocumentationDriftTests` now enforces the two directions that failed
> silently: **every table created by a migration must appear in `06-database`**, and **every package
> the build references must appear in `04-technology`**. Both are one-directional by design — a
> documented-but-unbuilt table is a specification doing its job, not drift. A third rule asserts no
> migration declares a foreign key into another schema, which is D-1's defect stated against the
> migrations rather than against prose.

---

## 3. Verification residue

Found during 11.24's end-to-end verification. Neither is a defect in delivered behaviour.

| # | Finding | Detail |
| --- | --- | --- |
| **V-1** | **Three empty test projects** | `MaintOrbit.TestUtilities`, `MaintOrbit.Application.UnitTests` and `MaintOrbit.Infrastructure.IntegrationTests` contain zero `.cs` files. They restore, build, and appear in package listings, but run no tests. All Application and Infrastructure behaviour is currently covered through `MaintOrbit.Api.FunctionalTests` instead — a level higher than `08-development/testing-strategy.md` names for it. The coverage is real; its location is not what the strategy describes |
| **V-2** | **`xunit` 2.9.3 is deprecated** | Marked `Legacy`, superseded by `xunit.v3`. Test projects only — no production dependency. No vulnerable packages exist anywhere in the solution |
| **V-4** | **Audit partition bounds were not UTC-aligned** | Found and fixed in 12.3. The 12.2 migration computed months as `date` values through `%L`, producing a `timestamp without time zone` literal that PostgreSQL interprets in the *server's* timezone. On a server at `+05:30`, an event at 31 July 20:00Z was stored in the partition named for August — and retention drops partitions by name, so it would have destroyed part of one month and preserved part of another. **Invisible on a UTC server**, which is why it survived review. Corrected by `AuditPartitionBoundsUtc`, which refuses to run if any partition holds rows rather than rebuilding over live audit history |
| **V-3** | **Successful sign-ins were recorded with no Company or actor** | Found and fixed in 12.2. `SignInCommandHandler` used the ambient audit overload, which reads `ICurrentIdentity` — empty during an anonymous login request — so the event carried a null Company and an `Anonymous` actor. Invisible while the sink wrote to a log; a real defect the moment events became tenant-scoped, because the row belongs to no tenant and the Company could never see its own sign-ins. The failure path had always built its event explicitly; the success path now does too |

---

## 4. Open decisions this module is exposed to

From CLAUDE.md §5. Listed because Phase 11 built on ground these decisions could still move.

| ID | Question | Exposure |
| --- | --- | --- |
| **D-1** 🔴 | Ratify row-level-security tenancy after prototyping | Every one of the ten tenant-scoped identity tables depends on it. Verified working live (§5 below), but not ratified |
| **DD-2** 🔴 | Connection pooling mode compatible with session-scoped RLS | `app.current_company_id` is a session variable. A transaction-pooling proxy in front of PostgreSQL would break tenant isolation silently — the failure direction is zero rows, so it would read as "the application is broken", not "isolation is off" |
| **TD-1** 🔴 | Adopt .NET 10 LTS | Already built against .NET 10; the decision is unratified, not unmade |
| **TD-5** | PostgreSQL 17 or 18 | Migrations verified against 18.4. `NULLS NOT DISTINCT` (used by `ux_employee_roles_*`) requires 15+ |

---

## 5. Phase 11 assumptions, classified

Every judgement Phase 11 made without an explicit instruction, classified in Milestone 12.1. The
categories matter because they carry different obligations: a **documented decision** needs nothing,
an **accepted implementation assumption** is now ratified and may be relied on, a **deferred
decision** must not be built upon, a **documentation defect** was the document's error, and an
**obsolete** entry records a plan that has been superseded.

### Ratified as documented decisions

The implementation already matched a decision recorded somewhere; 12.1 made the link explicit.

| Assumption | Where it now rests |
| --- | --- |
| Recovery codes hashed with SHA-256, not Argon2id | `09-encryption-strategy` §3's decision tree routes high-entropy platform-generated material away from memory-hard functions |
| `identity` emits audit events directly, with no pipeline | `12-audit-and-compliance` §3.3 already sanctioned direct emission for the Gateway hot path; the new status block extends it explicitly to Phase 11 |
| Architecture rules implemented over reflection, not `NetArchTest` | `04-technology` §12 anticipated it — "the *rules* are the asset, not the library". Now recorded as taken, not merely permitted |
| `ICredentialDirectory`'s four lookups as the only cross-Company reads | `04-tenant-security` §3.4 **path 13**, added in 12.1 |
| `HMACSHA1` inside RFC 6238 despite the SHA-1 prohibition | `04-technology` §8 — the prohibition concerns collision resistance, which HMAC does not rely on |
| No foreign key from `identity` into `tenancy` | ADR-0002 R-6; `06-database` §3.3, corrected in 12.1 |

### Accepted implementation assumptions — now ratified

These were judgement calls with no documented answer. 12.1 documents them; they may now be relied
upon, and changing one is a decision rather than a refactor.

| Assumption | Rationale, now recorded |
| --- | --- |
| `company_authentication_policies` lives in `identity`, not `tenancy.company_settings` | It is authentication policy, owned by the module that enforces it. The alternative reads another module's store on every session validation |
| `failed_login_count` / `lockout_until_utc` on `employee_credentials`, not `employees` | They describe the credential under attack, not the person; a federated path should not be locked by password guessing |
| `permissions`, `role_definitions`, `role_permissions` carry no `company_id` and no RLS | Deployment-wide reference data — a property of the build, not a tenant. `06-database` §5.5 already carves this out |
| `NULLS NOT DISTINCT` on the `employee_roles` uniqueness index | Without it a `NULL` `scope_id` makes every Company-scoped grant distinct, so duplicates are permitted at exactly one scope. This was a live defect, fixed in 11.16 |
| Employee status and enum-like values stored PascalCase | Matches the domain enum names; the check constraints are the authority. Previously documented lowercase — D-8 |
| Framework logging rather than Serilog | Nothing needed a sink Serilog provides; `ILogger<T>` keeps it a registration change later |

### Deferred decisions — do not build on these

| Assumption | Why it is not settled |
| --- | --- |
| `LoggingAuditSink` is where audit events go | It is a placeholder. The destination is decided when the `auditing` module exists — **I-1** |
| One deployment-wide data key serves every Company | `dek_version` is on every row and the envelope is per-row, so the key *source* is the only deferred part — **I-8** |
| TOTP accepts only the current 30-second step, with no skew window | "Clock tolerance only if documented" was the instruction, and nothing documents a window. Widening it is a security decision, not a usability tweak |
| Audit action and target names (`AuditActions`, `AuditTargets`) | Invented, because no vocabulary is documented. Centralised so a later reconciliation renames in one place — **D-4, still open** |

### Documentation defects

All twelve are in §2, with their resolutions.

### Obsolete

| Superseded | By |
| --- | --- |
| `Microsoft.AspNetCore.Cryptography.KeyDerivation` as the password hashing package | `Konscious.Security.Cryptography.Argon2`, per SD-010 |
| `Otp.NET` as the TOTP package | No package; RFC 6238 over the framework's `HMACSHA1` |
| The claim that no Phase 11 documentation defect had been corrected | Milestone 12.1 corrected all but one |

---

## 6. What was verified, and how

Recorded so a later reader knows what "Phase 11 complete" was allowed to mean.

- **Migrations** — all 9 applied to an empty database by a `NOSUPERUSER NOBYPASSRLS` role. No
  pending model changes.
- **Tenant isolation** — verified live, connected *as the unprivileged role* rather than as a
  superuser (a superuser bypasses RLS unconditionally and makes the check meaningless). Isolation
  fails closed in four directions: unset tenant → 0 rows; empty tenant → 0 rows; malformed tenant →
  error; cross-tenant write → refused by policy.
- **RLS coverage** — all 10 tenant-scoped tables have `company_id`, a policy, `ENABLE` *and*
  `FORCE`. The 3 without RLS (`permissions`, `role_definitions`, `role_permissions`) are
  deployment-wide reference data, correctly not tenant-scoped.
- **Configuration** — verified in both directions: a deployment with a 5-byte data key refuses to
  start and names the reason; the same host with a valid key reaches "Now listening".
- **Architecture, dependencies, security** — 42 architecture rules pass as build gates, including
  the six added in 11.23.
- **Tests** — 928 across three assemblies, all passing.

Re-verified in Milestone 12.1 against the same procedure, plus a package vulnerability check
(none) and the two new documentation-drift rules. Test count was then 933.

Re-verified again in Milestone 12.2, which added `auditing.audit_events`:

- **The audit store's own security properties**, all asserted as a `NOSUPERUSER NOBYPASSRLS` role,
  because a superuser bypasses row-level security unconditionally and would make every one of them
  pass vacuously. A Company sees only its own events; an unset tenant sees none, through the parent
  or through a partition addressed directly; a Company cannot write an event belonging to another,
  nor an untenanted one; `UPDATE` and `DELETE` are refused with `permission denied`; `INSERT`
  still works.
- **Mutation-verified**: removing the `REVOKE`, widening the read policy to `USING (true)`, and
  disabling credential redaction each failed exactly the tests that name those properties.
- Test count is now **1,028**.
