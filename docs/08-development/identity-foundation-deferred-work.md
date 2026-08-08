# Identity Foundation — Deferred Work Register

**Status:** Phase 11 complete (milestones 11.1–11.24), verified 2026-08-08.
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
| **I-1** | The **`auditing` module** — `audit_events` table, RLS policy, append-only enforcement (AU-1), retention (AU-7), legal hold (AU-9) | `LoggingAuditSink` writes events to the log. It is named as a placeholder, not a store | **Highest-consequence item here.** Audit events are emitted correctly and go nowhere durable. ADR-0011's immutability guarantee is currently unmet — there is no append-only relation to enforce it on |
| **I-2** | **Email delivery** for password reset and email verification | `UndeliveredPasswordResetNotifier` / `UndeliveredEmailVerificationNotifier` log the gap rather than sending | Both flows are complete and correct server-side but cannot reach an Employee. Verification gates account activation (FR-AUTH-013), so this blocks real onboarding |
| **I-3** | The **ADR-0012 CQRS dispatcher** and its nine ordered behaviours | Handlers are registered one per use case against `ICommandHandler`/`IQueryHandler` and invoked directly from endpoints | Audit emission, validation, and transaction boundaries are each implemented per-handler. §3.3 warns this makes coverage "a function of developer discipline" — `AuditEmissionTests` is the deliberate stand-in for the pipeline's guarantee (see D-4 below) |
| **I-4** | The **ADR-0013 transactional outbox** | Handlers call `SaveChangesAsync` directly; audit emission is fail-open per ADR-0021 | An audit event can be lost if the sink fails after the transaction commits. This is classified fail-open deliberately, and every loss is logged as an AU-8 incident (EventId 1600) — but it is not the at-least-once delivery the outbox would give |
| **I-5** | **Platform API Keys** — listed in CLAUDE.md §7 as owned by `identity` | Nothing. `Authentication/ApiKeys/` is an empty directory | Not required by any Phase 11 milestone. Named here only so the empty directory is not mistaken for a partial implementation |
| **I-6** | **OAuth2 / SSO** | Nothing. `Authentication/OAuth2/` is an empty directory | As above |
| **I-7** | **Rate limiting** on authentication endpoints | Failed-login counting and account lockout (11.18) bound per-account guessing | Lockout bounds attempts against *one* account. It does nothing against spraying one password across many accounts, which is the attack it most resembles |
| **I-8** | **Per-Company data keys** (SD-012 `dek_version` is on the row already) | `DeploymentDataKeyStore` returns one deployment-wide key for every Company | The envelope scheme and the version column are correct and ready. Only the key *source* is deployment-wide, so a per-Company key rotation is a store swap, not a schema change |

---

## 2. Documentation defects found while building

These are defects in `docs/`, not in the code. Each was worked around by following the frozen
decision the document contradicts, and each was reported in the milestone that hit it. **None has
been corrected**, because correcting specification documents was outside every milestone's scope.

| # | Document | Defect |
| --- | --- | --- |
| **D-1** | `06-database` §3.3 | `employees.company_id → companies.id` is marked "same schema". `companies` belongs to `tenancy`, so this is a cross-module foreign key — which §3.3 itself, and CLAUDE.md §9, forbid. Implemented without the FK |
| **D-2** | `04-technology` §8 | Names a PBKDF2-only package for password hashing, contradicting SD-010's Argon2id. Implemented per SD-010 |
| **D-3** | `05-security` §3.4 | The cross-Company access path table omits authentication, which is itself a cross-Company read — an Employee's email must be resolved before their tenant is known. Implemented as `ICredentialDirectory`, the one documented elevated path |
| **D-4** | `05-security` §3.3 | Assigns audit emission to pipeline position 8 of a pipeline that does not exist (see I-3), and defines no vocabulary of audit action names. Action and target names were centralised in `AuditActions`/`AuditTargets` so a later reconciliation renames in one place |
| **D-5** | `06-database` §4.2 | **Three tables have no definition at all**: `password_reset_tokens`, `email_verification_tokens`, `company_authentication_policies` |
| **D-6** | `06-database` §4.2 | **Six tables are described in prose but carry no column definition**, unlike `sessions` which has one: `mfa_enrollments`, `mfa_recovery_codes`, `permissions`, `role_definitions`, `role_permissions`, `employee_roles` |

> **Recommendation, restated from milestones 11.17, 11.19, 11.20, 11.21 and 11.22.** These are now
> six defects spanning nine undocumented tables. A reconciliation milestone should precede the next
> feature. The specification is the artifact implementation is checked against; at this size it has
> started checking the other way.

---

## 3. Verification residue

Found during 11.24's end-to-end verification. Neither is a defect in delivered behaviour.

| # | Finding | Detail |
| --- | --- | --- |
| **V-1** | **Three empty test projects** | `MaintOrbit.TestUtilities`, `MaintOrbit.Application.UnitTests` and `MaintOrbit.Infrastructure.IntegrationTests` contain zero `.cs` files. They restore, build, and appear in package listings, but run no tests. All Application and Infrastructure behaviour is currently covered through `MaintOrbit.Api.FunctionalTests` instead — a level higher than `08-development/testing-strategy.md` names for it. The coverage is real; its location is not what the strategy describes |
| **V-2** | **`xunit` 2.9.3 is deprecated** | Marked `Legacy`, superseded by `xunit.v3`. Test projects only — no production dependency. No vulnerable packages exist anywhere in the solution |

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

## 5. What was verified, and how

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
