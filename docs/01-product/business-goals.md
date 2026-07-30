# Business Goals

| Field | Value |
| --- | --- |
| Document | Business Goals |
| Version | 1.0 |
| Status | Draft — targets require validation against financial plan |
| Owner | Product & Leadership |
| Last updated | 2026-07-30 |
| Audience | Leadership, Product, Engineering, Sales, Finance |

---

## 1. Purpose

This document states the commercial outcomes MaintOrbit AI is expected to produce,
the measures that indicate progress, and the product decisions those measures drive.

Its function for engineering is prioritization. When capacity is constrained, work
that moves a goal here outranks work that does not.

**Status note.** The numeric targets below are planning assumptions, not commitments.
They are internally consistent and derived from the segment profiles in
[`target-users.md`](target-users.md), but they have not been reconciled against a
financial model or validated with design partners. Every target carries the basis for
its derivation so it can be challenged specifically rather than in general.

---

## 2. Overview

MaintOrbit AI monetizes a control plane, which creates a structural characteristic
worth naming early: the platform's value grows with the proportion of an
organization's AI traffic that passes through it, and partial coverage is worth
disproportionately less than complete coverage. An organization routing 60% of its
traffic cannot answer a compliance question, cannot forecast spend, and cannot switch
providers safely.

Every goal below therefore ultimately serves one objective: **coverage**.

---

## 3. Goal hierarchy

```
                   Become the enterprise AI control plane
                                   │
        ┌──────────────────┬───────┴───────┬──────────────────┐
        │                  │               │                  │
   G1 Adoption      G2 Coverage      G3 Retention      G4 Efficiency
   Win accounts     Capture all      Become             Grow without
                    their traffic    irreplaceable      linear cost
```

Sequenced deliberately. G1 without G2 produces logos without value. G2 without G3
produces churn. G3 without G4 produces an unprofitable business. G4 pursued too early
starves the first three.

---

## 4. G1 — Adoption

**Objective.** Establish MaintOrbit AI in the primary segment as a credible,
purchasable alternative to building internally.

| # | Metric | Baseline | 6 months post-GA | 12 months post-GA | Basis |
| --- | --- | --- | --- | --- | --- |
| G1.1 | Paying Companies | 0 | 25 | 90 | Bottom-up from a 2-rep sales motion at a 30–90 day cycle |
| G1.2 | Design partners in production | 0 | 5 | 12 | Required for reference selling |
| G1.3 | Time to first governed request | — | ≤ 15 min | ≤ 10 min | Direct measure of [`mission.md`](mission.md) §4.1 |
| G1.4 | Trial → paid conversion | — | ≥ 20% | ≥ 30% | Typical for technical infrastructure with self-service entry |
| G1.5 | Security review pass rate | — | ≥ 70% | ≥ 90% | Below 70% indicates a product gap, not a sales gap |

**Product implications.**
- Onboarding is a first-class engineering surface, not a marketing page. G1.3 is a
  product metric with an owner.
- Self-service signup and provider connection must work without sales involvement.
- Security documentation is a product deliverable that gates G1.5.

**Leading indicators.** Signups reaching a connected provider; signups reaching a
first gateway request; median session count in the first week.

---

## 5. G2 — Coverage

**Objective.** Capture the majority of each customer's AI traffic. This is the goal
that most directly determines whether the platform delivers its promised value.

| # | Metric | 6 months | 12 months | Basis |
| --- | --- | --- | --- | --- |
| G2.1 | Median share of customer AI traffic routed | 40% | 75% | Below ~70%, compliance and forecasting claims are not defensible |
| G2.2 | Customers with ≥ 2 provider connections | 50% | 70% | Direct evidence the neutrality pillar delivers value |
| G2.3 | Median active Employees per Company | 25 | 120 | Reflects progression from stage 3 to stage 5 in [`target-users.md`](target-users.md) §6 |
| G2.4 | Non-technical share of active users | 20% | 45% | Tests whether the two-audience strategy works |
| G2.5 | Customers using both Gateway and AI Chat | 30% | 60% | Dual-surface adoption is the retention driver |
| G2.6 | Median monthly gateway requests per Company | 50k | 400k | Volume basis for infrastructure planning |

**Product implications.**
- Migration friction is the primary obstacle to G2.1. SDK compatibility and
  base-URL-only migration are coverage features, not convenience features.
- G2.4 is the clearest test of the P-04 persona hypothesis. If it stalls below 25%,
  AI Chat quality is the problem and should be treated as a coverage emergency.
- G2.5 is the strongest predictor of renewal and should be tracked weekly.

**Risk.** G2.1 is the hardest goal in this document. Traffic already integrated
directly with a provider migrates only when migration is nearly free. This should be
resourced accordingly.

---

## 6. G3 — Retention

**Objective.** Become infrastructure the organization depends on rather than a tool
it evaluates annually.

| # | Metric | 12 months | 24 months | Basis |
| --- | --- | --- | --- | --- |
| G3.1 | Gross revenue retention | ≥ 90% | ≥ 92% | Standard for infrastructure with switching cost |
| G3.2 | Net revenue retention | ≥ 115% | ≥ 130% | Expansion via seats and volume |
| G3.3 | Logo churn (annual) | ≤ 10% | ≤ 8% | Corollary of G3.1 |
| G3.4 | Customers with SSO configured | 40% | 70% | Strong entrenchment signal |
| G3.5 | Customers exporting cost data to finance systems | 25% | 50% | Once finance depends on it, removal requires a finance project |
| G3.6 | Median tenure of first design partner cohort | — | ≥ 24 months | Reference durability |

**Product implications.**
- Entrenchment is earned through dependency, not lock-in. The three dependencies
  worth building are identity integration (G3.4), financial reporting (G3.5), and the
  audit record.
- The audit trail becomes retention infrastructure over time: an organization whose
  compliance evidence lives in the platform cannot leave without a migration plan.
- Data export must be genuinely good. Products that trap data lose the security
  review, and losing it costs more than the churn prevented.

---

## 7. G4 — Efficiency

**Objective.** Grow gross margin as volume grows, so that scale improves rather than
erodes economics.

| # | Metric | 12 months | 24 months | Basis |
| --- | --- | --- | --- | --- |
| G4.1 | Gross margin | ≥ 65% | ≥ 78% | Infrastructure pass-through costs compress early margin |
| G4.2 | Infrastructure cost per 1M gateway requests | Baseline | −45% | Efficiency from batching, tiered storage, caching |
| G4.3 | Support tickets per Company per month | ≤ 4 | ≤ 2 | Product clarity, not support headcount |
| G4.4 | Share of customers onboarded without sales involvement | 30% | 55% | Self-service scalability |
| G4.5 | Storage cost per retained audit record | Baseline | −60% | Tiered retention without sampling |

**Product implications.**
- G4.5 is where [`mission.md`](mission.md) §4.5 becomes commercially difficult.
  Complete audit capture is affordable at low volume and expensive at high volume.
  The answer is tiered storage with unchanged completeness, never sampling — and this
  should be an ADR before it becomes an incident.
- G4.3 is a product-quality metric. Tickets concentrated on one surface indicate a
  design defect, not a documentation gap.

---

## 8. Commercial model

**Status: proposed, not decided.** This is the single largest unresolved product
decision and it must be settled before Phase 2 — see §11.

**Proposed structure — hybrid seats plus volume:**

| Component | Basis | Rationale |
| --- | --- | --- |
| Platform fee | Per Company, per tier | Covers fixed cost of governance surfaces regardless of volume |
| Seat fee | Per active Employee per month | Scales with organizational adoption; aligns to AI Chat value |
| Volume component | Per million gateway requests, tiered | Scales with infrastructure cost; aligns to developer value |
| Governance tier | Add-on for advanced policy, extended retention, self-hosted | Prices the capabilities segment 3.2 requires |

**Design constraint from [`target-users.md`](target-users.md) §6.** The model must not
penalize the transition from stage 4 (engineering-wide) to stage 5
(organization-wide), where user count grows several-fold while incremental value per
user falls. Pure per-seat pricing makes organization-wide rollout unaffordable — which
directly attacks G2.4 and G2.1, the goals the platform's value depends on. Seat
pricing should therefore decline with volume, or Members should be priced below
Developers.

**What must not be priced.**
- **Audit records.** Charging for compliance evidence creates an incentive to retain
  less, which contradicts [`mission.md`](mission.md) §4.5.
- **Provider connections.** Charging per provider penalizes the multi-provider
  behaviour the platform exists to enable, attacking G2.2.
- **Data export.** Charging for exit is a security-review liability.

---

## 9. Non-goals

Explicitly not pursued in the first twenty-four months:

| Non-goal | Reason |
| --- | --- |
| Margin on AI inference | Marking up provider costs destroys the neutrality that makes the platform trustworthy. Provider costs pass through at cost. |
| Consumer or individual plans | Different product, different economics, distraction from segment focus |
| Marketplace revenue | Requires an ecosystem that does not exist yet |
| Professional services revenue | Signals a product that cannot be adopted without help |
| Regulated-enterprise revenue before self-hosted deployment exists | Consumes long sales cycles with a product that cannot close |

---

## 10. Measurement and review

| Cadence | Review | Owner |
| --- | --- | --- |
| Weekly | G1.3, G2.5, G4.3 — fast-moving product-health signals | Product |
| Monthly | Full G1 and G2 scorecard | Product & Sales |
| Quarterly | All goals; targets revised against actuals | Leadership |
| Semi-annual | Commercial model review against G2.4 and G3.2 | Leadership & Finance |

**Instrumentation requirement.** Every metric above requires a data source that
exists in the product. G1.3, G2.1, G2.4, and G2.5 in particular need instrumentation
designed during Phase 2, not retrofitted after launch. Product analytics is a Phase 2
engineering requirement, not a post-launch addition.

---

## 11. Decisions required before Phase 2

| # | Decision | Why it blocks | Owner |
| --- | --- | --- | --- |
| D-1 | Commercial model structure and unit of value | Determines what must be metered, which shapes the Usage and Billing data models | Leadership |
| D-2 | Whether provider costs pass through at cost or with margin | Affects trust positioning and every cost calculation | Leadership |
| D-3 | Whether self-hosted deployment is a 12-month or 24-month commitment | Determines whether segment 3.2 is in the plan and constrains architecture | Leadership & Engineering |
| D-4 | Free tier: exists or not, and its limits | Affects G1.4 and infrastructure cost modelling | Product |
| D-5 | Audit retention included versus priced | Contradiction risk with [`mission.md`](mission.md) §4.5 | Product & Leadership |

---

## 12. Assumptions

| # | Assumption | Risk if wrong |
| --- | --- | --- |
| A-1 | The primary segment will pay for AI governance as a separate line item | Category does not support standalone pricing; forces bundling or a different buyer |
| A-2 | A 30–90 day sales cycle is achievable in the primary segment | G1.1 timelines slip materially |
| A-3 | Coverage above 70% is achievable given existing direct integrations | G2.1 unrealistic; value proposition weakens |
| A-4 | Non-technical adoption reaches 45% within 12 months | G2.4 fails; product collapses to a developer-only tool |
| A-5 | Infrastructure cost per request declines with scale | G4.1 unreachable; margin structure unviable |
| A-6 | Pass-through provider pricing is commercially sustainable | Requires higher platform fees, raising the adoption barrier |
| A-7 | Complete audit retention remains affordable at target volumes | Forces a choice between §4.5 and G4.5 |

---

## 13. Future considerations

- **Coverage measurement requires customer cooperation.** G2.1 needs the denominator —
  total AI traffic including what bypasses the platform — which the platform cannot
  observe directly. Either a customer-reported figure or an inference from provider
  invoice reconciliation is required. This should be designed, not improvised.
- **Net revenue retention may depend on volume growth outside our control.** If G3.2
  is driven mainly by customers' own AI usage growth, expansion revenue is exposed to
  a market trend rather than to product quality. Seat expansion is the more durable
  driver and should be weighted accordingly.
- **The commercial model may need to change after launch.** Pricing changes are
  expensive and damage trust. Deciding D-1 well is worth delaying Phase 2 by weeks.
- **Segment 3.2 will dominate revenue if pursued.** Contract values are 5–20× the
  primary segment. Once entered, product priorities will be pulled toward its
  requirements. This is a strategic choice to make deliberately.
- **Efficiency goals may conflict with mission principles.** G4.5 and
  [`mission.md`](mission.md) §4.5 are in tension at scale. The resolution is
  architectural, and it should be designed before it becomes a budget decision.

---

## 14. Cross references

| Document | Relationship |
| --- | --- |
| [`vision.md`](vision.md) | Long-term outcome these goals advance |
| [`mission.md`](mission.md) | Principles constraining how goals are pursued |
| [`target-users.md`](target-users.md) | Segments underlying the targets |
| [`user-personas.md`](user-personas.md) | Individuals whose behaviour the metrics measure |
| [`mvp-features.md`](mvp-features.md) | Scope required to reach the 6-month targets |
| [`future-roadmap.md`](future-roadmap.md) | Scope required for 12- and 24-month targets |
| [`competitor-analysis.md`](competitor-analysis.md) | Competitive context for pricing |
| [`non-functional-requirements.md`](non-functional-requirements.md) | Performance targets underpinning G2 and G4 |
