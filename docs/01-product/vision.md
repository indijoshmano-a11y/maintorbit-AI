# Product Vision

| Field | Value |
| --- | --- |
| Document | Product Vision |
| Version | 1.0 |
| Status | Draft — pending review |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, Leadership, Investors |

---

## 1. Purpose

This document describes the end state MaintOrbit AI is building toward and the
principles that constrain how it gets there. It exists so that decisions made
independently by different teams over several years converge rather than diverge.

Vision describes *where*. [`mission.md`](mission.md) describes *how*.
[`future-roadmap.md`](future-roadmap.md) describes *when*.

---

## 2. Overview

Every significant category of enterprise infrastructure eventually acquires a control
plane. Compute acquired orchestration. Networking acquired software-defined control.
Identity acquired single sign-on. Data acquired governance catalogs. In each case the
pattern was the same: capability arrived first, was adopted chaotically, and was then
consolidated behind a layer that made it governable without making it slower.

Enterprise AI is in the pre-consolidation phase of that cycle. MaintOrbit AI is
building the layer that ends it.

---

## 3. Vision statement

> **Every AI interaction in the enterprise passes through one governed, observable,
> provider-neutral layer — and no one has to slow down for that to be true.**

The second clause is the difficult one. Control planes fail when they become
tollbooths. A developer must be able to adopt MaintOrbit AI in under ten minutes and
find it faster and more reliable than calling a provider directly. Governance is then
a property the organization receives for free, rather than a tax it imposes.

---

## 4. The ten-year picture

If MaintOrbit AI succeeds, a large organization in 2036 looks like this:

**For the organization.** AI is a line item that can be forecast, attributed, and
optimized like cloud compute. Leadership can answer, in seconds, what AI costs per
team, per product, and per customer. Provider contracts are negotiated from a
position of measured leverage because switching is a configuration change.

**For the security and compliance function.** Every AI request is attributable to an
authenticated identity, subject to policy at the point of egress, and retained
according to a stated schedule. The AI data-flow inventory is generated, not
maintained by hand. Audit readiness is continuous.

**For the developer.** One endpoint, one credential, one interface. Model selection is
policy, not code. Failover, retries, and streaming are handled. Provider migration is
a routing rule. Assistance is present in the editor, backed by the same governed
infrastructure as production traffic.

**For the employee.** Sanctioned, capable AI assistance is available in the browser
and in the tools they already use, with organizational context, without anyone
needing a personal provider account.

**For the market.** "Which AI control plane do you run?" is a normal question in
enterprise architecture review, with the same weight as "which identity provider do
you use." MaintOrbit AI is a credible answer.

---

## 5. Strategic pillars

Five pillars carry the vision. Every roadmap item should advance at least one, and no
item should undermine another.

### Pillar 1 — Neutrality

The platform is never a competitor to the providers it routes to, and never favors
one commercially. Neutrality is the reason an enterprise trusts a control plane with
its entire AI surface. It constrains us permanently: MaintOrbit AI will not train or
sell foundation models, and will not accept placement incentives that bias routing.

### Pillar 2 — Control without friction

Governance that developers route around provides no governance. Every control must be
enforced at a point developers already pass through, and the governed path must be
the most convenient path. Where a control cannot be made frictionless, it is made
observable first and enforcing second.

### Pillar 3 — Financial clarity

Token counts are not a business metric. The platform's job is to translate provider
metering into language finance uses: cost per team, per product, per feature, per
customer, forecast against budget. Cost data is a first-class product surface, not a
report appended to a dashboard.

### Pillar 4 — One platform, two audiences

Developers and non-technical employees have genuinely different needs, and products
that serve only one leave half the organization ungoverned. MaintOrbit AI serves both
from a single identity model, a single policy engine, and a single ledger — which is
what makes complete coverage possible and what makes the platform hard to displace.

### Pillar 5 — Deployable where the data must live

Regulated and sovereign workloads will not send prompt content to a multi-tenant
system operated by a vendor. The architecture must support self-hosted and
region-pinned deployment without forking the product. This constrains engineering
from day one: no dependency on a managed service that cannot be run by a customer.

---

## 6. Boundaries of the vision

Constraints are as load-bearing as ambitions. MaintOrbit AI will not become:

| Not this | Because |
| --- | --- |
| A foundation model provider | Destroys the neutrality that makes the control plane trustworthy (Pillar 1) |
| An application development platform | The layer beneath customer-facing AI features, never the features themselves |
| A general API gateway | AI traffic has distinct metering, governance, and failure semantics; generalizing dilutes all of it |
| A data warehouse or BI product | Usage data is exported to the systems that own analysis, not hoarded |
| A consumer product | Every design decision assumes an organizational buyer with a compliance function |

---

## 7. What must be true for the vision to hold

The vision depends on conditions outside our control. Each is monitored, and each has
a documented response.

| Condition | If it fails |
| --- | --- |
| The provider market stays plural | Neutrality loses value; pivot emphasis to governance, cost control, and audit, which survive consolidation |
| Provider APIs remain abstractable | Per-provider engineering cost rises; narrow supported surface to the highest-value capabilities |
| Enterprises keep buying governance separately from cloud | Category compresses into hyperscaler platforms; differentiate on neutrality and depth of financial tooling |
| Prompt content can pass through a vendor system with controls | Accelerate self-hosted deployment from a roadmap item to a primary distribution model |
| AI spend remains material enough to govern | Value shifts from cost control toward security and compliance |

---

## 8. Vision by horizon

| Horizon | Period | The platform is… |
| --- | --- | --- |
| **H1 — Foundation** | Year 1 | A governed gateway with a working ledger. An organization can route traffic through it, see spend by team, and produce an audit trail. Coverage is the goal; depth is not. |
| **H2 — Consolidation** | Years 2–3 | The system of record for enterprise AI. SSO and directory sync make it the identity boundary; governance policies make it the enforcement point; finance-grade reporting makes it the accounting boundary. Displacing it means re-plumbing the organization. |
| **H3 — Expansion** | Years 4+ | The optimization and assurance layer. The platform recommends model choices from observed cost and quality data, governs agentic workloads, and provides evidence packages that satisfy regulators directly. |

Horizon detail is in [`future-roadmap.md`](future-roadmap.md).

---

## 9. How we will know the vision is being realized

Directional indicators, distinct from the operating targets in
[`business-goals.md`](business-goals.md):

- **Traffic share.** The proportion of a customer's total AI traffic passing through
  the platform, trending toward complete coverage. Partial coverage means partial
  governance, which is worth substantially less.
- **Provider plurality per customer.** Customers routing to two or more providers,
  indicating the neutrality pillar is delivering real switching capability.
- **Audience breadth.** The ratio of non-technical to technical active users,
  indicating Pillar 4 is working rather than the product collapsing into a
  developer-only tool.
- **Time to first governed request.** Measured in minutes, trending down. This is the
  direct measure of Pillar 2.
- **Survival of provider migration.** Customers who change primary providers without
  changing application code — the clearest proof the abstraction holds.

---

## 10. Assumptions

| # | Assumption | Basis | Review |
| --- | --- | --- | --- |
| A-1 | Enterprise AI follows the consolidation pattern of prior infrastructure categories | Historical analogy to orchestration, SDN, IAM, data governance | Annual |
| A-2 | Provider-neutrality remains commercially defensible | No credible neutral incumbent at time of writing | Semi-annual |
| A-3 | A single platform can serve developers and non-technical employees without compromising either | Shared identity, policy, and ledger; separated experience layers | Post-MVP validation |
| A-4 | Self-hosted deployment is achievable without a product fork | Architectural constraint accepted in Phase 0 | Each major release |
| A-5 | Gateway latency overhead can be held small relative to inference time | Inference dominates end-to-end latency | Continuously, against NFR-PERF |
| A-6 | AI governance becomes a standard enterprise architecture concern | Regulatory direction, security review trends | Annual |

---

## 11. Future considerations

- **Agentic workloads change the unit of governance.** When a single user action
  triggers dozens of chained model calls, the meaningful units become the task and
  the trace, not the request. The domain model should avoid assumptions that make
  request-level granularity permanent.
- **"Provider" will generalize.** Self-hosted open-weight models, private endpoints,
  and specialized inference vendors all belong behind the same abstraction. Nothing
  in the design should assume a provider is a public commercial API.
- **Governance may become externally attestable.** If regulators or auditors accept
  machine-readable evidence, the platform's audit trail becomes a compliance artifact
  in its own right — a significant expansion of value.
- **The control plane may extend beyond inference.** Vector stores, embedding
  pipelines, and evaluation harnesses share the same governance gap. Expansion is
  plausible but must not compromise section 6.
- **Neutrality will be tested commercially.** Providers will eventually offer
  incentives for preferential routing. The answer is documented now, before the offer
  arrives: no.

---

## 12. Cross references

| Document | Relationship |
| --- | --- |
| [`problem-statement.md`](problem-statement.md) | The conditions this vision resolves |
| [`mission.md`](mission.md) | Operating approach for pursuing this vision |
| [`business-goals.md`](business-goals.md) | Measurable near-term outcomes |
| [`future-roadmap.md`](future-roadmap.md) | Horizon-by-horizon sequencing |
| [`mvp-features.md`](mvp-features.md) | The first increment toward H1 |
| [`competitor-analysis.md`](competitor-analysis.md) | Competitive context for the pillars |
| [`target-users.md`](target-users.md) | Who the vision serves |
| [`glossary.md`](glossary.md) | Definitions of terms used above |
