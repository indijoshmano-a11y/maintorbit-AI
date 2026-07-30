# Problem Statement

| Field | Value |
| --- | --- |
| Document | Problem Statement |
| Version | 1.0 |
| Status | Draft — pending review |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, Leadership |

---

## 1. Purpose

This document states the problem MaintOrbit AI exists to solve, in terms that can be
validated or falsified by customer research. It is the reference point against which
every proposed feature is judged: a feature that does not trace back to a problem
described here is out of scope until this document is amended.

It deliberately does not describe a solution. Solution scope lives in
[`mvp-features.md`](mvp-features.md) and [`product-requirements.md`](product-requirements.md).

---

## 2. Overview

Enterprises adopted generative AI faster than they built the controls to manage it.
Adoption happened bottom-up — an individual team signed up for a provider account,
put an API key in an environment variable, and shipped. Repeated across dozens of
teams over two or three years, that pattern produced a predictable end state:
credentials nobody owns, spend nobody can attribute, data flows nobody has mapped,
and an audit trail that does not exist.

The organization now has a governance gap it cannot close by asking teams to stop.
AI is load-bearing in production systems and in daily knowledge work. The only
viable path is to put a managed layer between the enterprise and its AI providers —
one that preserves developer velocity while restoring the visibility, control, and
accountability the enterprise requires.

That layer is what MaintOrbit AI provides.

---

## 3. The problems in detail

### 3.1 Credential sprawl and unmanaged secrets

Provider API keys are the highest-value secrets in the modern enterprise: they carry
direct spend authority and unrestricted access to a data egress channel. In practice
they are distributed as plaintext.

Observed pattern:

- Keys live in `.env` files, CI variables, Kubernetes secrets, shared password
  managers, Slack DMs, and internal wiki pages, often simultaneously.
- No single system knows how many keys exist, who created them, or which workloads
  depend on them.
- Rotation is avoided because nobody can predict what will break.
- Offboarding an employee does not revoke the keys they created or copied.
- A leaked key is discovered through an anomalous invoice, not through detection.

**Consequence:** an unbounded, unmonitored spend and data-egress liability with no
owner and no revocation path.

---

### 3.2 No cost attribution

AI spend arrives as a single monthly invoice per provider. It is denominated in
tokens, not in business activity.

- Finance cannot answer "which team spent this," "which product feature spent this,"
  or "what did this customer cost us to serve."
- Engineering cannot detect a regression that triples token consumption until the
  invoice lands, up to thirty days later.
- Budget enforcement is impossible: there is no mechanism to stop spend at a
  threshold, only to observe it after the fact.
- Cost per provider is not comparable — each vendor meters and prices differently,
  and unit economics shift with every model release.

**Consequence:** an expense line growing at a rate leadership cannot forecast,
attribute, or cap.

---

### 3.3 Provider concentration risk and switching cost

Most organizations integrate one provider's SDK directly into application code.

- A provider outage or rate-limit event becomes a customer-visible product outage,
  with no automatic fallback path.
- Model deprecation forces coordinated code changes across every service that
  hardcoded a model identifier.
- Evaluating an alternative provider requires re-integration work, so evaluations do
  not happen and pricing leverage is lost.
- Regional data-residency requirements cannot be satisfied without a parallel
  integration.

**Consequence:** availability tied to a single vendor's reliability, and commercial
terms dictated by a vendor who knows switching is expensive.

---

### 3.4 No governance over what leaves the organization

Every AI request is an egress event carrying arbitrary text to a third party.

- No inventory exists of which systems send data to which providers.
- No enforcement point exists for redaction, content filtering, or blocking.
- Retention terms differ per provider and per plan, and are rarely tracked.
- Regulated data — personal data, health data, payment data, source code under
  customer NDA — can reach a provider with no policy check.

**Consequence:** an unmapped data-flow surface that fails security review, blocks
enterprise sales, and creates regulatory exposure.

---

### 3.5 Fragmented and inequitable access

AI capability inside the enterprise is distributed by job title rather than by need.

- Developers hold API access and build their own tooling.
- Non-technical employees — legal, finance, marketing, support, HR — have no
  sanctioned option, so they use personal consumer accounts, moving corporate data
  outside the organization entirely.
- IT has no way to provision, deprovision, or observe either group.

**Consequence:** shadow AI usage that is invisible precisely where it is riskiest.

---

### 3.6 Absent audit trail

- No record links an AI request to an authenticated identity.
- No record captures which model answered, at what cost, under what configuration.
- Security incident response cannot reconstruct what was sent to a provider.
- Compliance frameworks that require access logging over data-processing activities
  cannot be satisfied.

**Consequence:** audit findings, blocked certifications, and an inability to answer
basic questions after an incident.

---

### 3.7 Duplicated platform engineering

Every team independently rebuilds the same non-differentiating plumbing: streaming,
retries with backoff, rate-limit handling, token counting, timeout tuning, error
normalization, and cost estimation. Each implementation is subtly different and
independently buggy.

**Consequence:** engineering effort spent on undifferentiated infrastructure, and
inconsistent reliability characteristics across the product portfolio.

---

## 4. Who experiences these problems

| Problem | Felt most acutely by |
| --- | --- |
| Credential sprawl | Security & compliance leadership, IT administration |
| No cost attribution | Finance, engineering leadership |
| Concentration risk | Platform engineering, engineering leadership |
| Governance gap | Security & compliance leadership, legal |
| Fragmented access | Non-technical employees, IT administration |
| Absent audit trail | Security & compliance leadership, internal audit |
| Duplicated plumbing | Application developers, platform engineering |

Detailed profiles are in [`user-personas.md`](user-personas.md); segment definitions
are in [`target-users.md`](target-users.md).

---

## 5. Why this problem is urgent now

1. **Multi-provider is the default.** Organizations no longer standardize on one
   model vendor. Capability, price, and latency differ enough per task that using
   several is rational — which multiplies every problem above.
2. **Model churn is continuous.** Model versions are deprecated on timelines shorter
   than enterprise release cycles. Direct SDK integration has become a recurring
   maintenance cost rather than a one-time one.
3. **AI spend crossed the materiality threshold.** It moved from an experimental line
   item to one large enough to attract finance scrutiny and require forecasting.
4. **Regulatory attention is rising.** Obligations around transparency,
   record-keeping, and risk management for AI systems are moving from proposal to
   enforcement across major jurisdictions, and record-keeping is the hardest
   obligation to satisfy retroactively.
5. **Security review has become a sales gate.** Enterprises selling to enterprises
   are now asked to document their AI data flows. Organizations that cannot lose deals.

---

## 6. Why existing approaches fall short

| Approach | What it solves | What it leaves unsolved |
| --- | --- | --- |
| Direct provider SDK integration | Fastest path to a first prototype | Every problem in section 3 |
| Open-source proxy / gateway library | Routing, fallback, basic key management | Identity, org hierarchy, chat for non-technical staff, finance-grade cost reporting, audit retention, and the operational burden of self-hosting |
| LLM observability tooling | Traces, latency and error analytics, evaluation | Control — it observes traffic but cannot enforce policy, cap spend, or manage credentials |
| Enterprise chat assistant products | Non-technical employee access | Developer API access, provider governance, cost attribution across programmatic workloads |
| Cloud-vendor AI platforms | Deep integration within one cloud | Cross-provider neutrality, which is the entire premise |
| Internal build | Exact fit to local requirements | 6–18 months of platform engineering on a non-differentiating capability, then indefinite maintenance |

The recurring outcome is a stack of three or four partial tools with overlapping
cost, inconsistent identity models, and gaps between them. See
[`competitor-analysis.md`](competitor-analysis.md).

---

## 7. Problem statement

> Enterprises have adopted AI across many teams and many providers without a common
> control plane. As a result they cannot say who is using AI, what it costs per team,
> what data is leaving the organization, or what happened during an incident — and
> they cannot fix this without either slowing developers down or building a platform
> they do not want to own.
>
> MaintOrbit AI is the control plane that closes that gap: a single, provider-neutral
> layer through which every AI request in the organization is authenticated,
> governed, routed, metered, and recorded.

---

## 8. Evidence required to validate this problem

This statement is a hypothesis until tested. Before Phase 2 closes, product should
gather:

| Evidence | Method | Validates |
| --- | --- | --- |
| Count of provider keys and their owners in target accounts | Discovery interview | §3.1 |
| Time to answer "what did Team X spend on AI last month" | Discovery interview | §3.2 |
| Number of distinct provider integrations in production | Technical discovery | §3.3 |
| Existence of a documented AI data-flow inventory | Security questionnaire review | §3.4 |
| Proportion of employees using unsanctioned consumer AI accounts | Anonymous survey | §3.5 |
| Whether AI usage appears in the current audit scope | Compliance interview | §3.6 |
| Engineering hours spent on provider integration maintenance | Engineering interview | §3.7 |

A minimum of twelve discovery interviews across at least three industry verticals is
recommended before requirements are frozen.

---

## 9. Out of scope

MaintOrbit AI does not attempt to solve the following, and no requirement should
assume it does:

- **Training or fine-tuning foundation models.** The platform brokers access to
  models; it does not produce them.
- **Being an AI provider.** The platform never competes with the providers it routes to.
- **Application-layer AI product development.** The platform does not build the
  customer-facing AI features an organization ships; it is the layer beneath them.
- **General-purpose API management.** Scope is AI traffic, not all enterprise APIs.
- **Data warehousing.** Usage and cost data are retained for platform analytics and
  export, not as a general analytics store.
- **Endpoint or network security.** The platform governs traffic that passes through
  it and makes no claim about traffic that bypasses it.

Section 9 is a deliberate constraint. See [`vision.md`](vision.md) §6 for the
long-term boundary and [`future-roadmap.md`](future-roadmap.md) for items that are
out of scope now but not permanently.

---

## 10. Assumptions

| # | Assumption | Risk if wrong |
| --- | --- | --- |
| A-1 | Enterprises will route production AI traffic through a third-party control plane rather than build one | Invalidates the product category; mitigated by self-hosted deployment |
| A-2 | Multi-provider usage continues rather than consolidating to a single dominant vendor | Reduces the value of routing and neutrality, though governance and cost value remain |
| A-3 | Provider APIs remain sufficiently similar to abstract behind a unified interface | Increases per-provider engineering cost and weakens the abstraction |
| A-4 | Budget authority for this purchase sits with engineering or platform leadership | Lengthens sales cycle; changes the primary persona |
| A-5 | The gateway's added latency is acceptable when it is small relative to model inference time | Blocks latency-sensitive adoption; drives the strict budget in [`non-functional-requirements.md`](non-functional-requirements.md) |
| A-6 | Organizations will accept prompt content passing through a vendor-operated system, given adequate controls | Forces self-hosted-first strategy and metadata-only default retention |
| A-7 | Regulatory pressure on AI record-keeping increases rather than recedes | Weakens the compliance-driven urgency argument |

---

## 11. Future considerations

- **Problem scope may extend to agentic workloads.** Multi-step autonomous agents
  amplify every problem here — spend becomes non-linear, egress becomes harder to
  predict, and audit trails must capture chains rather than single calls. This is
  anticipated but deliberately excluded from initial scope.
- **Model hosting may move on-premises.** If self-hosted open-weight models become
  standard for regulated workloads, "provider" must generalize to include internal
  inference endpoints. The domain model should not assume providers are external.
- **Procurement may consolidate.** If AI governance becomes a checkbox inside larger
  cloud or security platforms, the standalone category compresses. Differentiation
  then rests on neutrality and depth.
- **The problem may bifurcate.** Developer-facing gateway concerns and
  employee-facing chat concerns could diverge into separate purchases. The
  architecture keeps them separable — see [`vision.md`](vision.md) §5.

---

## 12. Cross references

| Document | Relationship |
| --- | --- |
| [`vision.md`](vision.md) | The end state that resolves these problems |
| [`mission.md`](mission.md) | How the organization pursues that end state |
| [`target-users.md`](target-users.md) | Which organizations experience these problems most acutely |
| [`user-personas.md`](user-personas.md) | Individual experience of each problem |
| [`business-goals.md`](business-goals.md) | Commercial outcomes from solving these problems |
| [`mvp-features.md`](mvp-features.md) | Which problems the first release addresses |
| [`product-requirements.md`](product-requirements.md) | Functional requirements traced to these problems |
| [`competitor-analysis.md`](competitor-analysis.md) | How others address the same problems |
| [`glossary.md`](glossary.md) | Definitions of terms used above |
