# Frontend Technologies

| Field | Value |
| --- | --- |
| Document | Frontend Technologies |
| Version | 1.0 |
| Status | Draft — versions require verification |
| Owner | Engineering |
| Last updated | 2026-07-30 |
| Audience | Frontend Engineering, Security, Architecture Review |
| Phase | 4 — Technology Standards |

---

> **Version verification applies throughout** — see
> [`technology-stack.md`](technology-stack.md) §1. The Node.js runtime finding in §3.3 of
> that document affects everything here.
>
> **These packages are derived from architectural decisions, not from an existing
> `package.json`.** This is the intended dependency set and becomes the reference against
> which the first manifests are written.

---

## 1. Purpose

This document inventories every npm package for the web console and the VS Code
extension, with rationale, lifecycle, and risk for each.

The frontend dependency tree is where dependency *count* becomes a risk in its own right.
A React application routinely resolves to several hundred transitive packages, and each
is a supply-chain surface. This document exists partly to keep the direct dependency list
deliberate, since that is the only part we control.

---

## 2. Scope

**In scope:** npm packages for the web console and VS Code extension; the Node.js runtime;
build tooling; selection rationale and risk.

**Out of scope:** NuGet packages
([`backend-technologies.md`](backend-technologies.md)), infrastructure
([`infrastructure-technologies.md`](infrastructure-technologies.md)), TypeScript style
rules ([`coding-standards.md`](coding-standards.md)).

---

## 3. Runtime and language

### 3.1 Node.js

| Field | Value |
| --- | --- |
| **Purpose** | Build-time toolchain for both clients; server runtime for the Next.js console |
| **Why chosen** | Required by the framework selection; not an independent decision |
| **Alternatives considered** | Bun and Deno — faster and increasingly capable, but Next.js production support on Node is the well-trodden path, and [`../01-product/mission.md`](../01-product/mission.md) §4.6 argues against novelty in anything customer-facing |
| **Version** | **24 LTS recommended** — Phase 0 specified 20, which is past end of life. See [`technology-stack.md`](technology-stack.md) §3.3 |
| **Support lifecycle** | Even-numbered releases become LTS; roughly 30 months of support |
| **Risks** | Running past end of life (TR-2); major upgrades occasionally break native build tooling |
| **Upgrade strategy** | LTS to LTS, planned, pinned by `.nvmrc` and the container base image so local and CI agree |
| **Replacement strategy** | Bound to the framework. An alternative runtime would follow a framework change, not precede it |
| **Security considerations** | Must never run out of support — the console server handles authenticated sessions. Patches applied promptly |
| **Performance considerations** | Server components (FD-001) mean the Node process does real rendering work; it is not merely a static file server, and must be sized accordingly |
| **Cross references** | [ADR-0024](../03-adr/ADR-0024-frontend-stack.md) |

### 3.2 TypeScript

| Field | Value |
| --- | --- |
| **Purpose** | Implementation language for the console and the extension |
| **Why chosen** | Static typing across a large surface; types derived from Zod schemas rather than declared separately, keeping validation and types in one place |
| **Alternatives considered** | JavaScript — rejected; a console with this many data shapes and permission branches needs type checking |
| **Version** | 5.x |
| **Support lifecycle** | Rolling; roughly quarterly minor releases |
| **Risks** | Minor releases occasionally introduce stricter checks that surface as build failures |
| **Upgrade strategy** | Minor upgrades batched monthly; `strict` mode is non-negotiable |
| **Security considerations** | Type safety prevents a defect class but is not a security control |
| **Performance considerations** | Build-time only; incremental builds matter for NFR-MAINT-009's 15-minute budget |
| **Cross references** | [`coding-standards.md`](coding-standards.md) |

---

## 4. Framework and rendering

### 4.1 Next.js

| Field | Value |
| --- | --- |
| **Purpose** | React framework, App Router, server components, server-side rendering |
| **Why chosen** | Server components by default keep the JavaScript payload proportional to interactivity, which is what makes NFR-PERF-009's 2-second interactive load achievable on a data-dense console. Permission-gated server rendering means a surface an Employee may not access is never sent to the browser |
| **Alternatives considered** | Client-only SPA — larger payload, no server-side permission gating. Remix — comparable; Phase 0 selected Next.js. Astro — excellent for content, weaker for a highly interactive console |
| **Version** | 15.x |
| **Support lifecycle** | Roughly annual majors; support for older majors is limited — **this is the main lifecycle risk in the frontend** |
| **Risks** | Annual breaking majors; App Router patterns still evolving; server/client boundary easy to misuse in ways that silently inflate the bundle |
| **Upgrade strategy** | One major behind current is acceptable. Upgrades are planned, architecture-reviewed, and gated on the bundle budget and accessibility audit |
| **Replacement strategy** | Hard — the App Router shapes the application's structure. Mitigated by keeping business logic in `modules/` and `services/` rather than in route files, so a framework change is a re-shell rather than a rewrite |
| **Security considerations** | Server-side permission gating is **defence in depth only**; FR-PERM-001 requires backend enforcement. Server components must never leak secrets into the client bundle — a real and easy mistake |
| **Performance considerations** | Server components by default; client components by exception. Bundle size budget enforced in CI |
| **Cross references** | [ADR-0024](../03-adr/ADR-0024-frontend-stack.md) |

| Package | Purpose | Version | Licence | Risk |
| --- | --- | --- | --- | --- |
| `next` | Framework | 15.x | MIT | 🟡 |
| `react` | UI library | 19.x | MIT | 🟢 |
| `react-dom` | DOM renderer | 19.x | MIT | 🟢 |

---

## 5. State management

Per [ADR-0024](../03-adr/ADR-0024-frontend-stack.md), two libraries with a **binding
division**: TanStack Query owns all server state; Redux owns only client state. Server
data is never copied into Redux.

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `@tanstack/react-query` | **Server state** — caching, revalidation, mutations | 5.x | MIT | 🟢 | Query keys include the Company identifier (FD-005) |
| `@tanstack/react-query-devtools` | Development tooling | 5.x | MIT | 🟢 | Development only |
| `@reduxjs/toolkit` | **Client state** — session, filters, chat composition, notifications | 2.x | MIT | 🟢 | |
| `react-redux` | React bindings | 9.x | MIT | 🟢 | |

**Why the division needs enforcing.** These libraries overlap, and the failure mode —
server data in Redux, cache invalidation hand-rolled, two sources of truth that drift — is
assessed as **high likelihood** in ADR-0024 R-1. A lint rule should be added if a
mechanical check is feasible; otherwise it is a review gate.

---

## 6. UI and styling

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `tailwindcss` | Utility CSS | 4.x | MIT | 🟢 | v4 changed configuration substantially — verify migration path |
| `@tailwindcss/postcss` | PostCSS integration | 4.x | MIT | 🟢 | |
| `postcss` | CSS processing | 8.x | MIT | 🟢 | |
| `class-variance-authority` | Component variant API | 0.7.x | Apache 2.0 | 🟡 | Pre-1.0; small but widely used |
| `clsx` | Conditional class names | 2.x | MIT | 🟢 | |
| `tailwind-merge` | Tailwind class conflict resolution | 2.x / 3.x | MIT | 🟢 | |
| `lucide-react` | Icon set | Current | ISC | 🟢 | |
| `@radix-ui/react-*` | Accessible primitives *(via shadcn/ui)* | 1.x | MIT | 🟢 | **The accessibility foundation** — see below |
| `next-themes` | Light and dark theme | 0.4.x | MIT | 🟢 | |

### 6.1 shadcn/ui — vendored, not a dependency

**shadcn/ui components are copied into `components/ui/` rather than installed.** They are
source, not a package, which is the model the project uses.

| Consequence | Detail |
| --- | --- |
| No version to track | Components are vendored source under our control |
| **No automatic updates** | Upstream fixes — including accessibility fixes — must be pulled deliberately |
| Customization via tokens | FD-008 — primitives are not edited, so upstream updates stay viable |
| The real dependency is Radix | Radix UI primitives are the actual versioned dependency and the accessibility foundation |

**This is a maintenance obligation that is easy to forget.** Vendored components do not
appear in dependency scans, do not appear in vulnerability reports, and do not prompt
upgrade notifications. A periodic review against upstream is required, and it belongs in
§11.

---

## 7. Forms, validation, data display

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `react-hook-form` | Form state | 7.x | MIT | 🟢 | |
| `@hookform/resolvers` | Zod integration | 3.x / 5.x | MIT | 🟢 | |
| `zod` | Schema validation; **single client-side schema source** | 3.x / 4.x | MIT | 🟢 | Types derived from schemas, never declared separately |
| `@tanstack/react-table` | Headless table | 8.x | MIT | 🟢 | One shared wrapper (FD-009) |
| `@tanstack/react-virtual` | List virtualization | 3.x | MIT | 🟢 | Chat message list; long audit tables |
| `recharts` | Charts | 2.x / 3.x | MIT | 🟡 | Wrappers must not assume raw data arrives in the browser |
| `date-fns` | Date handling | 4.x | MIT | 🟢 | All timestamps UTC-stored, displayed in the user's zone (FR-X-003) |

**Client validation is never the enforcement point.** The server always revalidates
(ADR-0024 §7). Client and server validation are written in different languages against
different schemas and **will drift** — this is accepted, with the server authoritative and
its field errors structured well enough to attach to the correct input.

---

## 8. Real-time and content rendering

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `@microsoft/signalr` | SignalR client | Runtime major | MIT | 🟢 | Push is an **invalidation signal**, not a data channel (FD-004) |
| `react-markdown` | Markdown rendering | 9.x / 10.x | MIT | 🟡 | **Must tolerate incomplete input** — see below |
| `remark-gfm` | GitHub-flavoured markdown | 4.x | MIT | 🟢 | |
| `rehype-sanitize` | **HTML sanitization** | 6.x | MIT | 🟢 | **Security-critical** — model output is untrusted content |
| `shiki` *or* `react-syntax-highlighter` | Code highlighting | Current | MIT | 🟡 | Bundle size is the deciding factor |

> **Two things here matter more than they look.**
>
> **Markdown parsing must tolerate incomplete input as a normal condition.** Streamed
> content arrives mid-token — an unterminated code fence, a half-written table. A parser
> that fails on incomplete input produces visible flicker on every chunk, which reads as
> low quality and directly undermines AI Chat's competitive position against consumer
> products. This is a functional requirement of the rendering stack, not a nicety.
>
> **Sanitization is not optional.** Model completions are untrusted input rendered into
> the DOM. Without sanitization, a completion containing markup is a cross-site scripting
> vector — and a prompt-injected model can be induced to produce one. This is the most
> direct injection risk in the console.

---

## 9. VS Code extension

Per [ADR-0025](../03-adr/ADR-0025-extension-auth.md), a **deliberately minimal** dependency
set. The extension is a distributed artifact with access to a developer's entire
workspace; every dependency is a supply-chain surface on a machine holding source code and
credentials.

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `@types/vscode` | Extension API types | Matching engine | MIT | 🟢 | Types only |
| `esbuild` | Bundler | 0.2x | MIT | 🟢 | Fast; produces a small artifact |
| `@vscode/test-electron` | Integration tests | 2.x | MIT | 🟢 | Development only |
| `@vscode/vsce` | Packaging and publishing | 3.x | MIT | 🟢 | Development only |
| `typescript` | Language | 5.x | Apache 2.0 | 🟢 | Development only |

**No HTTP client dependency.** The platform's fetch implementation is used directly.

**No credential library.** VS Code `SecretStorage` — the OS keychain — is the only
credential store (XD-002).

**The webview has no network dependencies.** It holds no credentials and makes no network
calls; all transport happens in the extension host (XD-004). A content security policy
enforces this.

---

## 10. Build and quality tooling

| Package | Purpose | Version | Licence | Risk | Notes |
| --- | --- | --- | --- | --- | --- |
| `eslint` | Linting | 9.x | MIT | 🟢 | Flat config |
| `@typescript-eslint/*` | TypeScript rules | 8.x | MIT | 🟢 | |
| `eslint-config-next` | Framework rules | Matching Next | MIT | 🟢 | |
| `prettier` | Formatting | 3.x | MIT | 🟢 | |
| `vitest` | Unit tests | 2.x / 3.x | MIT | 🟢 | |
| `@testing-library/react` | Component tests | 16.x | MIT | 🟢 | |
| `@testing-library/user-event` | Interaction tests | 14.x | MIT | 🟢 | |
| `@playwright/test` | End-to-end tests | 1.x | Apache 2.0 | 🟢 | `tests/e2e/` |
| `@axe-core/playwright` | **Accessibility audit** | 4.x | MPL 2.0 | 🟢 | **Build gate** — NFR-USE-001 |
| `husky` | Git hooks | 9.x | MIT | 🟢 | |
| `lint-staged` | Staged-file linting | 15.x | MIT | 🟢 | |
| `@next/bundle-analyzer` | **Bundle size budget** | Matching Next | MIT | 🟢 | **Build gate** — NFR-PERF-009 |

**Two of these are build gates, not conveniences.** The accessibility audit enforces
NFR-USE-001 (WCAG 2.1 AA), and the bundle analyzer enforces the payload discipline that
FD-001's server-component-by-default rule exists to achieve. Both are listed in
[ADR-0019](../03-adr/ADR-0019-github-actions.md).

---

## 11. Packages requiring long-term maintenance attention

| Item | Why it needs attention | Consequence if neglected | Mitigation |
| --- | --- | --- | --- |
| **Vendored shadcn/ui components** | **Not in any dependency scan.** No update notifications, no vulnerability reports | Upstream accessibility and security fixes never arrive | Scheduled review against upstream; named owner |
| **`next`** | Annual breaking majors; limited support for older majors | Falling two or more majors behind makes upgrades compounding and painful | Stay within one major of current; upgrade planned each cycle |
| **Markdown and sanitization chain** | Renders untrusted model output into the DOM | Cross-site scripting via a prompt-injected completion | Sanitization is mandatory and covered by test; patches applied promptly |
| **`class-variance-authority`** | Pre-1.0, small, widely depended upon by the component layer | Component variant API needs replacing across the UI | Small surface; replaceable with modest effort |
| **`recharts`** | Charting libraries are frequently abandoned or rewritten | Every chart needs rebuilding | One shared wrapper (FD-009) confines the blast radius |
| **Transitive tree depth** | Several hundred packages; the real supply-chain surface | Vulnerability or compromise via a package nobody chose | Build-gating scan; lockfiles committed; direct dependencies kept deliberate |
| **`tailwindcss` v4 migration** | v4 changed configuration substantially | Config drift; upgrade friction | Adopt v4 conventions from the start rather than migrating later |

**The frontend's characteristic risk is different from the backend's.** The backend's
concerns are a few small libraries carrying large architectural weight. The frontend's is
**breadth** — a large transitive tree where no single package is critical but the
aggregate surface is substantial, plus vendored code that no tool tracks.

---

## 12. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Node.js 20 past end of life (TR-2) | High | Certain if unaddressed | Decision TD-1 — Node.js 24 LTS |
| R-2 | Unsanitized model output renders as markup — XSS | **Critical** | Low | `rehype-sanitize` mandatory; covered by test; security review |
| R-3 | Server data leaks into Redux, creating drifting sources of truth | High | **High** | ADR-0024 FD-003; review gate; lint rule if feasible |
| R-4 | Vendored components never updated | Medium | **High** | Scheduled review; named owner |
| R-5 | Next.js major upgrade deferred until compounding | Medium | High | One major behind current; planned upgrades |
| R-6 | Transitive vulnerability | Medium | High | Build-gating scan; lockfiles committed |
| R-7 | Server/client boundary misused, inflating the bundle | Medium | High | Bundle budget gate |
| R-8 | Markdown parser fails on partial input, producing streaming flicker | Medium | Medium | Verified against streamed content in test, not only complete documents |
| R-9 | A secret leaks into the client bundle via a server component | High | Low | Review gate; secret scanning; build-time check |

---

## 13. Cross references

| Document | Relationship |
| --- | --- |
| [`technology-stack.md`](technology-stack.md) | Master inventory; the Node.js finding |
| [`backend-technologies.md`](backend-technologies.md) | NuGet inventory |
| [`coding-standards.md`](coding-standards.md) | TypeScript and React conventions |
| [`dependency-policy.md`](dependency-policy.md) | How these are added and reviewed |
| [`package-policy.md`](package-policy.md) | Lockfiles and version management |
| [`support-lifecycle.md`](support-lifecycle.md) | End-of-support calendar |
| [`../03-adr/ADR-0024-frontend-stack.md`](../03-adr/ADR-0024-frontend-stack.md) | State division and rendering strategy |
| [`../03-adr/ADR-0025-extension-auth.md`](../03-adr/ADR-0025-extension-auth.md) | Extension minimalism rationale |
| [`../02-architecture/frontend-architecture-overview.md`](../02-architecture/frontend-architecture-overview.md) | How these are used |
