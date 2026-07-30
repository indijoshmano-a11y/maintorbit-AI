# MaintOrbit AI

Enterprise AI platform for securely managing, integrating, monitoring, and governing
multiple AI providers (OpenAI, Anthropic, Google Gemini, Azure OpenAI, and others)
through a single unified control plane.

> **Status:** Phase 0 — repository initialization. This repository currently contains
> the agreed structure only. No application code has been written yet.

---

## What this platform does

| Capability | Description |
| --- | --- |
| Provider management | Register AI providers per tenant, store credentials encrypted at rest, sync model catalogues. |
| Unified gateway | One API surface across every provider, with routing, fallback, retries, and circuit breaking. |
| Governance | Policies, guardrails, PII redaction, content filtering, and approval workflows. |
| Usage & cost | Token-level metering, per-tenant cost attribution, quotas and budget alerts. |
| Observability | Request traces, prompt/response logging, latency and error analytics. |
| Auditing | Immutable audit trail for compliance reporting. |
| Multi-tenancy | Organizations, workspaces, RBAC, and platform-issued scoped API keys. |

---

## Technology stack

**Frontend** — Next.js 15, TypeScript, Tailwind CSS, shadcn/ui, Redux Toolkit,
TanStack Query, React Hook Form, Zod, Recharts, TanStack Table

**Backend** — ASP.NET Core 9, C# 13, Entity Framework Core, PostgreSQL, Redis,
JWT, OAuth2, FluentValidation, Mapster, Hangfire, SignalR

**Infrastructure** — Docker, Docker Compose, Nginx, GitHub Actions, Azure VM

---

## Architecture

Clean Architecture layering combined with a Modular Monolith arrangement.

```
Api  ──▶ Application ──▶ Domain
             │              ▲
Infrastructure ─────────────┘   (implements Application/Domain abstractions)
```

Dependencies point inward only. `Domain` references nothing. `Application` depends
on `Domain`. `Infrastructure` and `Api` depend on the inner layers and never the
reverse — enforced by tests in `backend/tests/MaintOrbit.ArchitectureTests`.

Each layer is subdivided by module (`Identity`, `Tenancy`, `Providers`, `Gateway`,
`Governance`, `Usage`, `Billing`, `Observability`, `Auditing`, `Notifications`,
`Analytics`). Modules communicate through published contracts and integration
events, never by reaching into each other's internals — which is what allows any
module to be lifted into its own service later without a rewrite.

Full detail: [`docs/02-architecture/`](docs/02-architecture/).

---

## Repository layout

```
backend/            ASP.NET Core 9 solution (Clean Architecture + modules)
frontend/           Next.js 15 application
vscode-extension/   VS Code client for the platform
docker/             Compose stacks, Nginx, monitoring
docs/               Numbered documentation set (product → deployment → ADRs)
scripts/            Automation for setup, database, deploy, CI, maintenance
tests/              Cross-cutting tests (e2e, load, contract, security, smoke)
.github/            Workflows, issue templates, composite actions
```

---

## Getting started

Setup instructions land in [`docs/05-development/getting-started/`](docs/05-development/getting-started/)
once the solution and workspace files exist. Prerequisites will be:

- .NET 9 SDK
- Node.js 20 LTS (or newer) with pnpm
- Docker Desktop / Docker Engine with Compose v2
- PostgreSQL 16 and Redis 7 (or the provided Compose stack)

---

## Contributing

Branching strategy, commit conventions, and naming rules are documented in
[`docs/05-development/git-workflow/`](docs/05-development/git-workflow/) and
[`docs/05-development/coding-standards/`](docs/05-development/coding-standards/).

---

## License

Proprietary. See [LICENSE](LICENSE).
