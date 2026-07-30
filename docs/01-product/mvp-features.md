# MVP Feature Scope

| Field | Value |
| --- | --- |
| Document | MVP Feature Scope |
| Version | 1.0 |
| Status | Draft — pending engineering estimation |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, QA |

---

## 1. Purpose

This document defines exactly what is in the first release of MaintOrbit AI, what is
deliberately excluded, and why the line falls where it does.

Its practical function is to make scope changes visible. Adding to this document is a
decision with a schedule consequence; adding to a sprint quietly is not a decision at
all.

---

## 2. Overview

### 2.1 What the MVP must prove

The MVP is not "the smallest thing we can ship." It is the smallest thing that tests
the product's central hypotheses. Three questions must be answerable at the end of it:

1. **Will developers route production traffic through a third-party gateway?**
   Tested by migration friction and measured latency overhead. If the answer is no,
   the product does not work.
2. **Will non-technical employees abandon consumer AI for a sanctioned alternative?**
   Tested by AI Chat adoption relative to seats provisioned. If the answer is no, the
   platform covers only developer traffic and delivers a fraction of its value.
3. **Does cost attribution produce numbers finance will act on?**
   Tested by whether a finance lead uses platform figures in a budget conversation.

Everything in scope below exists to answer one of those three questions. Everything
excluded does not.

### 2.2 The MVP thesis

> A Company can sign up, connect a provider, route real traffic through the Gateway,
> give every employee AI Chat, and — at the end of a month — produce a cost breakdown
> by team and an audit trail, without contacting support.

### 2.3 Target release

| Attribute | Value |
| --- | --- |
| Release designation | v1.0 (General Availability) |
| Preceding milestone | Private beta with 3–5 design partners |
| Target segment | Primary segment only — see [`target-users.md`](target-users.md) §3.1 |
| Deployment | Vendor-hosted, single region |
| Personas served | P-01, P-02, P-03, P-04, P-05 fully; P-06 partially; P-07 minimally |

---

## 3. Scope summary

| Capability area | MVP status | Requirements included |
| --- | --- | --- |
| Enterprise Authentication | Partial — email, OAuth2, MFA; no SAML/SCIM | 15 of 20 |
| Multi-Tenancy & Organization | Substantially complete | 13 of 16 |
| AI Providers | Three providers, full lifecycle | 13 of 17 |
| AI Gateway | Core routing, failover, streaming | 19 of 25 |
| AI Chat | Complete for individual use | 11 of 17 |
| Usage Tracking | Complete | 12 of 13 |
| Cost Tracking | Complete except forecasting and reconciliation | 11 of 14 |
| Analytics | Core dashboards | 8 of 12 |
| Billing | Complete for self-service | 13 of 14 |
| Developer APIs | Keys, usage API, real-time console | 12 of 16 |
| VS Code Extension | Core assistant | 11 of 15 |
| Governance | Model restriction, pattern blocking, retention control | 10 of 15 |
| Auditing | Complete except tamper-evidence and streaming | 9 of 12 |
| Notifications | Email and in-app | 8 of 9 |
| Permissions (cross-cutting) | Fixed roles; no custom roles | 6 of 7 |
| Cross-cutting (`FR-X`) | Complete except localization | 7 of 8 |
| **Total** | | **178 of 230** |

Requirement identifiers are defined in
[`product-requirements.md`](product-requirements.md). Any requirement marked `MVP`
there is in scope here; this document explains the reasoning and groups the work.

---

## 4. In scope — capability detail

### 4.1 Foundation: identity and organization

**Included.** Self-service Company creation. Email/password authentication with
strength policy and compromised-credential checking. OAuth2 via Google and Microsoft.
TOTP multi-factor, optionally mandatory. Session management with configurable expiry
and administrative termination. Employee invitation, suspension, and removal. Team
creation and membership with a primary Team per Employee. The full seven-role
permission model, enforced at execution. Complete deprovisioning that revokes API
keys.

**Why in MVP.** Nothing else can be built without it, and the deprovisioning
requirement (FR-AUTH-018) is a security property that cannot be retrofitted safely.

**Excluded.** SAML SSO and SCIM — required by segment 3.2, which is not the MVP
target, and each is a substantial body of work with its own certification burden.
Custom roles — the seven fixed roles cover the primary segment.

---

### 4.2 Provider management

**Included.** Provider Connections for OpenAI, Anthropic, and Google Gemini.
Credentials encrypted at rest and never retrievable after creation. Validation at
creation with actionable errors. Continuous health monitoring. Credential rotation
without interrupting traffic. Immediate disablement. Model catalog with capabilities,
context limits, and pricing. Deprecation notification. Per-Team model restriction.
Multiple connections per provider. Full audit of every credential operation.

**Why in MVP.** This is the credential sprawl problem from
[`problem-statement.md`](problem-statement.md) §3.1, and three providers is the
minimum that makes the neutrality claim real. Two would look like a hedge; three
demonstrates a pattern.

**Excluded.** Azure OpenAI — significant additional configuration surface, and
customers who need it usually also need SSO, placing them in segment 3.2.
Customer-hosted endpoints. Team-scoped connections.

---

### 4.3 AI Gateway

**Included.** Authenticated inference via Platform API Key. Chat completions against
any catalog model. Streaming. **An OpenAI-compatible request interface** so migration
requires only a base URL and credential change. Normalized error taxonomy preserving
original provider errors. Routing Policies with ordered fallback. Bounded retry with
backoff. Per-connection circuit breaking. Full routing-decision recording. Rate limits
at Company, Team, and Key scope. Budget enforcement. Governance evaluation. Request
timeouts. Token counting with estimation clearly marked. Tool and function calling
passed through natively. Health endpoint. The fail-open/fail-closed policy of
FR-GW-017 and FR-GW-018.

**Why in MVP.** This is the product. The OpenAI-compatible interface in particular is
not a convenience feature — it is the mechanism by which existing traffic migrates,
and therefore the mechanism by which coverage (goal G2.1) is achieved. Without it, every
customer faces a rewrite and coverage stalls.

**Excluded.** The native provider-neutral interface — compatibility mode is what
drives migration; a second interface can wait. Embeddings and multimodal — real
demand, but neither tests an MVP hypothesis. Caching. A/B traffic splitting. Request
cancellation.

> **Highest-risk area in the MVP.** The Gateway is in the customer's production
> request path. Its non-functional requirements are not negotiable and its failure
> modes must be documented before beta, not after.

---

### 4.4 AI Chat

**Included.** Multi-turn conversation with streaming. Persistent, searchable history
across devices. Conversation rename, organize, delete. Model selection from the
permitted set with a Company default. Markdown and syntax-highlighted rendering,
copy-to-clipboard. Regenerate and edit-to-branch. Explicit disclosure of what the
Company can observe. Retention per Company policy with per-Employee deletion. Full
routing through the same Gateway, metering, governance, and audit path as the API.

**Why in MVP.** Hypothesis 2. Also the reason the platform can claim complete
coverage: without a sanctioned employee-facing tool, shadow AI usage continues
regardless of how well the Gateway performs.

**Excluded.** Document attachment, shared prompt templates, conversation sharing,
knowledge-source grounding, response feedback. Each adds value; none determines
whether an employee prefers this to what they use today. Mobile-optimized layout is
excluded as a dedicated effort, but the interface must remain usable on a mobile
browser.

> **Quality bar.** AI Chat competes against consumer AI products, not against other
> enterprise tools. "Adequate" fails here. If the team cannot deliver something an
> employee would genuinely choose, that is a signal worth acting on before GA rather
> than after.

---

### 4.5 Usage, cost, and analytics

**Included.** One immutable Usage Record per request with the full attribution chain.
Token counts, latency, outcome, failure category. Estimation clearly flagged, with the
estimated proportion exposed. Queryable across every dimension. Stated freshness.
Export. Configurable retention. Cost Records from versioned, effective-dated pricing.
Aggregation by every dimension. Published accuracy tolerance. Budgets at Company,
Team, and Employee scope, alert-only by default, with optional hard enforcement.
Trend and period comparison. Company overview, per-dimension breakdowns, Gateway
reliability metrics, model adoption. Permission-scoped, exportable as structured data,
with freshness always displayed.

**Why in MVP.** Hypothesis 3. FR-USG-012 (no sampling) and FR-USG-013 (no silent
loss) are in MVP because a ledger with unrecorded gaps cannot be repaired later —
the data is simply absent.

**Excluded.** Forecasting, invoice reconciliation, multi-currency, anomaly detection,
saved views, scheduled reports, comparative model performance. All are refinements on
a foundation that must be correct first.

---

### 4.6 Developer APIs and VS Code extension

**Included.** Platform API Key creation scoped to Employee and Team, secret shown once,
optional expiry with notification, immediate revocation, scopes, last-used tracking,
and automatic revocation on deprovisioning. A public usage and cost API. Versioning
with a published deprecation policy. Consistent error structure. Machine-readable API
specification. Real-time console updates without polling.

Extension: authentication without manual credential handling. In-editor chat through
the same governed path. Selected code as context. Explain, refactor, generate tests,
document. Model selection. Usage and budget display. Surface-distinguished
attribution. Governance compliance including source-code restrictions. Streaming with
cancellation. Graceful failure when unreachable. Never transmits unselected file
content.

**Why in MVP.** The extension is the clearest expression of
[`mission.md`](mission.md) §4.1 — governed AI at the moment of use, more convenient
than the alternative. It is also the surface where developers form their opinion of
the platform.

**Excluded.** Organizational management API, audit export API, webhooks, client
libraries. In the extension: admin disablement, direct diff application, workspace
configuration, JetBrains support.

---

### 4.7 Governance and audit

**Included.** Governance Policies at Company or Team scope. **Monitor mode by
default**, recording what would have happened. Model and provider restriction by Team.
Pattern-based blocking. Audit events for every block or redaction. Content retention
configurable per Team, off by default, with enabling itself audited. Policy activity
reporting. Latency within budget.

Audit: events for every authentication, authorization decision, configuration change,
credential operation, export, and administrative action. Actor, action, target,
outcome, timestamp, context. Append-only, unmodifiable by any role. **No sampling.**
Searchable, exportable. Configurable retention, itself audited. No content in audit
records. Write failure treated as an incident.

**Why in MVP.** Audit completeness is architectural. A system that samples cannot be
made complete later, and the persona who cares most (P-06) treats sampling as
disqualifying. Monitor-mode-by-default is in MVP because it is what makes governance
adoptable at all.

**Excluded.** PII detection — meaningful accuracy is hard, and FR-GOV-007 requires
publishing false-positive characteristics, which requires measurement the MVP cannot
produce. Shipping weak detection under a governance label is worse than shipping none.
Also excluded: legal-hold process, time and network restrictions, approval workflows,
tamper-evidence, audit streaming, data-flow reports.

---

### 4.8 Billing and notifications

**Included.** Subscription plans with documented limits. Plan and consumption
visibility. Self-service upgrade, downgrade, and cancellation with consequences stated
before confirmation. Metering of billable units. Invoice generation. Card payment via
a compliant processor, with no card data stored. Renewal and payment-failure
notification. Documented grace period and degradation sequence. Trial with a clear
expiry path. Tax handling. **No charge for audit retention.** Full billing audit.

Notifications: budget thresholds, provider health changes, model deprecation,
security-relevant events. User-configurable preferences. Email delivery. Real-time
in-app notification. Rate limiting to prevent flooding.

**Why in MVP.** Self-service purchase is required for goal G4.4 and for the
ten-minute onboarding target G1.3.

**Excluded.** Invoice-based billing with purchase orders — needed for larger contracts
but not for the primary segment's self-service motion. Slack and Teams delivery.

> **Blocked.** FR-BILL-005 cannot be implemented until decision D-1 in
> [`business-goals.md`](business-goals.md) §11 defines the billable unit. This is the
> single most schedule-critical open decision in the MVP.

---

## 5. Explicitly out of scope

Recorded so that exclusion is a decision rather than an oversight. Each will be
requested during the MVP period; the answer is here.

| Excluded | Rationale | Target |
| --- | --- | --- |
| SAML SSO, SCIM | Segment 3.2 requirement; substantial effort with certification burden | v1.2 |
| Self-hosted deployment | Blocked on decision D-3; changes release and support model entirely | v2.0 or later |
| Azure OpenAI | Large configuration surface; correlated with SSO-requiring customers | v1.1 |
| Embeddings, multimodal | Real demand; tests no MVP hypothesis | v1.1 |
| PII detection | Requires measured accuracy to meet FR-GOV-007; weak detection under a governance label is a liability | v1.1 |
| Prompt management, evaluations | P-08 (tertiary) requirements | v2.0 |
| Agent and workflow support | Changes the metering unit; premature | Later |
| Knowledge-source grounding in Chat | Large scope; a product in itself | v2.0 |
| Webhooks, client libraries | Convenience over the documented API | v1.1 |
| JetBrains extension | VS Code first validates the pattern | v2.0 |
| Multi-region, data residency | Segment 3.2 requirement | v2.0 |
| Localization | Primary markets are English-language | v2.0 |
| Custom roles | Seven fixed roles cover the primary segment | v2.0 |
| Mobile applications | Browser-based Chat is sufficient | Later |
| Parent-organization hierarchy | Rare in the primary segment | Later |

---

## 6. MVP delivery sequence

Not a schedule — a dependency order. Estimation is an engineering deliverable.

| Stage | Content | Exit criterion |
| --- | --- | --- |
| **1. Foundation** | Tenancy, identity, permissions, audit skeleton | A Company can be created, Employees invited, permissions enforced, every action audited |
| **2. Provider layer** | Connections, credential encryption, model catalog, health | A provider can be connected, validated, rotated, and disabled |
| **3. Gateway core** | Routing, OpenAI-compatible interface, streaming, error taxonomy | A request routes to a provider and returns a streamed response |
| **4. Ledger** | Usage Records, Cost Records, pricing versioning | Every request produces attributable usage and cost |
| **5. Resilience** | Fallback, retry, circuit breaking, timeouts, rate limits | A provider outage is survived without a customer-visible failure |
| **6. Controls** | Budgets, governance policies, monitor mode | A budget alerts and enforces; a policy blocks in enforce mode and records in monitor mode |
| **7. Surfaces** | Web console, analytics, AI Chat | A Member can converse; an Admin can see cost by Team |
| **8. Developer experience** | Public API, VS Code extension, API specification | A developer integrates in under ten minutes |
| **9. Commercial** | Plans, payment, invoicing, trial | A Company can subscribe without contacting support |
| **10. Hardening** | Load testing, failure injection, security review, documentation | NFR targets met; failure modes documented |

**Sequencing notes.** Stage 4 must not be deferred behind stage 5 — a ledger with gaps
cannot be reconstructed, so metering must be correct before traffic volume grows.
Stage 10 is not a buffer; if compressed, the Gateway's production-readiness claim
becomes unsupported.

---

## 7. Definition of done

Per [`mission.md`](mission.md) §4.9, a feature is complete only with every layer
present. For each MVP capability:

- [ ] Backend implementation with permission enforcement at execution
- [ ] Tenant isolation verified by test
- [ ] Audit events emitted for every relevant action
- [ ] Usage metering where applicable
- [ ] Frontend implementation meeting WCAG 2.1 AA
- [ ] Error states designed and implemented per FR-X-001
- [ ] Unit, integration, and functional tests
- [ ] Architecture tests confirming module boundaries hold
- [ ] API specification updated where the public surface changed
- [ ] User-facing documentation
- [ ] Relevant NFR targets verified under load

---

## 8. MVP success criteria

Assessed 90 days after GA. Distinct from the commercial targets in
[`business-goals.md`](business-goals.md) — these test whether the product works.

| # | Criterion | Threshold | Tests hypothesis |
| --- | --- | --- | --- |
| S-1 | Median time from signup to first governed request | ≤ 15 minutes | 1 |
| S-2 | Design partners routing production traffic | ≥ 3 of 5 | 1 |
| S-3 | Gateway p95 latency overhead | Within published budget | 1 |
| S-4 | Gateway availability | Meets published target | 1 |
| S-5 | AI Chat weekly active share of provisioned seats | ≥ 40% | 2 |
| S-6 | Companies with ≥ 1 non-technical active user | ≥ 60% | 2 |
| S-7 | Cost figures reconciling to provider invoices | Within published tolerance | 3 |
| S-8 | Design partners using platform cost data in budget discussions | ≥ 2 | 3 |
| S-9 | Usage Records lost or unattributed | Zero | Foundation |
| S-10 | Audit events lost | Zero | Foundation |
| S-11 | Cross-tenant data exposure incidents | Zero | Foundation |

**S-9 through S-11 are pass/fail gates, not targets.** Any non-zero value blocks GA
regardless of every other result.

---

## 9. Assumptions

| # | Assumption | Impact if wrong |
| --- | --- | --- |
| A-1 | Three providers are sufficient to demonstrate neutrality | Azure OpenAI moves into MVP, extending the schedule |
| A-2 | OpenAI-compatible interface enables low-friction migration for most customers | Coverage stalls; native interface and migration tooling become urgent |
| A-3 | AI Chat can reach consumer-comparable quality within MVP scope | Hypothesis 2 fails; the platform becomes developer-only |
| A-4 | Pattern-based blocking is adequate governance for MVP customers | PII detection moves into MVP |
| A-5 | Self-service billing is sufficient for the primary segment | Invoice billing moves forward |
| A-6 | Single-region hosting is acceptable to MVP customers | Multi-region moves forward significantly |
| A-7 | Decision D-1 resolves early enough not to block stage 9 | Billing implementation blocks release |
| A-8 | Governance evaluation fits within the latency budget | Enforcement becomes asynchronous, weakening the control |

---

## 10. Future considerations

- **The MVP boundary will be tested by the first large prospect.** A segment 3.2
  customer will appear during beta and ask for SAML and self-hosting. Accepting that
  work mid-MVP is the most likely way this release slips. The answer should be decided
  before the conversation happens.
- **AI Chat quality is an ongoing commitment, not a milestone.** Consumer AI sets the
  bar and moves it. Post-MVP capacity must be reserved for keeping pace, or Chat will
  degrade relative to alternatives even with no changes.
- **The ledger's design outlives the MVP.** Usage and Cost Record structure is the
  hardest thing to change later — every historical record carries the original shape.
  This deserves disproportionate design attention in stage 4.
- **Excluding PII detection has a sales cost.** It will appear on security
  questionnaires. The honest answer — that it ships when its accuracy can be published
  — is defensible, and should be prepared as a stated position rather than improvised.
- **Governance monitor mode may reveal uncomfortable data.** The first customers to run
  it will discover policy violations they did not know about. This is the feature
  working correctly, and both sales and support should expect it.

---

## 11. Cross references

| Document | Relationship |
| --- | --- |
| [`product-requirements.md`](product-requirements.md) | Full requirement definitions and identifiers |
| [`future-roadmap.md`](future-roadmap.md) | Where excluded scope is scheduled |
| [`business-goals.md`](business-goals.md) | Commercial targets and blocking decisions |
| [`user-personas.md`](user-personas.md) | Personas served and deferred |
| [`target-users.md`](target-users.md) | Segment targeting rationale |
| [`non-functional-requirements.md`](non-functional-requirements.md) | Quality targets in §8 |
| [`mission.md`](mission.md) | Definition of done and scope principles |
| [`glossary.md`](glossary.md) | Terminology |
