# Future Roadmap

| Field | Value |
| --- | --- |
| Document | Future Roadmap |
| Version | 1.0 |
| Status | Draft — directional; not a commitment |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Leadership, Sales |

---

## 1. Purpose

This document sequences everything excluded from the MVP: what comes next, in what
order, and on what condition.

**What this document is not.** It is not a schedule, and no date here should be
communicated externally. Release contents are directional and are re-evaluated
quarterly against evidence. A roadmap treated as a commitment becomes a constraint on
learning, which is the opposite of its purpose.

---

## 2. Overview

The roadmap is organized by the three horizons in [`vision.md`](vision.md) §8, with
named releases inside each. The sequencing logic is consistent throughout:

1. **Coverage before depth.** Capabilities that increase the share of an
   organization's AI traffic passing through the platform outrank capabilities that
   make existing traffic more useful. This follows directly from goal G2 in
   [`business-goals.md`](business-goals.md).
2. **Segment gates.** Regulated enterprises (segment 3.2) require a specific set of
   capabilities. Those capabilities are grouped so that segment entry is a single
   deliberate decision rather than a gradual drift.
3. **Foundations before features.** Anything that changes the shape of stored data is
   scheduled before things that depend on it.

---

## 3. Horizon 1 — Foundation (year 1)

Goal: a governed gateway with a working ledger, adopted by the primary segment.

### v1.0 — General Availability

Scope is defined in [`mvp-features.md`](mvp-features.md). Not repeated here.

---

### v1.1 — Depth and Developer Experience

**Theme.** Remove the friction the MVP left in place, and close the gaps design
partners will have reported.

| Capability | Requirements | Rationale |
| --- | --- | --- |
| Azure OpenAI provider | FR-PROV-003, FR-PROV-017 | Most-requested fourth provider; unblocks Microsoft-centric organizations |
| Embeddings and multimodal | FR-GW-019, FR-GW-020 | Common workloads currently forced to bypass the Gateway — direct coverage impact |
| Request cancellation | FR-GW-025 | Cost control for long-running requests |
| Native Gateway interface | FR-GW-005 | Provider-neutral surface for new integrations; compatibility mode remains for migration |
| PII detection | FR-GOV-006, FR-GOV-007 | Held from MVP until accuracy characteristics can be published |
| Legal-hold process | FR-GOV-011 | Required before content retention is usable at scale |
| Audit tamper-evidence | FR-AUD-008 | Recurring security-review request |
| Audit streaming and export API | FR-AUD-009, FR-API-009 | Integration with customer security tooling |
| Data-flow reporting | FR-AUD-012 | Directly answers the security questionnaire that blocks deals |
| Webhooks | FR-API-013 | Enables customer automation |
| Client libraries (TypeScript, Python) | FR-API-015 | Reduces integration time |
| Organizational management API | FR-API-008 | Automation of Employee and Team lifecycle |
| Cost forecasting | FR-COST-011 | Highest-value analytics gap for P-05 |
| Anomaly detection | FR-ANL-009 | Detects cost regressions before month-end |
| Attribution tags | FR-USG-011 | Cost per product, feature, or end customer |
| Team nesting and reorganization history | FR-TEN-010, FR-TEN-015 | Attribution survives organizational change |
| Chat: attachments, templates, sharing, feedback | FR-CHAT-009 – 011, 016, 017 | Closes the gap against consumer AI products |
| Extension: diff application, workspace config, admin disable | FR-EXT-011 – 013 | Developer workflow depth |
| Slack and Teams notifications | FR-NOT-007 | Alerts where teams already work |
| Service identities | FR-AUTH-019 | Automated workloads without a human owner |
| Scheduled reports, saved views | FR-ANL-010, FR-ANL-011 | Finance and leadership reporting cadence |
| Provider-scoped connections | FR-PROV-013 | Environment and business-unit separation |
| Comparative model performance | FR-ANL-012 | First step toward P-08's requirements |
| Invoice billing with purchase orders | FR-BILL-011 | Unblocks annual contracts |

**Entry condition.** MVP success criteria S-1 through S-11 met.

**The one to watch.** PII detection (FR-GOV-006) is the largest single item and the
one most likely to slip. FR-GOV-007 requires publishing its detection and
false-positive characteristics, which requires a measurement programme, not just an
implementation. If it is not ready, it ships in v1.2 rather than shipping unmeasured.

---

### v1.2 — Enterprise Readiness

**Theme.** The capabilities that gate segment 3.2. This release is the decision point
for entering the regulated-enterprise market.

| Capability | Requirements | Rationale |
| --- | --- | --- |
| SAML 2.0 SSO | FR-AUTH-015 | Non-negotiable for enterprise IT (P-07) |
| SCIM 2.0 provisioning | FR-AUTH-016 | Automated lifecycle; access-review requirement |
| Directory group → Team/role mapping | FR-AUTH-017 | Makes SCIM useful rather than merely present |
| Customer-hosted inference endpoints | FR-PROV-015 | Self-hosted open-weight models as a provider type |
| Invoice reconciliation | FR-COST-013 | Closes the finance credibility loop |
| Multi-currency | FR-COST-012 | Required outside USD markets |
| Time and network restrictions | FR-GOV-012 | Common enterprise policy requirement |
| Gateway response caching | FR-GW-022 | Cost reduction at scale |

**Entry condition — a decision, not a milestone.** This release only makes sense if
leadership has committed to segment 3.2 (decision D-3 in
[`business-goals.md`](business-goals.md) §11). If that commitment has not been made,
v1.2 should be replaced with continued depth in the primary segment.

> **The compounding effect.** Entering segment 3.2 pulls the product toward its
> requirements permanently. Contract values are 5–20× larger, so those customers
> exert proportionate influence on the roadmap. This is a strategic choice with a
> long tail, not a release.

---

## 4. Horizon 2 — Consolidation (years 2–3)

Goal: become the system of record for enterprise AI — the layer an organization
cannot remove without a migration project.

### v2.0 — Quality and Optimization

**Theme.** Move from "what did it cost" to "was it worth it." This is where P-08's
requirements are met and where the platform's data becomes genuinely differentiating.

| Capability | Requirements | Rationale |
| --- | --- | --- |
| Prompt management and versioning | New | Prompts become governed assets with history, not scattered strings |
| Evaluation framework | New | Systematic model comparison on real traffic |
| A/B traffic splitting | FR-GW-023 | Comparison without a bespoke harness |
| Quality regression detection | New | Detect provider-side model drift automatically |
| Cost-quality trade-off analysis | New | Combines the ledger with evaluation — a capability few competitors can match |
| Knowledge-source grounding in Chat | FR-CHAT-015 | Chat becomes organizationally aware |
| Approval workflows | FR-GOV-013 | High-risk request governance |
| Custom roles | FR-PERM-006 | Segment 3.2 permission granularity |
| Hardware security keys | FR-AUTH-020 | Phishing-resistant authentication |
| JetBrains extension | FR-EXT-015 | Second IDE, once the pattern is proven |
| Localization | FR-X-008 | Non-English markets |

**Why now and not earlier.** Every item depends on the ledger being complete and
trusted. Evaluation on incomplete usage data produces conclusions that are worse than
none.

---

### v2.1 — Deployment Flexibility

**Theme.** Meet data where it must live.

| Capability | Rationale |
| --- | --- |
| Self-hosted deployment | Removes the primary objection from regulated and sovereign buyers |
| Multi-region hosting with residency selection | Regulatory requirement in several markets |
| Private-cloud deployment | Middle ground between SaaS and fully self-hosted |
| Air-gapped operation | Government and defense; conflicts with continuous catalog updates and needs deliberate design |

**Precondition.** This release depends on constraint C-2 in
[`product-requirements.md`](product-requirements.md) — no dependency that cannot run
in a customer environment — having been honoured throughout Horizon 1. If it has been
violated, this release becomes a re-architecture rather than a packaging exercise.

> **This is the most expensive release in the roadmap.** Self-hosted deployment
> changes the release process, the support model, the upgrade path, and the
> observability story simultaneously. It should be committed with full awareness of
> that, and no earlier than the evidence requires.

---

### v2.2 — Organizational Scale

| Capability | Requirements | Rationale |
| --- | --- | --- |
| Parent-organization hierarchy | FR-TEN-016 | Multi-subsidiary enterprises |
| Cross-Company reporting | New | Group-level visibility for holding companies |
| Departmental billing and chargeback | New | Internal cost recovery, a standard enterprise finance function |
| Delegated administration | New | Large organizations cannot centralize all administration |

---

## 5. Horizon 3 — Expansion (year 4+)

Directional only. These are hypotheses about where the category goes, not plans.

### Agentic workload governance

Multi-step autonomous agents break the assumption that a request is a meaningful unit
of governance. Governing them requires:

- Trace-level metering — a parent identifier grouping the chain of calls produced by
  one user action
- Budget enforcement at trace level, since a single action may generate unpredictable
  cost
- Tool-use governance — an agent calling external tools is a different egress surface
- Audit records that capture the chain, not just the calls

**Preparation required now.** The Usage Record structure should accommodate a parent
trace identifier without restructuring. This is a Phase 2 design consideration, not a
Horizon 3 one — adding it later means every historical record lacks it.

### Compliance evidence generation

If regulators and auditors come to accept machine-readable evidence, the platform's
audit trail becomes a compliance artifact in its own right: generated evidence
packages mapped to specific control frameworks, continuous compliance monitoring, and
regulator-facing reporting. This would be a substantial expansion of value, and it
depends on external developments rather than on our engineering.

### Intelligent routing

With enough observed cost, latency, and quality data, routing decisions can be made
automatically rather than configured: select the cheapest model meeting a quality
threshold, adapt to observed provider degradation, optimize continuously against a
stated objective. This is the natural endpoint of combining the ledger with the
evaluation framework, and it is difficult for competitors lacking both.

### Control-plane expansion beyond inference

Vector stores, embedding pipelines, fine-tuning jobs, and evaluation harnesses share
the same governance gap that inference had. Expansion is plausible — but must not
violate the boundaries in [`vision.md`](vision.md) §6.

---

## 6. Deliberately unscheduled

Items with genuine demand that are not on the roadmap, with the reason recorded so the
question does not recur:

| Item | Reason |
| --- | --- |
| Mobile applications | Browser-based Chat is sufficient; native apps are a large ongoing commitment for marginal gain |
| Model hosting | Violates neutrality (Pillar 1) — permanent exclusion |
| Fine-tuning services | Adjacent to model provision; risks the same neutrality problem |
| General API gateway capability | Dilutes AI-specific metering and failure semantics |
| Marketplace / plugin ecosystem | Requires an ecosystem that does not exist; premature |
| Data warehouse | Export to the systems that own analysis rather than becoming one |
| Consumer or individual plans | Different product, different economics |
| Professional services offering | Signals a product that cannot be adopted unaided |

---

## 7. Roadmap decision gates

Each gate is a question with an owner and a deadline. Passing a gate commits
significant capacity; failing one should redirect it.

| Gate | Question | Decides | Deadline | Owner |
| --- | --- | --- | --- | --- |
| **GATE-1** | Did MVP hypothesis 1 hold — do developers route production traffic? | Whether the product continues in current form | GA + 90 days | Leadership |
| **GATE-2** | Did MVP hypothesis 2 hold — did non-technical adoption reach target? | Whether Chat receives continued investment or the product narrows to developer-only | GA + 90 days | Product |
| **GATE-3** | Do we enter segment 3.2? | Whether v1.2 proceeds as scoped | GA + 120 days | Leadership |
| **GATE-4** | Is self-hosted deployment committed? | Whether v2.1 exists; constrains all architecture | GA + 180 days | Leadership & Engineering |
| **GATE-5** | Has PII detection reached publishable accuracy? | Whether FR-GOV-006 ships in v1.1 or v1.2 | v1.1 planning | Engineering |
| **GATE-6** | Is the evaluation framework differentiating or table stakes? | Whether v2.0 leads with it or follows | v2.0 planning | Product |

---

## 8. Prioritization framework

When a new request arrives, it is scored before it is scheduled:

| Criterion | Weight | Question |
| --- | --- | --- |
| Coverage impact | 3× | Does this increase the share of AI traffic passing through the platform? |
| Segment gate | 3× | Does this unblock a segment we have committed to? |
| Retention impact | 2× | Does this deepen dependency for existing customers? |
| Persona centrality | 2× | Does it serve a primary persona? |
| Foundational | 2× | Does something else depend on it, or does it change stored data shape? |
| Competitive parity | 1× | Do we lose deals without it? |
| Effort | ÷ | Engineering estimate |

**Overrides.** Two conditions bypass scoring entirely: a security defect, and any
violation of a constraint in [`product-requirements.md`](product-requirements.md) §9.

---

## 9. Assumptions

| # | Assumption | Impact if wrong |
| --- | --- | --- |
| A-1 | The MVP validates its hypotheses, permitting Horizon 1 to proceed as planned | Roadmap restarts from a revised problem statement |
| A-2 | Segment 3.2 remains an attractive expansion | v1.2 and v2.1 lose their rationale; roadmap stays in the primary segment |
| A-3 | Constraint C-2 is honoured through Horizon 1 | v2.1 becomes a re-architecture rather than packaging |
| A-4 | Multi-provider usage persists | Neutrality-dependent items lose value; governance and cost items survive |
| A-5 | Agentic workloads become significant within 3–4 years | Horizon 3 preparation is wasted, though the cost is small |
| A-6 | Evaluation and quality measurement are durably differentiating | v2.0 becomes parity work rather than differentiation |
| A-7 | Regulatory direction continues toward AI record-keeping | Compliance-driven items lose urgency |

---

## 10. Future considerations

- **The roadmap will be pulled by the largest customer.** Once segment 3.2 is entered,
  its requirements dominate by contract value. The primary-segment product can quietly
  stagnate. This should be monitored explicitly — for example by tracking the share of
  roadmap capacity serving each segment.
- **Horizon 3 depends on external developments.** Compliance evidence generation
  requires regulators to accept machine-readable evidence. Agentic governance requires
  agents to become common. Neither is under our control, and both should be monitored
  rather than assumed.
- **Neutrality will be tested commercially.** As routing volume grows, providers will
  offer incentives for preferential treatment. The answer is recorded in
  [`mission.md`](mission.md) §7 before the offer arrives.
- **Some Horizon 2 items may need to move forward.** Evaluation and quality
  measurement are the most likely to be pulled earlier if competitors establish them
  as table stakes. GATE-6 exists for this.
- **The roadmap assumes a single product.** If developer-facing and employee-facing
  needs diverge sufficiently, two products sharing a control plane may become the
  better structure. That would be a significant strategic change and should be a
  deliberate decision, not a drift.

---

## 11. Cross references

| Document | Relationship |
| --- | --- |
| [`mvp-features.md`](mvp-features.md) | v1.0 scope and what was excluded |
| [`product-requirements.md`](product-requirements.md) | Requirement definitions referenced throughout |
| [`vision.md`](vision.md) | Horizon definitions |
| [`business-goals.md`](business-goals.md) | Targets and blocking decisions |
| [`target-users.md`](target-users.md) | Segment definitions behind the gates |
| [`user-personas.md`](user-personas.md) | Personas served by each release |
| [`competitor-analysis.md`](competitor-analysis.md) | Competitive pressure on sequencing |
| [`non-functional-requirements.md`](non-functional-requirements.md) | Quality attributes at scale |
