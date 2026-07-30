# User Personas

| Field | Value |
| --- | --- |
| Document | User Personas |
| Version | 1.0 |
| Status | Draft — pending validation through discovery interviews |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, Sales, Marketing |

---

## 1. Purpose

This document describes the individuals who interact with MaintOrbit AI: what they
are responsible for, what they are trying to accomplish, what currently obstructs
them, and what would cause them to adopt or abandon the platform.

Personas are a design and prioritization tool. When a requirement is contested, the
question is which persona it serves and how central that persona is to the value
proposition.

**Status note.** These personas are constructed from the problem analysis in
[`problem-statement.md`](problem-statement.md) and segment definitions in
[`target-users.md`](target-users.md). They are hypotheses. They must be validated
against a minimum of twelve discovery interviews before requirements are frozen, and
revised where reality disagrees. Names are fictional composites.

**Convention.** All personas are referred to with they/them pronouns. Real users have
their own; nothing in the product's design should depend on this attribute.

---

## 2. Overview

Eight personas across three tiers of importance:

| Tier | Persona | Role in platform | Why this tier |
| --- | --- | --- | --- |
| **Primary** | P-01 Priya — Director of Platform Engineering | Owner / Company Admin | Economic buyer and technical champion |
| **Primary** | P-02 Marcus — Staff Platform Engineer | Company Admin | Implements, operates, and can veto |
| **Primary** | P-03 Dana — Senior Application Developer | Developer | Daily API consumer; determines coverage |
| **Primary** | P-04 Sofia — Product Designer | Member | Represents the largest user population |
| **Secondary** | P-05 Raj — Director of Finance | Billing Admin | Justifies renewal; owns the budget |
| **Secondary** | P-06 Elena — Head of Security & Compliance | Auditor | Gatekeeper; can block the purchase |
| **Secondary** | P-07 Tom — IT Administrator | Company Admin | Owns identity and lifecycle |
| **Tertiary** | P-08 Aisha — Machine Learning Lead | Developer / Team Lead | Drives advanced capability requirements |

The four primary personas must all be satisfied for the product to succeed. Priya
and Marcus decide whether it is bought; Dana and Sofia decide whether it is used.
A product that satisfies only the first pair is purchased and abandoned — the
characteristic failure mode of this category.

---

## 3. Primary personas

### P-01 — Priya Raghavan, Director of Platform Engineering

| Attribute | Detail |
| --- | --- |
| Organization | 900-employee B2B SaaS company, ~140 engineers |
| Reports to | VP Engineering |
| Team | 11 platform engineers across infrastructure, developer experience, and observability |
| Platform role | Owner |
| Technical depth | High — formerly a distributed systems engineer |
| Budget authority | Approves up to USD 100k annually without escalation |

**Responsibilities.** Owns the internal platform that ~140 engineers build on:
CI/CD, observability, service infrastructure, developer tooling. AI infrastructure
landed on their plate by default when it became clear no one else owned it.

**What they are trying to accomplish.** Make AI a governed capability of the internal
platform rather than a per-team improvisation — without becoming a bottleneck and
without a multi-quarter build.

**Current situation.** Nine teams use AI in production across three providers. Six
provider accounts exist, two created by people who have since left. Last quarter's
combined invoice was USD 47,000 with no attribution. In March a deprecated model
caused an incident because no one tracked deprecation notices. Their VP has asked
twice for an AI cost breakdown by team; both answers were estimates.

**Frustrations.**
- Accountable for a system they have no visibility into.
- Every proposal to centralize is met with "will this slow us down?"
- A previous attempt at an internal wrapper was abandoned after one quarter.
- Cannot make a credible build-versus-buy case without knowing current spend.

**Goals.**
1. One place that shows every AI request, its cost, and its owner.
2. Provider failover so a vendor outage is not a product incident.
3. Credential management that survives employee turnover.
4. Achieve this without a dedicated team.

**Adoption criteria.** Working proof of value within a two-week pilot: one team
routed, real cost attribution, no developer complaints.

**Abandonment triggers.** Developers escalating about latency or reliability. A
gateway outage causing a customer-visible incident. Cost figures that do not reconcile
with provider invoices.

**Relevant capabilities.** Provider Connections, AI Gateway, Cost Tracking, Analytics,
Audit Log, Budgets.

---

### P-02 — Marcus Feld, Staff Platform Engineer

| Attribute | Detail |
| --- | --- |
| Organization | Same as P-01; reports to Priya |
| Experience | 12 years; 4 in platform engineering |
| Platform role | Company Admin |
| Technical depth | Very high — operates the runtime, reads source before trusting it |
| Budget authority | None — but effective veto |

**Responsibilities.** Evaluates, integrates, and operates platform infrastructure.
Carries the pager. Whatever is adopted, Marcus is who gets paged when it breaks.

**What they are trying to accomplish.** Add AI governance without adding an
unreliable dependency to the production request path.

**Current situation.** Assigned to evaluate three options. Has already rejected one
for lacking self-hosted deployment and another for opaque failure behaviour. Reads
architecture documentation before pricing pages.

**Frustrations.**
- Vendors describing failure modes vaguely or not at all.
- Products that are unobservable from outside — no metrics, no structured logs.
- Being asked to put a black box in the critical path of production traffic.
- Migration paths that assume a greenfield start.

**Goals.**
1. Understand exactly what happens when the gateway or a provider fails.
2. Health checks, metrics, and structured logs that integrate with existing monitoring.
3. A migration path that does not require rewriting nine services at once.
4. Confidence that a platform outage degrades rather than halts operations.

**Adoption criteria.** Documented failure modes and timeouts. Metrics endpoint.
Deterministic, inspectable routing behaviour. A credible answer to "what happens when
you go down."

**Abandonment triggers.** Undocumented behaviour in the data path. An incident where
platform logs cannot explain what happened. Discovering the vendor samples audit data.

**Relevant capabilities.** AI Gateway, routing and failover configuration, health and
metrics endpoints, audit completeness, deployment model.

> **Design implication.** Marcus is why [`mission.md`](mission.md) §4.6 exists.
> Transparency about failure behaviour is a feature aimed directly at this persona,
> and it is decisive in technical evaluation.

---

### P-03 — Dana Okonkwo, Senior Application Developer

| Attribute | Detail |
| --- | --- |
| Organization | Same; product engineering, not platform |
| Experience | 7 years, full-stack |
| Platform role | Developer |
| Technical depth | High in application development; limited interest in infrastructure |
| Budget authority | None |

**Responsibilities.** Ships customer-facing features, three of which call AI models
for summarization, classification, and drafting.

**What they are trying to accomplish.** Ship features. AI is a means; the platform is
a means to the means. Interest in governance is close to zero.

**Current situation.** Uses one provider's SDK directly. Holds a key from a shared
vault. Has written retry logic twice, in different styles. Spent a day last month
debugging a rate-limit failure with an unhelpful error. Does not know what their
feature costs to run.

**Frustrations.**
- Rebuilding retries, streaming, and error handling in every service.
- Provider errors that do not explain the actual problem.
- Being asked to migrate to a new model with no compatibility guidance.
- Platform initiatives that add process without adding capability.

**Goals.**
1. Make an AI call work correctly in under ten minutes.
2. Stop maintaining retry and streaming plumbing.
3. Know what a feature costs without asking anyone.
4. Change models without changing code.

**Adoption criteria.** Drop-in compatibility with existing provider SDKs. A single
base-URL and credential change to migrate. Streaming works. Errors are clear.
Latency overhead is imperceptible.

**Abandonment triggers.** Any added latency they can feel. An error message worse
than the provider's. A required workflow step that direct integration does not have.
Being asked to fill in metadata for someone else's dashboard.

**Relevant capabilities.** AI Gateway, Developer APIs, VS Code Extension, SDK
compatibility, per-feature cost visibility.

> **Design implication.** Dana never asked for this product and will compare it
> constantly against calling the provider directly. Every developer-facing decision
> should be tested against: *would Dana choose this over the SDK?* This persona is the
> reason [`mission.md`](mission.md) §4.1 outranks every other principle.

---

### P-04 — Sofia Alvarez, Senior Product Designer

| Attribute | Detail |
| --- | --- |
| Organization | Same; design team of 8 |
| Experience | 9 years in product design |
| Platform role | Member |
| Technical depth | Comfortable with software; does not write code |
| Budget authority | None |

**Responsibilities.** Product design — research synthesis, copy, specifications,
stakeholder communication.

**What they are trying to accomplish.** Use AI for the writing and synthesis work
that surrounds design, without violating a policy they have not read.

**Current situation.** Uses a personal consumer AI subscription, paid personally, for
work tasks: summarizing research transcripts, drafting specifications, rewriting copy.
Aware this is probably not allowed. Has pasted anonymized user research into it. No
sanctioned alternative has ever been offered.

**Frustrations.**
- Paying personally for a tool that makes them better at their job.
- Uncertainty about what is permitted, with no clear place to ask.
- Internal tools that are consistently worse than consumer equivalents.
- Expecting that any sanctioned tool will be slow, restricted, and unpleasant.

**Goals.**
1. A capable assistant they are allowed to use.
2. Clarity about what is acceptable to share.
3. Conversation history that persists and is searchable.
4. Quality comparable to the consumer product they already use.

**Adoption criteria.** As fast and as capable as what they use today. No training
required. Clear statement of what the organization can see.

**Abandonment triggers.** Noticeably slower or less capable than the consumer
alternative. Restrictive filtering with unexplained blocks. Discovering their manager
reads their conversations without that having been disclosed.

**Relevant capabilities.** AI Chat, conversation history, model selection,
transparency about visibility.

> **Design implication.** Sofia represents 40–80% of eventual users and nearly all
> current shadow AI usage. AI Chat competing on quality with consumer products is
> not optional — it is the mechanism by which the governance gap actually closes.
> An inferior sanctioned tool does not displace the unsanctioned one.

---

## 4. Secondary personas

### P-05 — Raj Menon, Director of Finance

| Attribute | Detail |
| --- | --- |
| Organization | Same; finance team of 6 |
| Platform role | Billing Admin |
| Technical depth | Low technically; very high in financial systems |
| Budget authority | Controls the budget the purchase comes from |

**Responsibilities.** Departmental budgeting, forecasting, vendor spend management,
month-end close.

**What they are trying to accomplish.** Move AI from an unpredictable line item to a
forecastable, attributable one.

**Current situation.** AI spend grew from USD 3k to USD 47k per quarter in
eighteen months. It cannot be allocated to departments, so it sits in a general
engineering pool. Cannot forecast next year. Asked engineering for a breakdown and
received a spreadsheet built by hand.

**Frustrations.**
- A materially growing cost line that cannot be attributed or forecast.
- Discovering overruns thirty days after they occur.
- Technical metrics — tokens — that do not map to financial reporting.
- No enforcement mechanism, only observation.

**Goals.**
1. Cost by department, team, and product, exportable to the finance system.
2. Alerts before a budget is exceeded, not after.
3. Hard caps where required.
4. Figures that reconcile with provider invoices.

**Adoption criteria.** Attribution matching the organizational chart. CSV and API
export. Reconciliation within a stated tolerance.

**Abandonment triggers.** Figures materially disagreeing with provider invoices with
no explanation. Attribution that cannot follow reorganizations.

**Relevant capabilities.** Cost Tracking, Budgets, Analytics, Billing, exports.

> **Design implication.** Raj is why cost accuracy carries a published tolerance
> rather than an implied one. A cost report that cannot be reconciled is not a finance
> tool. See [`mission.md`](mission.md) §4.4.

---

### P-06 — Elena Vasquez, Head of Security & Compliance

| Attribute | Detail |
| --- | --- |
| Organization | Same; security team of 4 |
| Platform role | Auditor |
| Technical depth | High in security architecture |
| Budget authority | None — but can block any purchase |

**Responsibilities.** Security architecture, vendor risk assessment, SOC 2 program,
incident response, customer security questionnaires.

**What they are trying to accomplish.** Bring AI usage inside the control framework
that already governs every other data-processing activity.

**Current situation.** Cannot produce an AI data-flow inventory for the SOC 2 audit.
Customer security questionnaires now ask about AI subprocessors and answers are
partly guesswork. Knows employees use personal AI accounts and has no measurement.
Aware that provider keys exist without inventory or rotation.

**Frustrations.**
- Accountable for risk in a system with no telemetry.
- Vendors making security claims that do not survive questioning.
- Being consulted at the end of an evaluation rather than the start.
- Governance tools that generate alerts nobody acts on.

**Goals.**
1. A complete, immutable record of AI usage attributable to identities.
2. Enforced policy at the egress point.
3. Documented data flows for audit and questionnaires.
4. Immediate, verifiable revocation on offboarding.

**Adoption criteria.** Complete audit capture with no sampling. Clear data-flow
documentation. Honest answers about the vendor's own security posture, including gaps.

**Abandonment triggers.** Discovering audit records are sampled or incomplete.
Vendor security claims that do not survive assessment. An incident the platform cannot
explain.

**Relevant capabilities.** Audit Log, Governance policies, Enterprise Authentication,
retention configuration, data-flow documentation.

> **Design implication.** Elena is the reason [`mission.md`](mission.md) §4.5 permits
> no sampling, and the reason honesty about limitations is an operating principle.
> This persona detects overstatement reliably and treats it as disqualifying.

---

### P-07 — Tom Bergman, IT Administrator

| Attribute | Detail |
| --- | --- |
| Organization | Same; IT team of 5 |
| Platform role | Company Admin |
| Technical depth | Moderate-high in identity and endpoint management |
| Budget authority | Small tooling budget |

**Responsibilities.** Identity provider administration, SaaS application lifecycle,
onboarding and offboarding, access reviews.

**What they are trying to accomplish.** Ensure the platform behaves like every other
managed SaaS application: provisioned from the directory, deprovisioned reliably,
reviewable during access audits.

**Current situation.** Manages roughly 60 SaaS applications. Roughly a third support
SCIM; the rest require manual offboarding, which is where mistakes happen. Quarterly
access reviews are largely manual.

**Frustrations.**
- Applications without SSO or automated provisioning.
- Offboarding that leaves residual access, especially API credentials.
- Access reviews requiring manual data collection.
- Shadow SaaS purchased without IT involvement.

**Goals.**
1. SSO through the existing identity provider.
2. Automated provisioning and deprovisioning.
3. Offboarding that revokes everything, including API keys, immediately.
4. Exportable access reports for reviews.

**Adoption criteria.** SSO support, group-to-role mapping, verifiable complete
deprovisioning.

**Abandonment triggers.** Discovering a deprovisioned employee's API key still works.
No SSO support at enterprise scale.

**Relevant capabilities.** Enterprise Authentication, SSO, SCIM, role mapping,
credential lifecycle, access reports.

> **Design implication.** Tom's requirement that deprovisioning revoke *everything*
> including active API keys is a hard functional requirement, not a convenience. This
> is a common and serious gap in comparable platforms.

---

## 5. Tertiary persona

### P-08 — Aisha Karim, Machine Learning Lead

| Attribute | Detail |
| --- | --- |
| Organization | Same; ML team of 6 |
| Platform role | Developer + Team Lead |
| Technical depth | Very high in ML; high in engineering |
| Budget authority | Team-level budget |

**Responsibilities.** ML capability across the product — model selection, evaluation,
prompt engineering, quality measurement.

**What they are trying to accomplish.** Choose the right model for each task on
evidence, and detect quality regressions when providers change models underneath them.

**Current situation.** Maintains a spreadsheet comparing models on cost, latency, and
manually scored quality. It is perpetually out of date. Has no systematic way to
detect provider-side quality drift.

**Frustrations.**
- Model comparison requiring bespoke harnesses each time.
- Silent provider-side changes affecting output quality.
- No connection between quality evaluation and production cost data.
- Prompt versions scattered across repositories with no history.

**Goals.**
1. Compare models on real traffic without rewriting integrations.
2. Detect quality regressions automatically.
3. Version and manage prompts centrally.
4. Correlate quality with cost to make defensible trade-offs.

**Adoption criteria.** Ability to route a traffic percentage to an alternative model
and compare outcomes. Access to raw request data.

**Abandonment triggers.** Inability to access underlying data. Abstraction that hides
provider-specific parameters they need.

**Relevant capabilities.** AI Gateway routing rules, Analytics, model comparison;
post-MVP: evaluations, prompt management, A/B routing.

> **Design implication.** Aisha's requirements are largely post-MVP but shape the
> data model now: request records must retain enough detail to support later
> evaluation and comparison. Discarding that detail at MVP forecloses the capability.

---

## 6. Persona-to-capability matrix

| Capability | P-01 Priya | P-02 Marcus | P-03 Dana | P-04 Sofia | P-05 Raj | P-06 Elena | P-07 Tom | P-08 Aisha |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Enterprise Authentication | ● | ● | ○ | ○ | – | ● | ● | ○ |
| Companies / Teams / Employees | ● | ○ | – | ○ | ● | ○ | ● | ○ |
| AI Providers | ● | ● | ○ | – | – | ● | – | ● |
| AI Gateway | ● | ● | ● | – | – | ○ | – | ● |
| AI Chat | ○ | – | ○ | ● | – | ○ | – | ○ |
| Usage Tracking | ● | ○ | ○ | – | ● | ● | – | ● |
| Cost Tracking | ● | – | ○ | – | ● | – | – | ● |
| Analytics | ● | ○ | ○ | – | ● | ● | ○ | ● |
| Billing | ○ | – | – | – | ● | – | ○ | – |
| Developer APIs | ○ | ● | ● | – | – | – | – | ● |
| VS Code Extension | ○ | ○ | ● | – | – | – | – | ● |
| Governance policies | ○ | ○ | – | ○ | – | ● | ○ | – |
| Audit Log | ● | ● | – | – | ○ | ● | ● | – |

● primary user · ○ secondary interest · – not relevant

---

## 7. Assumptions

| # | Assumption | Validation | Risk if wrong |
| --- | --- | --- | --- |
| A-1 | P-01 is the economic buyer | Discovery interviews; win/loss | Messaging and pricing aimed at the wrong role |
| A-2 | P-02 can veto a purchase | Sales observation | Under-investment in technical transparency |
| A-3 | P-03 will not tolerate perceptible latency overhead | Design-partner benchmarks | Core adoption assumption fails |
| A-4 | P-04 will switch from consumer AI if quality is comparable | Pilot measurement | Coverage stalls; governance gap persists |
| A-5 | P-05 requires invoice reconciliation | Finance interviews | Over-investment in cost accuracy |
| A-6 | P-06 treats sampled audit data as disqualifying | Security review observation | Over-investment in audit completeness |
| A-7 | P-07's requirements are largely post-MVP | Segment analysis | SSO/SCIM must move into MVP |
| A-8 | P-08's needs shape data model but not MVP scope | Roadmap review | Evaluation features required earlier |

---

## 8. Future considerations

- **Personas will split as the product matures.** "Developer" already contains at
  least three distinct behaviours: application developers (Dana), platform engineers
  (Marcus), and ML practitioners (Aisha). Further segmentation will be needed.
- **A ninth persona is likely: the AI programme lead.** Larger organizations are
  creating roles dedicated to AI adoption and governance. If this becomes common, it
  may displace P-01 as the economic buyer.
- **P-04's expectations will rise continuously.** Consumer AI sets the quality bar and
  moves it every few months. AI Chat is committing to a permanent race, which should
  inform build-versus-integrate decisions for that surface.
- **P-06's requirements will formalize.** As AI-specific regulation matures, security
  and compliance needs shift from internal policy to external obligation, making this
  persona more powerful in the buying process.
- **P-08 may become primary in AI-native companies.** In segment 3.3 of
  [`target-users.md`](target-users.md), the ML lead is frequently the decision-maker.
- **Employee sentiment needs its own research.** P-04's relationship with
  organizational visibility into their AI usage is assumed here and has not been
  tested. It could be a significant adoption barrier.

---

## 9. Cross references

| Document | Relationship |
| --- | --- |
| [`target-users.md`](target-users.md) | Organizational segments these personas belong to |
| [`problem-statement.md`](problem-statement.md) | Problems each persona experiences |
| [`product-requirements.md`](product-requirements.md) | Requirements traced to personas |
| [`mvp-features.md`](mvp-features.md) | Which personas the first release serves |
| [`future-roadmap.md`](future-roadmap.md) | When remaining persona needs are met |
| [`mission.md`](mission.md) | Principles derived from persona constraints |
| [`glossary.md`](glossary.md) | Role and capability definitions |
