# Glossary

| Field | Value |
| --- | --- |
| Document | Glossary |
| Version | 1.0 |
| Status | **Normative** — these definitions bind all documentation, design, and implementation |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Everyone |

---

## 1. Purpose

This glossary is the single source of truth for MaintOrbit AI's vocabulary. It is
normative, not descriptive: where a term is defined here, that definition is the only
correct one, and no synonym may be used in its place.

**Why this matters more than it appears to.** Ambiguous vocabulary in a multi-tenant
governance product produces defects that are expensive and hard to detect. If one
engineer understands "user" to mean an Employee and another understands it to include
an API key acting on an Employee's behalf, the permission model that results will have
a gap nobody notices until an audit. The cost of vocabulary discipline is small; the
cost of its absence compounds.

**Scope of application.** These terms are used consistently in: product
documentation, architecture and design documents, database and API naming, code
identifiers, user interface labels, error messages, and customer-facing documentation.
Requirement FR-X-007 and NFR-USE-008 make this binding on the product surface.

---

## 2. Organizational terms

### Company
The tenant. The top-level unit of isolation, contracting, billing, and administration.
Every record in the platform belongs to exactly one Company, and no data is ever
visible across Companies. A Company is created through self-service signup, and the
person who creates it becomes its Owner.

*Do not use:* organization, org, tenant, account, workspace, customer.
*Note:* "tenant" is acceptable in architecture documentation when discussing isolation
mechanics, but never in product or user-facing contexts.

### Employee
A user account belonging to exactly one Company. An Employee authenticates, holds one
or more Roles, may belong to Teams, and is the identity to which usage, cost, and
audit records are attributed. A person working with two Companies holds two separate
Employee accounts; there is no cross-Company identity.

*Do not use:* user, member (as a general term — Member is a specific Role), seat,
person, account.

### Team
A grouping of Employees within a Company, used for cost attribution, budget scoping,
policy scoping, and access control. An Employee may belong to zero or more Teams and
designates one as their primary Team for default attribution. Teams may nest to a
bounded depth (v1.1).

*Do not use:* group, department, project, workspace, unit.

### Team Membership
The association between an Employee and a Team.

### Primary Team
The single Team designated for an Employee's default cost and usage attribution when a
request does not specify one.

### Role
A named set of permissions scoped to a Company. The seven Roles are Owner, Company
Admin, Billing Admin, Team Lead, Developer, Member, and Auditor. An Employee holding
multiple Roles receives the union of their permissions. Defined in
[`product-requirements.md`](product-requirements.md) §6.

*Do not use:* permission level, access level, group, persona.
*Note:* **Role** is a platform concept. **Persona** is a product-research concept
describing a type of user (P-01 Priya, etc.). They are not interchangeable.

### Owner
The Role holding ultimate authority over a Company: manages the subscription,
transfers ownership, deletes the Company. Exactly one per Company.

### Company Admin
The Role holding full administrative control except subscription ownership and Company
deletion.

### Billing Admin
The Role managing plans, payment methods, invoices, and Budgets, with full cost
visibility but no access to Provider Connections or Governance Policies.

### Team Lead
The Role holding administrative control scoped to specified Teams.

### Developer
The Role permitted to create and manage their own Platform API Keys and to use the AI
Gateway, AI Chat, and the VS Code Extension.

### Member
The Role permitted to use AI Chat and view their own usage. The largest population in
a typical Company.

*Note:* always capitalized when referring to the Role. Never use lowercase "member"
to mean a generic Employee — use Employee.

### Auditor
The Role holding read-only access to the Audit Log, usage, and analytics across the
Company, with no configuration access and no access to conversation content.

---

## 3. AI provider terms

### AI Provider
An organization offering AI model inference through an API — for example OpenAI,
Anthropic, or Google. Refers to the vendor, not to the customer's configuration of it.

*Do not use:* vendor, model provider, LLM provider, AI vendor.

### Provider Connection
A Company's configured, credentialed connection to one AI Provider. The unit that is
created, validated, health-monitored, rotated, disabled, and audited. A Company may
hold multiple Provider Connections to the same AI Provider, for example to separate
environments or business units.

*Do not use:* integration, provider config, credential, connection (unqualified),
provider account.
*Note:* the distinction from **AI Provider** is important. OpenAI is an AI Provider;
"our production OpenAI connection" is a Provider Connection.

### Provider Credential
The secret supplied when creating a Provider Connection. Encrypted at rest and never
retrievable through any interface after creation, by any Role.

*Do not use:* API key (which in this platform means Platform API Key), token, secret.

### Model
A specific inference model made available through a Provider Connection, with defined
capabilities, a context limit, and pricing.

### Model Catalog
The set of Models available to a Company through its Provider Connections, including
capabilities, context limits, and pricing. Refreshable, and the source of deprecation
notification.

### Provider Endpoint
The network address a Provider Connection targets. Relevant where a customer specifies
their own — for example Azure OpenAI deployments (v1.1) or customer-hosted
OpenAI-compatible endpoints (v1.2).

---

## 4. Gateway terms

### AI Gateway
The platform component that receives inference requests, authenticates and authorizes
them, applies Governance Policies and Budgets, routes them to a provider according to
a Routing Policy, and records the result. Referred to as "the Gateway" after first
use within a document.

*Do not use:* proxy, router, API gateway, LLM gateway.
*Note:* "API gateway" specifically denotes a different product category and must not
be used — see [`competitor-analysis.md`](competitor-analysis.md).

### Gateway Request
A single inference request processed by the Gateway. Produces exactly one Usage Record
and at least one Audit Event.

*Do not use:* call, invocation, completion, query, prompt.
*Note:* **prompt** means the content sent to a model, not the request carrying it.

### Routing Policy
Configuration determining which Provider Connection and Model serve a Gateway Request,
including an ordered fallback chain.

*Do not use:* route, routing rule, load balancing config.

### Fallback
The attempt of the next target in a Routing Policy's ordered chain after the preceding
target fails or is unavailable. Distinct from **Retry**.

### Retry
A repeated attempt against the *same* target following a transient failure, subject to
a bounded attempt count and backoff. Distinct from **Fallback**.

*Note:* conflating Retry and Fallback in metrics or logs makes routing behaviour
uninterpretable. They are separate concepts with separate counters.

### Circuit Breaker
The mechanism removing a failing Provider Connection from routing rotation and
restoring it upon recovery.

### Gateway Overhead
The latency attributable to the platform: end-to-end request duration minus time
awaiting the provider. The measure governed by NFR-PERF-001 through -003.

*Do not use:* latency (unqualified), overhead, platform latency.

### Compatibility Interface
The Gateway request interface modelled on the OpenAI chat completions API, enabling
migration of an existing integration by changing only the base URL and credential.

### Native Interface
The provider-neutral Gateway request interface, not modelled on any single provider
(v1.1).

### Fail Open / Fail Closed
The policy governing behaviour when a subsystem is unavailable. **Fail open**:
the request proceeds — applied to metering, analytics, and notification, so a platform
problem never becomes a customer outage. **Fail closed**: the request is rejected —
applied to authentication, authorization, Budget, and Governance enforcement. Every
subsystem is classified into exactly one category. See FR-GW-017 and FR-GW-018.

---

## 5. Chat and extension terms

### AI Chat
The platform's first-party conversational interface, available to any Employee with
Chat access. Routes through the same Gateway, metering, governance, and audit path as
Developer API traffic.

*Do not use:* chatbot, assistant, copilot, chat app.

### Conversation
A persistent, ordered sequence of Messages between an Employee and a Model, owned by
that Employee and retrievable across sessions and devices.

*Do not use:* thread, session, chat, dialogue.
*Note:* **Session** means an authenticated session. The terms are unrelated.

### Message
A single turn within a Conversation, from either the Employee or the Model.

### Prompt
The content sent to a Model. Distinct from the Gateway Request that carries it.

### Completion
The content returned by a Model.

*Do not use:* response, answer, output.
*Note:* "response" is acceptable when referring to the HTTP response; "Completion"
refers to the generated content.

### VS Code Extension
The platform's Visual Studio Code client, providing in-editor chat and code-assistance
commands through the same governed path as all other surfaces. Referred to as "the
Extension" after first use.

### Surface
The product entry point through which a request originated: Gateway, Chat, or
Extension. Recorded on every Usage Record and available as an analytics dimension.

*Do not use:* channel, client, source, origin.

---

## 6. Usage, cost, and billing terms

### Usage Record
The immutable record of one processed request, carrying the full attribution chain,
token counts, latency, and outcome. Exactly one per Gateway Request. Never sampled.

*Do not use:* log entry, event, usage log, transaction.

### Attribution Chain
The complete set of dimensions to which a Usage Record is attributed: Company → Team →
Employee → Surface → Provider Connection → Model. A request that cannot be fully
attributed is not accepted.

### Attribution Tag
An optional customer-supplied label on a Gateway Request enabling attribution to a
product, feature, or end customer beyond the standard chain (v1.1).

### Token
The provider's unit of metering for input and output content. Recorded as
provider-reported where available, and as a documented estimate otherwise, with the
distinction always marked.

### Estimated Token Count
A token count derived by the platform because the provider did not report one. Always
flagged, and the proportion of estimated records is exposed (FR-USG-007).

### Cost Record
The monetary cost derived from a Usage Record and the Model's pricing at the time of
the request. Reported at the price the customer pays their AI Provider, with any
MaintOrbit AI platform fee shown separately.

*Do not use:* charge, spend, price.

### Budget
A configurable spending limit at Company, Team, or Employee scope over a defined
period, with alert thresholds and an optional hard limit. Defaults to alert-only.

*Do not use:* limit, cap, quota.
*Note:* **Quota** is a distinct concept — see below.

### Quota
A limit on request rate or volume, distinct from a Budget, which limits monetary
spend.

### Hard Limit
A Budget configured to reject requests that would exceed it. Requires explicit
activation; never the default.

### Soft Limit
A Budget threshold that triggers notification without rejecting requests.

### Plan
A MaintOrbit AI subscription tier with defined limits and included capabilities.

### Subscription
A Company's active commercial relationship with MaintOrbit AI.

### Billing
MaintOrbit AI's charges to the Company. **Distinct from Cost Tracking**, which
concerns the Company's spend with its AI Providers.

*Note:* this distinction is a frequent source of confusion and must be preserved
rigorously. "Cost" always means AI Provider spend. "Billing" always means what
MaintOrbit AI charges.

### Freshness
The lag between an event occurring and its appearance in query results. Always
displayed alongside the data it describes (FR-ANL-008).

---

## 7. Governance and audit terms

### Governance Policy
A rule evaluated against a request's content and metadata before it is forwarded,
scoped to a Company or specified Teams. Every Governance Policy supports Monitor Mode
and Enforce Mode and defaults to Monitor on creation.

*Do not use:* rule, guardrail, filter, control.

### Monitor Mode
A Governance Policy state in which the policy records what action it *would* have
taken without affecting the request. The default state for every new policy.

*Do not use:* dry run, test mode, audit mode, shadow mode.

### Enforce Mode
A Governance Policy state in which the policy's action is applied to the request.

### Redaction
Removal or masking of matched content from a request before it is forwarded, as an
alternative to blocking (v1.1).

### Content Retention
The Company-configured setting determining whether Prompt and Completion content is
stored. Configurable per Team, disabled by default, and enabling it is itself audited.

*Do not use:* logging, prompt logging, data retention.
*Note:* **Retention Period** governs how long records of any kind are kept. **Content
Retention** governs specifically whether Prompt and Completion content is stored at all.

### Retention Period
The configured duration for which records of a given type are kept before automated
deletion.

### Legal Hold
The separately authorized, separately audited process by which retained content may be
accessed (v1.1). No Role grants content access through the standard interface.

### Audit Event
An immutable, append-only record of an action taken in the platform, capturing actor,
action, target, outcome, timestamp, and originating context. Never sampled. Never
contains Prompt or Completion content.

*Do not use:* log, log entry, event (unqualified), activity record.

### Audit Log
The searchable, exportable collection of Audit Events for a Company.

### Tamper-Evident
The property whereby modification of an Audit Record is detectable (v1.1). Distinct
from tamper-proof, which is not claimed.

---

## 8. Platform and API terms

### Platform API Key
A MaintOrbit AI-issued credential authenticating requests to the Gateway and public
APIs. Scoped to an Employee and a Team, carries permission scopes, supports optional
expiry, is revocable immediately, and is automatically revoked when its creating
Employee is deprovisioned. Its secret is displayed once at creation and never again.

*Do not use:* API key (unqualified), token, credential, key.
*Note:* the distinction from **Provider Credential** is critical. A Platform API Key
authenticates *to* MaintOrbit AI. A Provider Credential authenticates *from*
MaintOrbit AI to an AI Provider. Confusing them in design or documentation produces
security defects.

### Developer API
The public programmatic interface for usage, cost, organizational management, and
audit export. Distinct from the Gateway, which serves inference traffic.

### Scope
A permission attached to a Platform API Key restricting which capabilities it may
exercise.

### Session
An authenticated period of interaction between an Employee and the platform, subject
to inactivity and absolute lifetime expiry.

### Deprovisioning
The complete revocation of an Employee's access, including every Platform API Key they
created, effective across all Surfaces within one minute.

*Do not use:* offboarding, removal, deactivation.

### Correlation Identifier
The identifier carried by a request across every subsystem and returned to the caller,
enabling complete reconstruction of a request's handling.

*Do not use:* request ID, trace ID, transaction ID.

---

## 9. Documentation and process terms

### Persona
A research construct describing a type of user — P-01 through P-08 in
[`user-personas.md`](user-personas.md). Not a platform concept. Not a Role.

### Requirement Identifier
The permanent label of a requirement: `FR-<AREA>-<NNN>` or `NFR-<CATEGORY>-<NNN>`.
Never reused, even after withdrawal.

### ADR
Architecture Decision Record. A dated record of an architecturally significant
decision, its context, the options considered, and its consequences. Stored in
`docs/07-adr/`.

### Horizon
A strategic time band defined in [`vision.md`](vision.md) §8: H1 Foundation (year 1),
H2 Consolidation (years 2–3), H3 Expansion (year 4+).

### Segment
A category of target customer organization defined in
[`target-users.md`](target-users.md) §3.

### Coverage
The proportion of a Company's total AI traffic passing through the platform. The
central measure of delivered value — see [`business-goals.md`](business-goals.md) §5.

---

## 10. Prohibited and ambiguous terms

Terms that must not be used, with their correct replacements. This table exists
because each of these has caused ambiguity in comparable systems.

| Do not use | Use instead | Why |
| --- | --- | --- |
| User | Employee | "User" is ambiguous between a human, a Platform API Key, and a service identity |
| Organization / Org | Company | Reserved for the future parent-organization construct (FR-TEN-016) |
| Tenant | Company | Acceptable in architecture documentation about isolation; never product-facing |
| Account | Company or Employee | Ambiguous between the two |
| Workspace | Company or Team | Ambiguous, and carries competitor connotations |
| Group | Team | Reserved for identity-provider groups in SSO/SCIM contexts |
| API key | Platform API Key or Provider Credential | The single most dangerous ambiguity in the vocabulary |
| Token | Token (metering) or session token | Always qualify — the metering and authentication meanings are unrelated |
| Log | Audit Event, Usage Record, or application log | Three distinct concepts with different guarantees |
| Event | Audit Event or integration event | Always qualify |
| Limit | Budget, Quota, or context limit | Three distinct concepts |
| Proxy / router | AI Gateway | Understates the capability and invites competitor comparison |
| Chatbot / assistant / copilot | AI Chat | Product name, not a category description |
| Spend | Cost (provider) or Billing (ours) | The distinction is load-bearing for finance users |
| Session (for chat) | Conversation | "Session" means an authenticated session |
| Dry run / shadow mode | Monitor Mode | One name for one concept |
| Guardrail | Governance Policy | Vague industry term with no consistent meaning |
| Real-time | State the freshness target | Meaningless without a number |
| Enterprise-grade | State the specific property | Meaningless as a claim |

---

## 11. Naming conventions

Implementation naming conventions derived from this vocabulary are specified in
`README.md` §Naming conventions and will be expanded in
`docs/05-development/coding-standards/`. The binding rule is that platform terms keep
their glossary spelling in every context, adjusted only for the casing convention of
the language or system:

| Context | Form |
| --- | --- |
| C# class, property | `ProviderConnection`, `UsageRecord` |
| TypeScript type, component | `ProviderConnection`, `UsageRecord` |
| Database table, column | `provider_connections`, `usage_records` |
| API path segment | `/provider-connections`, `/usage-records` |
| JSON field | `providerConnectionId`, `usageRecordId` |
| UI label | "Provider connection", "Usage record" |

No abbreviations of platform terms are permitted in any of these contexts. `ProvConn`,
`usg_rec`, and similar are prohibited.

---

## 12. Assumptions

| # | Assumption | Impact if wrong |
| --- | --- | --- |
| A-1 | Company is a durable top-level construct | FR-TEN-016 introduces a parent construct, requiring a vocabulary revision |
| A-2 | "Provider" continues to mean an external commercial API | Customer-hosted endpoints (FR-PROV-015) already strain this; the definition may need broadening |
| A-3 | A request is a meaningful unit of metering | Agentic workloads introduce traces as the meaningful unit, requiring new terms |
| A-4 | Seven fixed Roles are sufficient | Custom roles (FR-PERM-006) make Role a composed rather than named concept |
| A-5 | The Cost / Billing distinction is understood by users | User research may show the terms are confusing regardless of internal discipline |

---

## 13. Future considerations

- **Agentic workloads will require new vocabulary.** Trace, task, step, and tool call
  have no definitions here because they have no product meaning yet. When they acquire
  one, they must be defined before they are implemented, not after — the terms will
  otherwise be fixed by whoever writes the first design document.
- **"Provider" will need broadening.** FR-PROV-015 introduces customer-hosted
  endpoints, which are provider-shaped but neither external nor commercial. The
  definition in §3 should be revisited at that point rather than stretched silently.
- **Role will change shape with custom roles.** FR-PERM-006 converts Role from a fixed
  named set into a composition of permissions. The glossary entry, and probably the
  permission vocabulary generally, will require revision.
- **This document should be enforced automatically.** A linting rule checking
  prohibited terms in documentation and code identifiers would make §10 self-enforcing
  rather than dependent on review attention. This is worth implementing during Phase 2.
- **Customer-facing vocabulary may need to diverge.** Internal precision and customer
  comprehensibility occasionally conflict. Where they do, the customer-facing term is
  chosen deliberately and recorded here as an approved alias — never invented ad hoc in
  the interface.

---

## 14. Cross references

| Document | Relationship |
| --- | --- |
| [`product-requirements.md`](product-requirements.md) | §3 conceptual model and §6 Role definitions |
| [`target-users.md`](target-users.md) | Segment and Role context |
| [`user-personas.md`](user-personas.md) | Persona identifiers referenced throughout |
| [`mission.md`](mission.md) | Principles behind Monitor Mode, Content Retention, no-sampling |
| [`non-functional-requirements.md`](non-functional-requirements.md) | NFR-USE-008 making this glossary binding |
| [`mvp-features.md`](mvp-features.md) | Which defined concepts exist at v1.0 |
| [`future-roadmap.md`](future-roadmap.md) | Concepts arriving in later releases |
| `README.md` | Implementation naming conventions |
| `docs/02-architecture/` | Phase 2 — architectural use of this vocabulary |
