# CLAUDE.md — Engineering Handbook

**This is a routing document, not a summary.** ~29,900 lines of specification exist across 85
documents in `docs/`. This file says which one answers your question and which rules must never
be broken. It does not restate their content — a summary that drifts from its source is worse
than no summary.

---

# 1. Project Overview

**MaintOrbit AI** — an enterprise AI platform giving organizations one governed, observable,
provider-neutral layer through which every AI request passes. It brokers access to AI providers
(OpenAI, Anthropic, Google Gemini); it never trains or serves models.

| Aspect | Value |
| --- | --- |
| **Backend** | ASP.NET Core, C#, EF Core, PostgreSQL, Redis, Hangfire, SignalR, FluentValidation, Mapster |
| **Frontend** | Next.js, React, TypeScript, Tailwind, shadcn/ui, Redux Toolkit, TanStack Query, Zod |
| **Infrastructure** | Docker, Nginx, Azure VMs, GitHub Actions |
| **Architecture** | **Clean Architecture + Modular Monolith**, designed for later service extraction |

⚠️ **Runtime versions are contested — see TD-1 in §5. Do not assume the Phase 0 selection.**

**Layout:** `backend/` (5 projects + tests) · `frontend/` · `vscode-extension/` · `docker/` ·
`docs/` · `scripts/` · `tests/` · `.github/`. Clients: web console, VS Code Extension, and
customer server applications.

---

# 2. Read Before Writing

**Mandatory before generating any code.** The order matters — each layer constrains the next.

| # | Read | Why |
| --- | --- | --- |
| 1 | `docs/01-product/` | What is being built and for whom. **`glossary.md` is normative** |
| 2 | `docs/02-architecture/` | How the system is structured |
| 3 | `docs/03-adr/` | Why, and what was rejected |
| 4 | `docs/04-technology/` | Which technologies and packages are permitted |
| 5 | `docs/05-security/` | Controls that cannot be bypassed |
| 6 | `docs/06-database/` | Schema, tenancy, indexing |
| 7 | `docs/07-api/` | Contract shape and conventions |
| 8 | `docs/08-development/` | How to write, test, and complete work |

**Minimum for any backend change:** the relevant ADR + `06-database` §5 (tenancy) +
`05-security/security-architecture.md` §7–9 + `08-development/coding-standards.md`.
**These documents are cross-referenced and self-consistent** — changes made without reading
them will contradict them.

---

# 3. Documentation Map

| Question | Document |
| --- | --- |
| What are we building? Who for? | `docs/01-product/product-requirements.md`, `target-users.md`, `user-personas.md` |
| **What does this term mean?** | **`docs/01-product/glossary.md` — normative; prohibited terms in §10** |
| What is in MVP vs later? | `docs/01-product/mvp-features.md`, `future-roadmap.md` |
| What performance/quality target applies? | `docs/01-product/non-functional-requirements.md` |
| How is the system structured? | `docs/02-architecture/system-architecture.md` |
| How are layers and modules organized? | `docs/02-architecture/backend-architecture-overview.md` |
| What talks to what? What breaks if X fails? | `docs/02-architecture/component-diagram.md` §3.6 |
| How does the Gateway work? | `docs/02-architecture/ai-gateway-architecture.md` |
| How does a request flow end to end? | `docs/02-architecture/request-flow.md` |
| Frontend / extension design | `docs/02-architecture/frontend-architecture-overview.md`, `vscode-extension-architecture.md` |
| Deployment and scaling | `docs/02-architecture/deployment-architecture.md`, `scalability-strategy.md` |
| **Why was X decided? What was rejected?** | **`docs/03-adr/` — index in `README.md`** |
| Which package? May I add a dependency? | `docs/04-technology/backend-technologies.md`, `frontend-technologies.md`, `dependency-policy.md` |
| How do versions and upgrades work? | `docs/04-technology/versioning-policy.md`, `support-lifecycle.md` |
| **Security model, controls, threats** | **`docs/05-security/security-architecture.md`, `threat-model.md`** |
| Compliance posture · verification items | `docs/05-security/compliance.md`, `security-checklist.md` |
| Schema, tenancy, indexes, partitioning | `docs/06-database/database-design.md` |
| API conventions, errors, pagination | `docs/07-api/api-specification.md` |
| How do I write this code? | `docs/08-development/coding-standards.md` + `docs/04-technology/coding-standards.md` |
| Branching, commits, PRs | `docs/08-development/git-workflow.md` |
| What tests are required? | `docs/08-development/testing-strategy.md` |
| **When is it done?** | **`docs/08-development/definition-of-done.md`** |

> **Two quirks.** `docs/05-security/` holds two overlapping sets — 15 numbered documents and 4
> consolidated ones; both current, prefer the consolidated four unless you need depth. And six
> directory numbers are duplicated with empty Phase 0 scaffolding. **This table is
> authoritative.**

---

# 4. Frozen Decisions

**Must not change without explicit instruction.** Details live in the referenced document —
do not restate them, read them.

| Decision | Source |
| --- | --- |
| Clean Architecture; dependencies point inward | `03-adr/ADR-0001` |
| **Modular Monolith; acyclic module graph; no cross-module internals** | `03-adr/ADR-0002` |
| PostgreSQL as the single system of record; schema per module | `03-adr/ADR-0004` |
| **Tenant isolation by row-level security, enforced below the application layer** | `03-adr/ADR-0005` |
| **No foreign keys across module schemas** | `06-database` §3.3 |
| **UUIDv7 primary keys; composite PK on partitioned tables** | `06-database` §1.6 |
| Redis for cache, counters, streams, backplane | `03-adr/ADR-0006` |
| **JWT 15-min access tokens; permissions NEVER in claims; stateful refresh with reuse detection** | `03-adr/ADR-0007`, `05-security` §10, §15 |
| **Envelope encryption; no plaintext credential retrieval path exists in code** | `03-adr/ADR-0008`, `05-security` §16 |
| Provider abstraction: narrow port + opaque pass-through | `03-adr/ADR-0009` |
| **Gateway hot path: no synchronous relational access; bypasses the dispatcher** | `03-adr/ADR-0010` |
| **Usage and audit records are immutable and NEVER sampled** | `03-adr/ADR-0011`, `05-security` §30 |
| In-house CQRS dispatcher (fixed pipeline order); transactional outbox | `03-adr/ADR-0012`, `ADR-0013` |
| **REST `/api/v1` URL versioning; OpenAI-compatible Gateway on a separate base path** | `03-adr/ADR-0016`, `07-api` §1.4, §9 |
| **Deny by default; permission-based authorization; roles are presets, never branched on by name** | `05-security` §7–8 |
| **Content retention off by default, opt-in per Team** | `05-security` §8 |
| Fail-closed security controls; fail-open metering | `03-adr/ADR-0021` |
| **Keyset pagination on ledger/audit; no total counts** | `07-api` §5.4 |
| Money as `decimal` / string-encoded JSON, never floating point | `06-database` §1.2, `07-api` §1.6 |
| **Trunk-based development; squash merge; Conventional Commits** | `08-development/git-workflow.md` |
| Architecture tests AT-1…AT-12 are build gates | `02-architecture/backend-architecture-overview.md` §8 |
| **Documentation drift is a build gate too** — a table without a definition, or a package outside the inventory, fails the build | `08-development/identity-foundation-deferred-work.md` §2 |

---

# 5. Open Decisions

**Do not build on these as though settled. If a task depends on one, say so and stop.**

| ID | Question | Defined in |
| --- | --- | --- |
| **D-1** 🔴 | Ratify row-level-security tenancy after prototyping — **blocks all schema work** | `02-architecture/system-architecture.md` §8 |
| **DD-2** 🔴 | Connection pooling mode compatible with session-scoped RLS | `02-architecture/deployment-architecture.md` §8 |
| **D-8** 🔴 | Does `usage_records` carry `parent_trace_id` at v1.0? — **irreversible if omitted** | `02-architecture/system-architecture.md` §8 |
| **D-4** 🔴 | What is the billable unit? — blocks Usage/Billing model and API | same |
| **TD-1** 🔴 | Adopt .NET 10 LTS and Node.js 24 LTS — **stated runtime is out of support** | `04-technology/technology-stack.md` §9 |
| D-2 | Ingestion durability: amend the requirement or fund higher durability | `02-architecture/system-architecture.md` §8 |
| D-3 | Gateway behaviour during a Redis outage — may budget enforcement fail open? | same |
| D-5 | Default retention periods | same |
| D-6 | Key custodian + **tested backup procedure + escrow custody** | same |
| D-7 | Confirm the Chat module addition to the Phase 0 structure | same |
| DD-1 | Two-VM topology, or amend the availability target | `02-architecture/deployment-architecture.md` §8 |
| DD-5 | Infrastructure as code from first deployment | same |
| TD-2 | Standardize on Valkey in place of Redis (licence) | `04-technology/technology-stack.md` §9 |
| TD-3 / TD-4 / TD-5 | Hangfire LGPL · payment+email providers · PostgreSQL 17 or 18 | same |
| **SD-018** | Legal confirmation that pseudonymized erasure satisfies applicable law | `05-security/security-architecture.md` §35.1 |

---

# 6. Engineering Rules

1. **Never invent architecture.** If a decision is not documented, ask — do not choose.
2. **Never contradict an ADR.** If one seems wrong, say so; do not work around it.
3. **Never bypass a security control.** They are listed in §9.
4. **Never create duplicate documentation.** Extend the existing document or reference it.
5. **Keep modules independent.** Published contracts and integration events only.
6. **Follow the coding standards** — both `08-development/coding-standards.md` (practice) and
   `04-technology/coding-standards.md` (language rules).
7. **Use glossary terminology exactly.** `Employee` not User; `Company` not Organization;
   `Platform API Key` and `Provider Credential` are never both "API key".
8. **Update documentation in the same change** when implementation alters architecture, API
   surface, or schema.
9. **Every change meets the Definition of Done** before it is proposed as complete.
10. **Say when you are blocked.** A task depending on an open decision in §5 stops there.

---

# 7. Module Boundaries

**Twelve modules.** One schema each; one namespace each.

| Module | Owns | Commonly called |
| --- | --- | --- |
| `identity` | Employees, credentials, sessions, roles, permissions, Platform API Keys | *Authentication, Employee, Developer Platform* |
| `tenancy` | Companies, Teams, memberships, settings | *Company, Settings* |
| `providers` | Provider Connections, credentials, model catalogue | |
| `gateway` | Routing policies, inference execution | *AI Gateway* |
| `chat` | Conversations, Messages | *Chat* |
| `governance` | Policies, content retention configuration | |
| `usage` | Usage/Cost Records, Budgets, Quotas | *Usage* |
| `analytics` | Projections only — no authoritative state | *Analytics* |
| `billing` | Plans, subscriptions, invoices | *Billing* |
| `auditing` | Audit Events, legal holds | |
| `notifications` | Preferences, deliveries | *Notifications* |
| `observability` | Decision Records | |

**Boundaries must remain intact.** A module references another's **published contracts only** —
never entities, repositories, or internal services; never its data store; never a foreign key
into its schema. **The dependency graph must stay acyclic** (AT-3, build gate). This is what
makes later service extraction possible; violating it forecloses it permanently.

---

# 8. Implementation Workflow

```
Read documentation (§2)
        ↓
Design within existing architecture — no new patterns
        ↓
Implement — coding standards, module boundaries, security controls
        ↓
Write tests — lowest level that verifies the behaviour
        ↓
Verify Definition of Done — docs/08-development/definition-of-done.md
        ↓
Commit — Conventional Commits, scope = module
```

**Before implementing:** confirm no §5 decision blocks the work, which module owns the
behaviour, and the permission and tenant scope. **Before completing:** tenant isolation tested ·
audit events emitted · authorization at execution · migration backward-compatible · API
specification updated if the surface changed.

---

# 9. What Claude Must Never Do

| Never | Because |
| --- | --- |
| Change an ADR without instruction | They are the record of why |
| Introduce new infrastructure or a new datastore | `04-technology/dependency-policy.md` gates this |
| Replace PostgreSQL or Redis | Frozen |
| Split into microservices | Modular monolith is deliberate; extraction is a later, measured decision |
| Change API versioning or path conventions | Breaks every integration |
| **Bypass tenant isolation** | `company_id` + RLS policy on every tenant-scoped relation |
| **Add a table without a row-level security policy in the same migration** | A table without one is a leak |
| **Store or return a Provider Credential in plaintext** | No retrieval path exists — do not create one |
| **Put permissions in a JWT** | Breaks the 60-second revocation requirement |
| **Add an update or delete path to audit records** | Immutability is structural |
| **Sample usage or audit records** | Never, under any load |
| **Branch authorization on a role name** | Evaluate permissions |
| Derive the tenant from request input | Server-side from the credential only |
| Log credentials, tokens, prompts, or completions | Absent by construction, not masked |
| Disable certificate validation, including in development | — |
| Use floating point for money | 2% cost tolerance cannot survive it |
| Add a foreign key across module schemas | Forecloses extraction |
| Create a thirteenth module or duplicate an existing one | §7 |
| Disable an architecture test or CI gate | Requires architecture review |
| Generate documentation duplicating an existing document | Two sources will drift |

---

# 10. References

| Folder | Contents |
| --- | --- |
| `docs/01-product/` | Vision, mission, personas, requirements, NFRs, **glossary** |
| `docs/02-architecture/` | System, components, gateway, auth, flows, deployment, scalability |
| `docs/03-adr/` | 25 Architecture Decision Records + index |
| `docs/04-technology/` | Stack, packages, dependency/package/versioning policy, lifecycle |
| `docs/05-security/` | Security architecture, threat model, compliance, checklist |
| `docs/06-database/` | Logical database design |
| `docs/07-api/` | REST and Gateway API specification |
| `docs/08-development/` | Coding standards, git workflow, testing, definition of done, **identity deferred-work register** |
| `README.md` | Repository structure and setup |

**Empty directories** (`03-database`, `04-api`, `05-development`, `06-deployment`, `07-adr`,
`08-assets`) are Phase 0 scaffolding — §3 is authoritative.
