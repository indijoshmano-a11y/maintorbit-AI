# Product Requirements Document

| Field | Value |
| --- | --- |
| Document | Product Requirements Document (master) |
| Version | 1.0 |
| Status | Draft — pending engineering review and open-question resolution |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, QA, Security |
| Supersedes | — |

---

## 1. Purpose

This is the master requirements document for MaintOrbit AI. It defines *what* the
platform must do, expressed as numbered, testable requirements traceable to personas
and problems.

It deliberately does not define *how*. It contains no API design, no database schema,
no technology selection, and no interface specification. Those are Phase 2 and 3
deliverables and belong in `docs/02-architecture/`, `docs/03-database/`, and
`docs/04-api/` respectively.

**How to use this document.** Every requirement has a stable identifier. Design
documents, tickets, tests, and ADRs should reference those identifiers. A capability
that appears in code without a corresponding requirement here is either scope creep or
a documentation gap — both need resolving.

---

## 2. Overview

MaintOrbit AI is an enterprise AI platform providing a single governed layer between
an organization and the AI providers it uses. It serves two populations from one
identity model, one policy engine, and one usage ledger: developers consuming a
unified API, and employees using a first-party chat interface.

The platform comprises fourteen capability areas:

| # | Capability area | Requirement prefix | Serves |
| --- | --- | --- | --- |
| 1 | Enterprise Authentication | `FR-AUTH` | All personas |
| 2 | Multi-Tenancy & Organization | `FR-TEN` | P-01, P-05, P-07 |
| 3 | AI Providers | `FR-PROV` | P-01, P-02, P-08 |
| 4 | AI Gateway | `FR-GW` | P-02, P-03, P-08 |
| 5 | AI Chat | `FR-CHAT` | P-04 |
| 6 | Usage Tracking | `FR-USG` | P-01, P-05, P-06, P-08 |
| 7 | Cost Tracking | `FR-COST` | P-01, P-05 |
| 8 | Analytics | `FR-ANL` | P-01, P-05, P-06, P-08 |
| 9 | Billing | `FR-BILL` | P-05 |
| 10 | Developer APIs | `FR-API` | P-02, P-03, P-08 |
| 11 | VS Code Extension | `FR-EXT` | P-03, P-08 |
| 12 | Governance | `FR-GOV` | P-06 |
| 13 | Auditing | `FR-AUD` | P-06, P-07 |
| 14 | Notifications | `FR-NOT` | P-01, P-05, P-06 |

---

## 3. Conceptual model

The vocabulary below is normative. Every document, design, and implementation uses
these terms with these meanings. Full definitions are in [`glossary.md`](glossary.md).

### 3.1 Organizational hierarchy

```
Company  (the tenant — the isolation boundary)
   │
   ├── Employee        (a user account belonging to exactly one Company)
   │
   └── Team            (a grouping within a Company)
         └── Team Membership   (an Employee's participation in a Team)
```

- A **Company** is the unit of tenancy, contracting, billing, and data isolation. No
  data is ever visible across Companies.
- An **Employee** belongs to exactly one Company. Cross-Company identity is out of
  scope; a person working with two Companies holds two accounts.
- A **Team** is a grouping within a Company used for cost attribution, budget scoping,
  policy scoping, and access control. Teams may nest to a bounded depth.
- An Employee may belong to zero or more Teams.

### 3.2 AI resources

```
Company
   ├── Provider Connection   (a configured credential for one AI provider)
   │      └── Model          (a model made available by that connection)
   │
   ├── Routing Policy        (rules selecting a model for a request)
   ├── Platform API Key      (a MaintOrbit-issued credential for Developer APIs)
   └── Governance Policy     (rules evaluated against request content and metadata)
```

### 3.3 Activity records

```
Gateway Request  ──▶ Usage Record  ──▶ Cost Record
       │
       └──────────▶ Audit Event
```

Every request that passes through the platform produces exactly one Usage Record and
at least one Audit Event. Cost Records derive from Usage Records and provider pricing.

### 3.4 Attribution chain

Every Usage Record is attributable to: Company → Team → Employee → Surface (Gateway,
Chat, or Extension) → Provider Connection → Model. This chain is the foundation of
cost attribution, analytics, and audit. **No request may be accepted that cannot be
fully attributed.** This is a hard constraint, not a preference.

---

## 4. Requirement conventions

**Identifier format:** `FR-<AREA>-<NNN>` for functional requirements,
`NFR-<CATEGORY>-<NNN>` for non-functional (see
[`non-functional-requirements.md`](non-functional-requirements.md)). Identifiers are
permanent. A withdrawn requirement is marked withdrawn, never reused.

**Priority (MoSCoW):**

| Code | Meaning |
| --- | --- |
| **M** | Must — the release is not viable without it |
| **S** | Should — significant value; omitted only under schedule pressure |
| **C** | Could — desirable; first to be cut |
| **W** | Won't (this release) — explicitly deferred, recorded to prevent re-litigation |

**Release:** `MVP`, `v1.1`, `v1.2`, `v2.0`, or `Later`. MVP contents are detailed in
[`mvp-features.md`](mvp-features.md); later releases in
[`future-roadmap.md`](future-roadmap.md).

**Language:** *must* denotes a mandatory requirement; *should* a strong
recommendation; *may* an option.

---

## 5. Product scope

### 5.1 In scope

- Multi-tenant organizational management: Companies, Teams, Employees, roles
- Enterprise authentication and session management
- AI provider connection and credential management
- A unified inference gateway with routing, failover, and streaming
- A first-party AI Chat interface for all employees
- Usage metering and cost attribution
- Analytics and reporting for engineering, finance, and compliance
- Subscription and billing management
- Public Developer APIs with platform-issued credentials
- A VS Code extension
- Governance policy definition and enforcement
- Immutable audit logging
- Notification and alerting

### 5.2 Out of scope

Permanently out of scope per [`vision.md`](vision.md) §6 and
[`problem-statement.md`](problem-statement.md) §9: training or serving foundation
models, application development tooling beyond the extension, general-purpose API
management, data warehousing, endpoint or network security.

Out of scope for MVP but planned: SAML SSO, SCIM provisioning, self-hosted deployment,
advanced governance, evaluations, prompt management, agent workloads.

---

## 6. Role and permission model

### 6.1 Roles

All roles are scoped to a Company. An Employee holds one or more roles.

| Role | Description | Cardinality |
| --- | --- | --- |
| **Owner** | Ultimate authority. Manages the subscription, transfers ownership, deletes the Company. | Exactly one per Company |
| **Company Admin** | Full administrative control except subscription ownership and Company deletion. | Unbounded |
| **Billing Admin** | Manages plans, payment methods, invoices, budgets. Full cost visibility. No provider or policy access. | Unbounded |
| **Team Lead** | Administrative control scoped to assigned Teams: members, budgets, usage. | Unbounded |
| **Developer** | Creates and manages own Platform API Keys. Uses Gateway, Chat, Extension. Sees own usage. | Unbounded |
| **Member** | Uses AI Chat. Sees own usage. | Unbounded |
| **Auditor** | Read-only access to audit log, usage, and analytics across the Company. No configuration or content access. | Unbounded |

### 6.2 Permission matrix

| Capability | Owner | Company Admin | Billing Admin | Team Lead | Developer | Member | Auditor |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Manage subscription & payment | ✔ | – | ✔ | – | – | – | – |
| Delete Company | ✔ | – | – | – | – | – | – |
| Transfer ownership | ✔ | – | – | – | – | – | – |
| Manage Employees & roles | ✔ | ✔ | – | Team only | – | – | – |
| Manage Teams | ✔ | ✔ | – | Own Teams | – | – | – |
| Manage Provider Connections | ✔ | ✔ | – | – | – | – | – |
| View provider credentials | – | – | – | – | – | – | – |
| Manage Routing Policies | ✔ | ✔ | – | – | – | – | – |
| Manage Governance Policies | ✔ | ✔ | – | – | – | – | – |
| Manage Budgets | ✔ | ✔ | ✔ | Own Teams | – | – | – |
| Create Platform API Keys | ✔ | ✔ | – | – | Own only | – | – |
| Revoke any Platform API Key | ✔ | ✔ | – | Team only | Own only | – | – |
| Use AI Gateway | ✔ | ✔ | – | ✔ | ✔ | – | – |
| Use AI Chat | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | – |
| View own usage & cost | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| View Team usage & cost | ✔ | ✔ | ✔ | Own Teams | – | – | ✔ |
| View Company usage & cost | ✔ | ✔ | ✔ | – | – | – | ✔ |
| View audit log | ✔ | ✔ | – | – | – | – | ✔ |
| Export data | ✔ | ✔ | ✔ | Own Teams | – | – | ✔ |
| Configure content retention | ✔ | ✔ | – | – | – | – | – |
| Read retained conversation content | – | – | – | – | – | Own only | – |

**Note on the last two rows.** No role grants the ability to read another Employee's
conversation content through the standard interface. Content access, where a Company
enables retention, requires an explicit, separately audited legal-hold process
(FR-GOV-011). This is a deliberate constraint arising from
[`mission.md`](mission.md) §5.

### 6.3 Permission requirements

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| FR-PERM-001 | The platform must enforce the permission matrix in §6.2 on every operation, at the point of execution rather than only in the interface. | M | MVP |
| FR-PERM-002 | Permission checks must be deny-by-default: an operation with no explicit grant is refused. | M | MVP |
| FR-PERM-003 | An Employee holding multiple roles must receive the union of their permissions. | M | MVP |
| FR-PERM-004 | Every permission denial must produce an audit event. | M | MVP |
| FR-PERM-005 | Role changes must take effect within one minute across all surfaces without requiring re-authentication. | M | MVP |
| FR-PERM-006 | The platform must support custom roles composed of individual permissions. | C | v2.0 |
| FR-PERM-007 | Permissions must be evaluable without reference to another Company's data, so that tenant isolation cannot be violated by a permission bug. | M | MVP |

---

## 7. Functional requirements

### 7.1 Enterprise Authentication (`FR-AUTH`)

**Intent.** Establish verified identity for every actor before any request is
processed, and ensure revocation is complete and immediate.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-AUTH-001 | Employees must be able to authenticate with an email address and password. | M | All | MVP |
| FR-AUTH-002 | Passwords must be subject to a configurable strength policy and checked against known-compromised credential lists. | M | P-06 | MVP |
| FR-AUTH-003 | Employees must be able to authenticate via OAuth2 with Google and Microsoft identity providers. | M | P-07 | MVP |
| FR-AUTH-004 | A Company Admin must be able to restrict authentication to specified methods, including disabling password authentication entirely. | M | P-07 | MVP |
| FR-AUTH-005 | The platform must support time-based one-time password multi-factor authentication. | M | P-06 | MVP |
| FR-AUTH-006 | A Company Admin must be able to require MFA for all Employees or for specified roles. | M | P-06 | MVP |
| FR-AUTH-007 | Sessions must expire after a configurable period of inactivity and after a configurable absolute lifetime. | M | P-06 | MVP |
| FR-AUTH-008 | Employees must be able to view their active sessions and terminate any of them. | S | P-06 | MVP |
| FR-AUTH-009 | A Company Admin must be able to terminate any Employee's sessions immediately. | M | P-07 | MVP |
| FR-AUTH-010 | Session termination must revoke access across every surface — console, Gateway, Chat, and Extension — within one minute. | M | P-07 | MVP |
| FR-AUTH-011 | The platform must lock an account after a configurable number of failed authentication attempts and notify the account holder. | M | P-06 | MVP |
| FR-AUTH-012 | Employees must be able to reset a forgotten password through a verified email flow with a single-use, time-limited token. | M | All | MVP |
| FR-AUTH-013 | Email addresses must be verified before an account becomes active. | M | All | MVP |
| FR-AUTH-014 | Every authentication event — success, failure, lockout, MFA challenge, password change — must produce an audit event. | M | P-06 | MVP |
| FR-AUTH-015 | The platform must support SAML 2.0 single sign-on with customer-managed identity providers. | M | P-07 | v1.2 |
| FR-AUTH-016 | The platform must support SCIM 2.0 provisioning and deprovisioning. | M | P-07 | v1.2 |
| FR-AUTH-017 | The platform must map identity-provider group membership to Teams and roles. | S | P-07 | v1.2 |
| FR-AUTH-018 | Deprovisioning an Employee must immediately revoke every credential they hold, including Platform API Keys they created. | M | P-07 | MVP |
| FR-AUTH-019 | The platform must support service identities that authenticate without a human Employee, for automated workloads. | S | P-02 | v1.1 |
| FR-AUTH-020 | The platform must support hardware security key authentication. | C | P-06 | v2.0 |

---

### 7.2 Multi-Tenancy and Organization (`FR-TEN`)

**Intent.** Provide strict isolation between Companies and a hierarchy that mirrors
how organizations actually structure themselves.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-TEN-001 | Every data record must belong to exactly one Company, and no operation may return data from more than one Company. | M | P-06 | MVP |
| FR-TEN-002 | Tenant isolation must be enforced at the data-access layer, so that omitting a filter in application logic cannot leak data. | M | P-06 | MVP |
| FR-TEN-003 | A person must be able to create a Company through self-service signup and become its Owner. | M | P-01 | MVP |
| FR-TEN-004 | A Company must have a unique identifier, display name, and configurable settings including default retention and default policies. | M | P-01 | MVP |
| FR-TEN-005 | Company Admins must be able to invite Employees by email address, with a role assigned at invitation. | M | P-07 | MVP |
| FR-TEN-006 | Company Admins must be able to restrict signup to specified verified email domains and permit automatic joining from those domains. | S | P-07 | MVP |
| FR-TEN-007 | Company Admins must be able to suspend an Employee, preserving their records while revoking all access. | M | P-07 | MVP |
| FR-TEN-008 | Company Admins must be able to remove an Employee, with their historical usage and audit records retained and attributed to the removed identity. | M | P-05 | MVP |
| FR-TEN-009 | Company Admins must be able to create, rename, and archive Teams. | M | P-01 | MVP |
| FR-TEN-010 | Teams must support nesting to a defined maximum depth, with cost and usage rolling up through the hierarchy. | S | P-05 | v1.1 |
| FR-TEN-011 | An Employee must be assignable to zero or more Teams, with one designated as their primary Team for default attribution. | M | P-05 | MVP |
| FR-TEN-012 | Ownership must be transferable to another Employee, with both parties notified and the transfer audited. | M | P-01 | MVP |
| FR-TEN-013 | A Company must not be deletable while an active subscription exists; deletion must require explicit confirmation and a defined grace period before data is destroyed. | M | P-01 | MVP |
| FR-TEN-014 | Company Admins must be able to export all Company data in a documented, machine-readable format. | M | P-06 | MVP |
| FR-TEN-015 | Team reorganization must preserve historical attribution: past usage remains attributed to the structure in effect at the time. | M | P-05 | v1.1 |
| FR-TEN-016 | The platform must support a parent-organization construct above Company for enterprises with multiple subsidiaries. | C | P-05 | Later |

---

### 7.3 AI Providers (`FR-PROV`)

**Intent.** Make provider credentials a managed, revocable, auditable asset rather
than a distributed secret.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-PROV-001 | Company Admins must be able to create a Provider Connection by supplying credentials for a supported AI provider. | M | P-01 | MVP |
| FR-PROV-002 | The platform must support OpenAI, Anthropic, and Google Gemini at MVP. | M | P-01 | MVP |
| FR-PROV-003 | The platform must support Azure OpenAI, including customer-specified endpoints and deployments. | M | P-01 | v1.1 |
| FR-PROV-004 | Provider credentials must be encrypted at rest and must never be retrievable through any interface after creation, including by the Owner. | M | P-06 | MVP |
| FR-PROV-005 | The platform must validate a Provider Connection at creation and report a clear, actionable failure when validation fails. | M | P-01 | MVP |
| FR-PROV-006 | The platform must continuously monitor Provider Connection health and surface degraded or failed connections. | M | P-02 | MVP |
| FR-PROV-007 | Company Admins must be able to rotate a Provider Connection's credentials without interrupting in-flight or subsequent requests. | M | P-01 | MVP |
| FR-PROV-008 | Company Admins must be able to disable a Provider Connection immediately, halting all traffic to it. | M | P-06 | MVP |
| FR-PROV-009 | The platform must maintain a catalog of models available through each Provider Connection, including capabilities, context limits, and pricing. | M | P-08 | MVP |
| FR-PROV-010 | The model catalog must be refreshable, and the platform must notify administrators when a model in active use is deprecated by its provider. | M | P-01 | MVP |
| FR-PROV-011 | Company Admins must be able to restrict which models are available to which Teams. | S | P-01 | MVP |
| FR-PROV-012 | Multiple Provider Connections to the same provider must be supported, for separating environments or business units. | S | P-02 | MVP |
| FR-PROV-013 | Provider Connections must be scopeable to specified Teams rather than the whole Company. | S | P-01 | v1.1 |
| FR-PROV-014 | The platform must record and display each Provider Connection's observed availability, latency, and error rate. | S | P-02 | MVP |
| FR-PROV-015 | The platform must support customer-hosted, OpenAI-compatible inference endpoints as a provider type. | S | P-02 | v1.2 |
| FR-PROV-016 | Every provider credential operation — creation, rotation, disablement, deletion — must produce an audit event. | M | P-06 | MVP |
| FR-PROV-017 | The platform must support provider-side organization or project identifiers where the provider offers them. | C | P-05 | v1.1 |

---

### 7.4 AI Gateway (`FR-GW`)

**Intent.** A single inference endpoint that is more reliable and more convenient than
calling providers directly. This is the capability on which coverage depends.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-GW-001 | The Gateway must accept inference requests authenticated by a Platform API Key and route them to a configured provider. | M | P-03 | MVP |
| FR-GW-002 | The Gateway must support chat completion requests against any model in the catalog. | M | P-03 | MVP |
| FR-GW-003 | The Gateway must support streaming responses. | M | P-03 | MVP |
| FR-GW-004 | The Gateway must expose a request interface compatible with the OpenAI chat completions API, so that existing integrations migrate by changing base URL and credential only. | M | P-03 | MVP |
| FR-GW-005 | The Gateway must expose a native interface that is provider-neutral and not modelled on any single provider. | S | P-02 | v1.1 |
| FR-GW-006 | The Gateway must normalize provider errors into a documented, stable error taxonomy while preserving the original provider error for diagnosis. | M | P-03 | MVP |
| FR-GW-007 | The Gateway must support Routing Policies that select a provider and model based on requested model, Team, and Provider Connection availability. | M | P-01 | MVP |
| FR-GW-008 | Routing Policies must support an ordered fallback chain, attempting the next target when a target fails or is unavailable. | M | P-01 | MVP |
| FR-GW-009 | The Gateway must retry transient provider failures according to a configurable policy with bounded attempts and backoff. | M | P-02 | MVP |
| FR-GW-010 | The Gateway must apply a circuit breaker per Provider Connection, removing a failing target from rotation and restoring it on recovery. | M | P-02 | MVP |
| FR-GW-011 | Every routing decision — target selected, fallbacks attempted, retries performed, latency at each step — must be recorded and retrievable for a request. | M | P-02 | MVP |
| FR-GW-012 | The Gateway must enforce per-Company, per-Team, and per-Key rate limits, returning a documented error with retry guidance when exceeded. | M | P-01 | MVP |
| FR-GW-013 | The Gateway must enforce Budgets, rejecting requests that would exceed a hard limit and permitting those against a soft limit while alerting. | M | P-05 | MVP |
| FR-GW-014 | The Gateway must evaluate applicable Governance Policies before forwarding a request. | M | P-06 | MVP |
| FR-GW-015 | The Gateway must apply a configurable request timeout and terminate requests that exceed it, releasing all resources. | M | P-02 | MVP |
| FR-GW-016 | The Gateway must record token counts for every request, using provider-reported counts where available and a documented estimate otherwise, marking which was used. | M | P-05 | MVP |
| FR-GW-017 | The Gateway must degrade gracefully when dependent subsystems are unavailable: metering or analytics failure must not fail an inference request, and such degradation must be recorded. | M | P-02 | MVP |
| FR-GW-018 | The Gateway must never fail open on authentication, authorization, budget, or governance enforcement. | M | P-06 | MVP |
| FR-GW-019 | The Gateway must support embedding generation requests. | S | P-08 | v1.1 |
| FR-GW-020 | The Gateway must support multimodal requests including image inputs, where the target model supports them. | S | P-03 | v1.1 |
| FR-GW-021 | The Gateway must support provider-native tool and function calling, passed through without loss of fidelity. | M | P-03 | MVP |
| FR-GW-022 | The Gateway must support caching of identical requests within a configurable window, disabled by default and configurable per Team. | C | P-05 | v1.2 |
| FR-GW-023 | The Gateway must support routing a configurable percentage of traffic to an alternative model for comparison. | C | P-08 | v2.0 |
| FR-GW-024 | The Gateway must expose a health endpoint reporting its own status and that of each Provider Connection. | M | P-02 | MVP |
| FR-GW-025 | The Gateway must support request cancellation, propagating cancellation to the provider where supported. | S | P-03 | v1.1 |

> **Requirement FR-GW-017 and FR-GW-018 together define the fail-open/fail-closed
> policy.** Metering and observability degrade open so that a platform problem never
> becomes a customer outage. Security and financial controls degrade closed. This
> distinction must be explicit in design and covered by tests.

---

### 7.5 AI Chat (`FR-CHAT`)

**Intent.** A sanctioned assistant good enough that employees prefer it to the
consumer product they currently use. Quality here is a governance mechanism.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-CHAT-001 | Any Employee with Chat access must be able to hold a multi-turn conversation with a permitted model. | M | P-04 | MVP |
| FR-CHAT-002 | Responses must stream as they are generated. | M | P-04 | MVP |
| FR-CHAT-003 | Conversations must persist and be retrievable by their owner across sessions and devices. | M | P-04 | MVP |
| FR-CHAT-004 | Employees must be able to search their own conversation history. | S | P-04 | MVP |
| FR-CHAT-005 | Employees must be able to rename, organize, and delete their own conversations. | M | P-04 | MVP |
| FR-CHAT-006 | Employees must be able to select from the models their Company permits them, with a sensible Company-configured default. | M | P-04 | MVP |
| FR-CHAT-007 | Chat requests must pass through the same Gateway, metering, governance, and audit path as Developer API requests. | M | P-06 | MVP |
| FR-CHAT-008 | The interface must clearly disclose what the Company can observe about Chat usage. | M | P-04 | MVP |
| FR-CHAT-009 | Employees must be able to attach documents for the model to reference, subject to Company policy on permitted types and sizes. | S | P-04 | v1.1 |
| FR-CHAT-010 | Company Admins must be able to define shared prompt templates available to specified Teams. | S | P-04 | v1.1 |
| FR-CHAT-011 | Employees must be able to share a conversation with named colleagues in the same Company. | C | P-04 | v1.1 |
| FR-CHAT-012 | The interface must render markdown, syntax-highlighted code, and support copying any response. | M | P-04 | MVP |
| FR-CHAT-013 | Employees must be able to regenerate a response and edit a previous message to branch the conversation. | S | P-04 | MVP |
| FR-CHAT-014 | Conversation content must be retained according to the Company's configured retention policy, with a documented default and per-Employee deletion available at any time. | M | P-06 | MVP |
| FR-CHAT-015 | The platform must support grounding responses in Company-provided knowledge sources. | C | P-04 | v2.0 |
| FR-CHAT-016 | The interface must be usable on mobile browsers. | S | P-04 | v1.1 |
| FR-CHAT-017 | Employees must be able to provide feedback on a response, recorded for quality analysis without exposing content to other Employees. | C | P-08 | v1.1 |

---

### 7.6 Usage Tracking (`FR-USG`)

**Intent.** A complete, attributable ledger of every request. This is the substrate
for cost, analytics, and audit; gaps here corrupt all three.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-USG-001 | Every request processed by the platform must produce exactly one Usage Record. | M | P-05 | MVP |
| FR-USG-002 | Each Usage Record must carry the full attribution chain defined in §3.4. | M | P-05 | MVP |
| FR-USG-003 | Each Usage Record must record input tokens, output tokens, latency, outcome, and — where the request failed — the failure category. | M | P-01 | MVP |
| FR-USG-004 | Usage Records must be immutable once written; corrections must be issued as compensating records rather than edits. | M | P-06 | MVP |
| FR-USG-005 | Usage Records must be queryable by Company, Team, Employee, model, provider, surface, and time range. | M | P-01 | MVP |
| FR-USG-006 | Usage data must be available for query within a stated freshness target, and the current freshness must be visible to users. | M | P-01 | MVP |
| FR-USG-007 | The platform must record and expose the proportion of Usage Records where token counts were estimated rather than provider-reported. | M | P-05 | MVP |
| FR-USG-008 | Usage Records must be exportable in a documented machine-readable format, filtered by any queryable dimension. | M | P-05 | MVP |
| FR-USG-009 | Usage Records must be retained for a configurable period with a documented default, and retention changes must be audited. | M | P-06 | MVP |
| FR-USG-010 | Employees must be able to view their own usage. | M | P-03 | MVP |
| FR-USG-011 | The platform must support a customer-supplied attribution tag on Gateway requests, enabling attribution to a product, feature, or end customer. | S | P-05 | v1.1 |
| FR-USG-012 | Usage recording must not be sampled under any load condition. | M | P-06 | MVP |
| FR-USG-013 | If a Usage Record cannot be written, the failure must be recorded, alerted on, and reconciled — never silently discarded. | M | P-06 | MVP |

---

### 7.7 Cost Tracking (`FR-COST`)

**Intent.** Translate provider metering into financial language, with stated accuracy.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-COST-001 | Every Usage Record must yield a Cost Record calculated from recorded tokens and the model's pricing at the time of the request. | M | P-05 | MVP |
| FR-COST-002 | Model pricing must be versioned with effective dates, so historical costs remain accurate after a price change. | M | P-05 | MVP |
| FR-COST-003 | Cost must be aggregatable by Company, Team, Employee, model, provider, surface, and attribution tag, over any time range. | M | P-05 | MVP |
| FR-COST-004 | The platform must publish the expected accuracy of its cost figures relative to provider invoices, and surface the causes of divergence. | M | P-05 | MVP |
| FR-COST-005 | Billing Admins must be able to define Budgets at Company, Team, or Employee scope, over a defined period. | M | P-05 | MVP |
| FR-COST-006 | Budgets must support configurable alert thresholds that notify designated recipients when crossed. | M | P-05 | MVP |
| FR-COST-007 | Budgets must support a hard limit that causes further requests in scope to be rejected with a clear, documented error. | M | P-05 | MVP |
| FR-COST-008 | Every Budget must default to alert-only, requiring an explicit action to become enforcing. | M | P-01 | MVP |
| FR-COST-009 | Cost data must be exportable in a documented format suitable for import into financial systems. | M | P-05 | MVP |
| FR-COST-010 | The platform must show cost trends over time and compare the current period against prior periods. | M | P-05 | MVP |
| FR-COST-011 | The platform must forecast period-end spend from observed usage, with the basis of the forecast stated. | S | P-05 | v1.1 |
| FR-COST-012 | The platform must support a Company-defined currency for display, with a documented conversion source and rate date. | S | P-05 | v1.2 |
| FR-COST-013 | The platform must support reconciliation against provider invoices, reporting variance and its likely causes. | S | P-05 | v1.2 |
| FR-COST-014 | Provider costs must be reported at the price the customer pays their provider, with any platform fee shown as a separate line. | M | P-05 | MVP |

---

### 7.8 Analytics (`FR-ANL`)

**Intent.** Answer the questions each persona actually asks, without requiring them to
construct queries.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-ANL-001 | The platform must present a Company overview showing spend, request volume, active users, and error rate for a selected period. | M | P-01 | MVP |
| FR-ANL-002 | The platform must present cost and usage broken down by Team, Employee, model, provider, and surface. | M | P-05 | MVP |
| FR-ANL-003 | The platform must present Gateway reliability metrics: success rate, latency distribution, retry rate, fallback rate, per Provider Connection. | M | P-02 | MVP |
| FR-ANL-004 | The platform must present model adoption over time, showing usage shift between models and providers. | S | P-08 | MVP |
| FR-ANL-005 | All analytics must be filterable by time range, Team, provider, model, and surface. | M | P-01 | MVP |
| FR-ANL-006 | Every analytics view must be exportable as structured data, not only as an image. | M | P-05 | MVP |
| FR-ANL-007 | Analytics must respect the permission model: users see only data within their scope. | M | P-06 | MVP |
| FR-ANL-008 | Analytics must state the freshness of the data being displayed. | M | P-01 | MVP |
| FR-ANL-009 | The platform must surface anomalies in usage or cost relative to established baselines. | S | P-01 | v1.1 |
| FR-ANL-010 | Users must be able to save and share filtered views within their permission scope. | C | P-01 | v1.1 |
| FR-ANL-011 | The platform must provide scheduled report delivery by email. | C | P-05 | v1.1 |
| FR-ANL-012 | The platform must expose comparative model performance — latency, error rate, cost per request — for models used on comparable traffic. | S | P-08 | v1.1 |

---

### 7.9 Billing (`FR-BILL`)

**Intent.** Manage the commercial relationship between MaintOrbit AI and the Company.
Distinct from Cost Tracking, which concerns the Company's spend with its providers.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-BILL-001 | The platform must support defined subscription plans with documented limits and included capabilities. | M | P-05 | MVP |
| FR-BILL-002 | Owners and Billing Admins must be able to view the current plan, its limits, and consumption against them. | M | P-05 | MVP |
| FR-BILL-003 | The platform must support self-service plan upgrade, with the change effective immediately. | M | P-05 | MVP |
| FR-BILL-004 | The platform must support plan downgrade and cancellation, effective at the end of the current period, with consequences stated before confirmation. | M | P-05 | MVP |
| FR-BILL-005 | The platform must meter the billable units defined by the commercial model and expose current-period consumption. | M | P-05 | MVP |
| FR-BILL-006 | The platform must generate invoices for each billing period, retrievable as documents. | M | P-05 | MVP |
| FR-BILL-007 | The platform must accept payment by card through a compliant payment processor, and must never store card data directly. | M | P-06 | MVP |
| FR-BILL-008 | The platform must notify Billing Admins in advance of renewal and on payment failure. | M | P-05 | MVP |
| FR-BILL-009 | The platform must define and enforce a documented grace period and degradation sequence on payment failure, never destroying data without notice. | M | P-05 | MVP |
| FR-BILL-010 | The platform must support a trial period with defined limits and a clear expiry path. | M | P-01 | MVP |
| FR-BILL-011 | The platform must support invoice-based billing with purchase orders for annual contracts. | S | P-05 | v1.1 |
| FR-BILL-012 | The platform must support tax calculation and display appropriate to the Company's jurisdiction. | M | P-05 | MVP |
| FR-BILL-013 | The platform must never bill for audit record retention. | M | P-06 | MVP |
| FR-BILL-014 | Every billing event — plan change, payment, failure, refund — must produce an audit event. | M | P-06 | MVP |

> **FR-BILL-005 is blocked** until decision D-1 in [`business-goals.md`](business-goals.md)
> §11 is resolved. The billable unit determines what must be metered.

---

### 7.10 Developer APIs (`FR-API`)

**Intent.** Make the platform programmable, and make credential management something
that survives employee turnover.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-API-001 | Developers must be able to create Platform API Keys scoped to themselves and a Team. | M | P-03 | MVP |
| FR-API-002 | A Platform API Key's secret must be displayed exactly once at creation and never be retrievable afterwards. | M | P-06 | MVP |
| FR-API-003 | Platform API Keys must support an optional expiry date, with notification before expiry. | M | P-06 | MVP |
| FR-API-004 | Platform API Keys must be revocable immediately by their creator, a Team Lead of their scope, or a Company Admin. | M | P-07 | MVP |
| FR-API-005 | Platform API Keys must carry scopes restricting which capabilities they may exercise. | M | P-06 | MVP |
| FR-API-006 | The platform must record and display last-used time and usage volume for each Platform API Key. | M | P-06 | MVP |
| FR-API-007 | A public API must expose usage and cost data, filterable by the dimensions in FR-USG-005. | M | P-01 | MVP |
| FR-API-008 | A public API must expose organizational management — Employees, Teams, roles — for automation. | S | P-07 | v1.1 |
| FR-API-009 | A public API must expose the audit log for export to external security systems. | M | P-06 | v1.1 |
| FR-API-010 | All public APIs must be versioned, with a documented deprecation policy and minimum notice period. | M | P-02 | MVP |
| FR-API-011 | All public APIs must return errors in a consistent, documented structure. | M | P-03 | MVP |
| FR-API-012 | The platform must publish a machine-readable API specification kept in sync with the implementation. | M | P-03 | MVP |
| FR-API-013 | The platform must support webhooks for defined events, with signed payloads and delivery retry. | S | P-02 | v1.1 |
| FR-API-014 | The platform must provide real-time updates for usage, cost, and Provider Connection health in the web console without polling. | S | P-01 | MVP |
| FR-API-015 | The platform must publish client libraries for at least TypeScript and Python. | S | P-03 | v1.1 |
| FR-API-016 | Platform API Keys must be automatically revoked when their creating Employee is deprovisioned. | M | P-07 | MVP |

---

### 7.11 VS Code Extension (`FR-EXT`)

**Intent.** Put governed AI where developers already work, and make the governed path
the convenient one at the moment of use.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-EXT-001 | Developers must be able to authenticate the extension against their Company account without manually handling a long-lived credential. | M | P-03 | MVP |
| FR-EXT-002 | The extension must provide a chat interface within the editor, using the same Gateway and governance path as all other surfaces. | M | P-03 | MVP |
| FR-EXT-003 | Developers must be able to include selected code as context in a request. | M | P-03 | MVP |
| FR-EXT-004 | The extension must provide commands for common tasks — explain, refactor, generate tests, document — operating on the current selection. | M | P-03 | MVP |
| FR-EXT-005 | The extension must allow model selection from the Company's permitted set. | M | P-03 | MVP |
| FR-EXT-006 | The extension must display the developer's current usage and remaining budget. | S | P-03 | MVP |
| FR-EXT-007 | Extension usage must be attributed to the Employee and Team and appear in all analytics, distinguishable by surface. | M | P-01 | MVP |
| FR-EXT-008 | The extension must respect Governance Policies, including any restriction on transmitting source code. | M | P-06 | MVP |
| FR-EXT-009 | The extension must stream responses and allow cancellation of an in-flight request. | M | P-03 | MVP |
| FR-EXT-010 | The extension must fail gracefully and informatively when the platform is unreachable. | M | P-03 | MVP |
| FR-EXT-011 | Company Admins must be able to disable the extension for the Company or specified Teams. | S | P-06 | v1.1 |
| FR-EXT-012 | The extension must support applying a suggested change directly to the open file, with a reviewable diff. | S | P-03 | v1.1 |
| FR-EXT-013 | The extension must support workspace-level configuration committed to a repository. | C | P-03 | v1.1 |
| FR-EXT-014 | The extension must never transmit file content that has not been explicitly included by the developer or by configured context rules. | M | P-06 | MVP |
| FR-EXT-015 | Equivalent extensions must be provided for JetBrains IDEs. | C | P-03 | v2.0 |

---

### 7.12 Governance (`FR-GOV`)

**Intent.** Provide an enforcement point at the moment of egress, introduced in a way
that does not break customer traffic.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-GOV-001 | Company Admins must be able to define Governance Policies scoped to the Company or specified Teams. | M | P-06 | MVP |
| FR-GOV-002 | Every Governance Policy must support monitor mode and enforce mode, and must default to monitor on creation. | M | P-02 | MVP |
| FR-GOV-003 | In monitor mode, a policy must record what action it would have taken without affecting the request. | M | P-02 | MVP |
| FR-GOV-004 | Policies must support restricting which models and providers may be used, by Team. | M | P-06 | MVP |
| FR-GOV-005 | Policies must support blocking requests containing configured patterns. | M | P-06 | MVP |
| FR-GOV-006 | Policies must support detection of common categories of personal data, with configurable actions of allow, redact, or block. | S | P-06 | v1.1 |
| FR-GOV-007 | The platform must publish the expected detection and false-positive characteristics of any automated content detection it provides. | M | P-06 | v1.1 |
| FR-GOV-008 | Every policy evaluation resulting in a block or redaction must produce an audit event recording the policy, the action, and the reason. | M | P-06 | MVP |
| FR-GOV-009 | Company Admins must be able to configure whether prompt and completion content is retained, per Team, defaulting to not retained. | M | P-06 | MVP |
| FR-GOV-010 | Enabling content retention must require explicit confirmation and must itself produce an audit event. | M | P-06 | MVP |
| FR-GOV-011 | Access to retained content must require a separate, explicitly authorized legal-hold process that is itself audited and notifies designated parties. | M | P-06 | v1.1 |
| FR-GOV-012 | Policies must support restricting usage to defined time windows or network origins. | C | P-06 | v1.2 |
| FR-GOV-013 | Policies must support an approval workflow for requests matching defined criteria. | C | P-06 | v2.0 |
| FR-GOV-014 | The platform must provide a report of policy activity: evaluations, blocks, redactions, and monitored-only matches, by policy and Team. | M | P-06 | MVP |
| FR-GOV-015 | Policy evaluation must not add latency beyond the budget defined in [`non-functional-requirements.md`](non-functional-requirements.md). | M | P-03 | MVP |

---

### 7.13 Auditing (`FR-AUD`)

**Intent.** Produce a record complete enough to satisfy an external auditor and an
incident investigation.

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-AUD-001 | The platform must record an audit event for every authentication, authorization decision, configuration change, credential operation, data export, and administrative action. | M | P-06 | MVP |
| FR-AUD-002 | Each audit event must record actor, action, target, outcome, timestamp, and originating context. | M | P-06 | MVP |
| FR-AUD-003 | Audit events must be append-only and must not be modifiable or deletable through any interface, by any role. | M | P-06 | MVP |
| FR-AUD-004 | Audit events must not be sampled under any load condition. | M | P-06 | MVP |
| FR-AUD-005 | Audit events must be searchable by actor, action, target, outcome, and time range. | M | P-06 | MVP |
| FR-AUD-006 | Audit events must be exportable in a documented machine-readable format suitable for ingestion by security tooling. | M | P-06 | MVP |
| FR-AUD-007 | Audit retention must be configurable with a documented default, and retention changes must themselves be audited. | M | P-06 | MVP |
| FR-AUD-008 | Audit records must be tamper-evident, such that modification is detectable. | M | P-06 | v1.1 |
| FR-AUD-009 | The platform must support continuous streaming of audit events to a customer-specified destination. | S | P-06 | v1.1 |
| FR-AUD-010 | Audit events must never contain prompt or completion content, referencing it only where retention is enabled. | M | P-06 | MVP |
| FR-AUD-011 | A failure to write an audit event must be treated as an incident: recorded, alerted, and reconciled. | M | P-06 | MVP |
| FR-AUD-012 | The platform must generate a data-flow report showing which Teams sent data to which providers over a period. | S | P-06 | v1.1 |

---

### 7.14 Notifications (`FR-NOT`)

| ID | Requirement | Pri | Persona | Release |
| --- | --- | --- | --- | --- |
| FR-NOT-001 | The platform must notify designated recipients when a Budget threshold is crossed. | M | P-05 | MVP |
| FR-NOT-002 | The platform must notify administrators when a Provider Connection becomes unhealthy or recovers. | M | P-01 | MVP |
| FR-NOT-003 | The platform must notify administrators when a model in active use is deprecated. | M | P-01 | MVP |
| FR-NOT-004 | The platform must notify administrators of security-relevant events: new Provider Connection, role elevation, failed authentication bursts. | M | P-06 | MVP |
| FR-NOT-005 | Users must be able to configure which notifications they receive and by which channel. | M | All | MVP |
| FR-NOT-006 | Notifications must be delivered by email at MVP. | M | All | MVP |
| FR-NOT-007 | The platform must support delivery to Slack and Microsoft Teams. | S | P-01 | v1.1 |
| FR-NOT-008 | The platform must present in-application notifications in real time without requiring a page refresh. | S | All | MVP |
| FR-NOT-009 | The platform must rate-limit notifications to prevent alert flooding from a single recurring condition. | M | P-01 | MVP |

---

## 8. Cross-cutting requirements

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| FR-X-001 | Every user-facing error must state what happened, why, and what the user can do next. Provider errors must be translated, not merely relayed. | M | MVP |
| FR-X-002 | Every list interface must support pagination, filtering, and sorting consistently. | M | MVP |
| FR-X-003 | All timestamps must be stored in UTC and displayed in the user's configured time zone. | M | MVP |
| FR-X-004 | Every destructive action must require explicit confirmation stating what will be lost. | M | MVP |
| FR-X-005 | The web console must meet WCAG 2.1 Level AA. | M | MVP |
| FR-X-006 | The platform must present a consistent status indication when any subsystem is degraded. | S | MVP |
| FR-X-007 | All product surfaces must use the terminology defined in [`glossary.md`](glossary.md), without synonyms. | M | MVP |
| FR-X-008 | The platform must support interface localization. | C | v2.0 |

---

## 9. Constraints

| # | Constraint | Source |
| --- | --- | --- |
| C-1 | Modules must communicate only through published contracts and events, never by direct internal access. | Phase 0 architecture decision |
| C-2 | No dependency may be introduced that cannot run in a customer-controlled environment. | [`vision.md`](vision.md) Pillar 5 |
| C-3 | Provider credentials must never be retrievable after creation, by any role. | FR-PROV-004 |
| C-4 | Content retention is opt-in and off by default. | [`mission.md`](mission.md) §4.7 |
| C-5 | Audit and usage records must never be sampled. | [`mission.md`](mission.md) §4.5 |
| C-6 | Gateway latency overhead must remain within the published budget. | [`non-functional-requirements.md`](non-functional-requirements.md) |
| C-7 | Every enforcing control must offer a monitor mode first. | [`mission.md`](mission.md) §4.3 |
| C-8 | The platform must not mark up provider inference costs. | [`business-goals.md`](business-goals.md) §9 |

---

## 10. External dependencies

| Dependency | Purpose | Risk |
| --- | --- | --- |
| AI provider APIs | Core function | Breaking changes, deprecation, outages, pricing changes — mitigated by the abstraction and multi-provider routing |
| Provider pricing data | Cost calculation | Published pricing may change without notice; requires monitoring and versioning per FR-COST-002 |
| Payment processor | Billing | Availability affects signup and renewal, not the data path |
| Email delivery | Notifications, verification | Deliverability affects onboarding |
| OAuth2 identity providers | Authentication | Availability affects login for affected Companies |
| Container and cloud infrastructure | Hosting | Standard operational risk |

---

## 11. Assumptions

| # | Assumption | Impact if wrong |
| --- | --- | --- |
| A-1 | Provider APIs are similar enough to abstract behind FR-GW-004 and FR-GW-005 | Per-provider work grows; abstraction leaks; native interface deferred |
| A-2 | Provider-reported token counts are available on most requests | FR-COST-004 accuracy degrades; estimation becomes the norm |
| A-3 | OpenAI-compatible interface remains a de facto migration standard | FR-GW-004's migration value diminishes |
| A-4 | Governance evaluation fits within the latency budget | FR-GOV-015 forces asynchronous evaluation, weakening enforcement |
| A-5 | Customers accept metadata-only default retention | Content retention becomes standard, raising privacy and cost profile |
| A-6 | Team hierarchy adequately models customer organizational structure | FR-TEN-016 moves forward |
| A-7 | Chat and Gateway can share one governance and metering path without compromising either | Divergent implementations; duplicated logic |

---

## 12. Open questions

Must be resolved before or during Phase 2. Each blocks specific requirements.

| # | Question | Blocks | Owner |
| --- | --- | --- | --- |
| Q-1 | What is the billable unit of the commercial model? | FR-BILL-005, FR-BILL-001 | Leadership |
| Q-2 | What tenant isolation strategy is used at the data layer? | FR-TEN-001, FR-TEN-002 | Engineering |
| Q-3 | What is the default retention period for Usage Records, audit events, and conversations? | FR-USG-009, FR-AUD-007, FR-CHAT-014 | Product & Legal |
| Q-4 | What is the published cost accuracy tolerance? | FR-COST-004 | Product & Finance |
| Q-5 | Is the native Gateway interface v1.1 or MVP? | FR-GW-005 | Product & Engineering |
| Q-6 | How are provider credentials encrypted, and who controls the keys? | FR-PROV-004, C-2 | Engineering & Security |
| Q-7 | What is the Gateway's behaviour when the metering subsystem is unavailable for an extended period? | FR-GW-017, FR-USG-013 | Engineering |
| Q-8 | Do Members and Developers consume the same billable seat? | FR-BILL-005 | Leadership |
| Q-9 | What is the legal-hold process for retained content, and who may authorize it? | FR-GOV-011 | Legal & Product |
| Q-10 | Is self-hosted deployment committed within 12 months? | C-2, FR-PROV-015 | Leadership |

---

## 13. Future considerations

- **Agentic workloads change the unit of metering.** A single user action producing
  dozens of chained calls breaks the assumption in FR-USG-001 that a request is a
  meaningful unit. The Usage model should be designed so that a parent trace
  identifier can be added without restructuring.
- **"Provider" will generalize beyond commercial APIs.** FR-PROV-015 is the first step;
  the abstraction should not assume an external, public, paid endpoint.
- **Governance will need to evaluate completions, not only prompts.** Current
  requirements focus on egress. Response-side evaluation has different latency
  characteristics with streaming and needs separate design.
- **The permission model will need customization.** FR-PERM-006 is deferred, but
  segment 3.2 customers will require it. The model should be built as composable
  permissions with fixed roles as presets, not as hard-coded roles.
- **Cost attribution will need to handle reorganizations.** FR-TEN-015 covers the
  basic case; matrix organizations and shared cost centres will require more.
- **Model quality is unmeasured.** No requirement addresses whether responses are
  good. This is deliberate for MVP but is the largest gap in the long-term value
  proposition, and P-08's requirements depend on closing it.

---

## 14. Cross references

| Document | Relationship |
| --- | --- |
| [`problem-statement.md`](problem-statement.md) | Problems these requirements address |
| [`user-personas.md`](user-personas.md) | Personas referenced in requirement tables |
| [`target-users.md`](target-users.md) | Segments and role definitions |
| [`mvp-features.md`](mvp-features.md) | Which requirements are in the first release |
| [`future-roadmap.md`](future-roadmap.md) | Sequencing of deferred requirements |
| [`non-functional-requirements.md`](non-functional-requirements.md) | Quality attributes constraining these requirements |
| [`business-goals.md`](business-goals.md) | Commercial decisions blocking §12 questions |
| [`mission.md`](mission.md) | Principles behind §9 constraints |
| [`glossary.md`](glossary.md) | Normative definitions of §3 terms |
| `docs/02-architecture/` | Phase 2 — how these requirements are satisfied |
| `docs/03-database/` | Phase 3 — data model |
| `docs/04-api/` | Phase 3 — interface specification |
| `docs/07-adr/` | Decisions resolving §12 open questions |
