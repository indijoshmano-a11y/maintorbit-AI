# Target Users

| Field | Value |
| --- | --- |
| Document | Target Users |
| Version | 1.0 |
| Status | Draft — pending review |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, Sales, Marketing |

---

## 1. Purpose

This document defines which organizations MaintOrbit AI is built for, which roles
inside those organizations it serves, and — equally important — which it does not.
It exists so that requirements, design decisions, and go-to-market efforts point at
the same buyer.

Organizational segments are here. Individual behaviour, goals, and frustrations are
in [`user-personas.md`](user-personas.md).

---

## 2. Overview

MaintOrbit AI is a business-to-business platform sold to organizations, not to
individuals. The purchase is made by engineering or platform leadership, validated by
security, and justified to finance. It is then used daily by two very different
populations — developers who consume the API, and employees who use AI Chat — neither
of whom is the buyer.

That split matters more than any other fact about the user base. Products in this
category commonly fail by serving the buyer's requirements and neglecting the daily
users, producing a platform that is purchased and not adopted. Coverage is the whole
value proposition, and coverage requires the daily users to prefer the governed path.

---

## 3. Organizational profile

### 3.1 Primary segment — mid-market technology organizations

| Attribute | Profile |
| --- | --- |
| Employee count | 200 – 2,000 |
| Engineering headcount | 30 – 300 |
| Monthly AI spend | USD 5,000 – 150,000 |
| AI providers in use | 2 – 4 |
| Teams using AI | 5 – 40 |
| Platform/infrastructure team | Present, typically 3–15 engineers |
| Compliance posture | SOC 2 Type II attained or in progress; ISO 27001 common |
| Deployment preference | Vendor-hosted SaaS acceptable |
| Buying process | Engineering leadership decides; security reviews; finance approves |
| Sales cycle | 30 – 90 days |

**Why this segment first.** It is large enough that the problems in
[`problem-statement.md`](problem-statement.md) are painful and quantified, and small
enough that a single decision-maker can approve without a procurement committee.
Crucially, it usually lacks the platform engineering capacity to build an equivalent
internally — the decisive factor in the build-versus-buy conversation.

**Qualifying signals.** Two or more provider accounts; an unattributed AI line item
in the budget; a security review that has flagged AI data flows; at least one
incident involving a leaked or misused provider key; an executive who has asked "what
are we spending on AI?" and not received a satisfactory answer.

**Disqualifying signals.** A single provider with no intention to add another; AI
spend below roughly USD 2,000/month; no platform or security function; a mandate to
build internally.

---

### 3.2 Secondary segment — regulated enterprises

| Attribute | Profile |
| --- | --- |
| Employee count | 2,000 – 50,000+ |
| Industries | Financial services, healthcare, insurance, government, defense, legal |
| Monthly AI spend | USD 50,000 – 1,000,000+ |
| AI providers in use | 3 – 6, including region-pinned deployments |
| Compliance posture | Sector-specific obligations in addition to SOC 2 / ISO 27001 |
| Deployment preference | Self-hosted or private-cloud, frequently mandatory |
| Buying process | Architecture review board, security assessment, procurement, legal |
| Sales cycle | 6 – 18 months |

**Why second, not first.** Contract values are substantially higher and retention is
excellent, but the requirements — self-hosted deployment, SAML SSO, SCIM
provisioning, data residency guarantees, penetration test reports, completed security
questionnaires, contractual audit rights — are largely post-MVP. Pursuing this segment
before those exist consumes the sales cycle without a product that can close.

**What this segment demands of architecture today.** Even though the segment is not
targeted at MVP, its requirements constrain Phase 2 design decisions: no dependency
that cannot run in a customer environment, tenant isolation strong enough to survive
external assessment, and audit records structured for external examination. Retrofitting
these is prohibitively expensive — see [`vision.md`](vision.md) §5, Pillar 5.

---

### 3.3 Tertiary segment — AI-native product companies

| Attribute | Profile |
| --- | --- |
| Employee count | 20 – 300 |
| Monthly AI spend | USD 20,000 – 500,000 (high spend relative to headcount) |
| AI providers in use | 3 – 6, changing frequently |
| Primary concern | Unit economics and provider arbitrage, not compliance |
| Deployment preference | SaaS, self-service |
| Buying process | Founder or engineering lead decides directly |
| Sales cycle | 1 – 14 days |

**Why interesting.** AI cost is cost of goods sold, so cost attribution is not a
reporting nicety — it determines gross margin. These organizations adopt quickly,
provide unusually rigorous feedback on routing and cost accuracy, and stress the
gateway harder than any other segment.

**Why not primary.** Price sensitivity is high, they are the most likely to build
their own, and they have limited interest in the governance surface that carries most
of the platform's value. Best treated as a self-service motion that hardens the
product for the primary segment.

---

## 4. Anti-segments

Explicitly not targeted. Requirements originating from these groups should be
declined rather than absorbed.

| Segment | Reason |
| --- | --- |
| Individual developers and hobbyists | The product's value is organizational — hierarchy, governance, attribution. None applies to one person. Cheaper direct alternatives exist. |
| Organizations under ~50 employees with a single provider | Problems in [`problem-statement.md`](problem-statement.md) are not yet acute. The platform is overhead. |
| Organizations wanting a model, not a gateway | Out of scope permanently. See [`vision.md`](vision.md) §6. |
| Consumer applications | No organizational identity model, no compliance function, incompatible pricing. |
| Organizations seeking a general-purpose API gateway | AI traffic has distinct metering and failure semantics; generalizing dilutes the product. |
| Academic and research users | Value is governance and cost control; research workloads want neither. |

---

## 5. User roles within a customer organization

Every user belongs to exactly one **Company** (the tenant), may belong to one or more
**Teams**, and holds one or more **Roles**. Definitions are in
[`glossary.md`](glossary.md); the permission model is specified in
[`product-requirements.md`](product-requirements.md) §6.

| Role | Typical job title | Primary surface | Population share |
| --- | --- | --- | --- |
| **Owner** | CTO, VP Engineering, founder | Web console | 1 per Company |
| **Company Admin** | Platform lead, IT manager | Web console | 2 – 5 |
| **Billing Admin** | Finance director, controller, FP&A | Web console (billing, cost) | 1 – 3 |
| **Team Lead** | Engineering manager, department head | Web console (team scope) | 5 – 40 |
| **Developer** | Software engineer, data scientist | Developer API, VS Code extension, console | 20 – 60% of users |
| **Member** | Any employee | AI Chat | 40 – 80% of users |
| **Auditor** | Security analyst, compliance officer, internal audit | Audit log, analytics (read-only) | 1 – 5 |

### 5.1 The two-population reality

The last two rows carry the product's adoption risk.

**Developers** judge the platform against calling a provider SDK directly. They will
adopt only if integration takes minutes, latency overhead is negligible, failover
works, and errors are clear. They are unmoved by governance features and will route
around anything that slows them down. Winning them requires ergonomics, not policy.

**Members** are the larger population and the one currently driving shadow AI usage.
They judge the platform against the consumer AI product they already use personally.
They will adopt only if AI Chat is comparably fast and capable. A sanctioned tool that
is noticeably worse than the unsanctioned alternative does not displace it — it is
simply ignored, and the governance gap persists.

This tension is the central product constraint and is why
[`mission.md`](mission.md) §4.1 is the highest-priority operating principle.

---

## 6. Adoption path within an organization

Observed and expected sequence, which the product must support at each stage:

| Stage | Trigger | Users | Product requirement |
| --- | --- | --- | --- |
| 1. Evaluation | A leader cannot answer a cost or security question | 1 – 2 | Self-service signup, a provider connected and a first governed request inside ten minutes |
| 2. Pilot | One team routes non-critical traffic | 5 – 15 | Team scoping, usage visibility, no production risk |
| 3. Team rollout | Pilot demonstrates cost visibility | 15 – 50 | Budgets, quotas, API key management, VS Code extension |
| 4. Engineering-wide | Platform team mandates the gateway | 50 – 300 | Reliability under load, failover, audit trail, SSO |
| 5. Organization-wide | AI Chat opened to all employees | All employees | Non-technical onboarding, seat-based commercial model, directory sync |
| 6. System of record | Compliance and finance depend on the platform | All employees | Retention policies, exports, evidence packages, contractual guarantees |

The commercial model must not penalize progression between stages — particularly the
step from stage 4 to stage 5, where user count grows several-fold while incremental
value per user falls. Pricing implications are in
[`business-goals.md`](business-goals.md) §7.

---

## 7. Geographic and deployment considerations

| Dimension | MVP | Post-MVP |
| --- | --- | --- |
| Primary markets | North America, Western Europe | Plus APAC, Middle East |
| Interface language | English only | Localization driven by segment 3.2 demand |
| Data residency | Single region, disclosed | Multi-region, customer-selected |
| Deployment | Vendor-hosted multi-tenant | Plus self-hosted and private-cloud |
| Currency | USD | Multi-currency, provider FX handling |

Segment 3.2 cannot be sold to until the post-MVP column is delivered. This should be
reflected in hiring and sales planning, not discovered mid-cycle.

---

## 8. Assumptions

| # | Assumption | Validation method | Risk if wrong |
| --- | --- | --- | --- |
| A-1 | Engineering leadership holds budget authority for this purchase | Discovery interviews | Different persona, different messaging, longer cycle |
| A-2 | Mid-market organizations prefer buying over building | Win/loss analysis | Primary segment collapses toward segment 3.2 |
| A-3 | Members adopt sanctioned chat if quality is comparable | Pilot measurement | Coverage stalls at developer traffic only |
| A-4 | Developers accept a gateway if latency overhead is small | Benchmarks with design partners | Core adoption assumption fails |
| A-5 | 200–2,000 employees is the right size band | Pipeline analysis by segment | Misdirected sales and product investment |
| A-6 | Security review is a gate but not a blocker at MVP maturity | Track review outcomes | Certification requirements pull forward significantly |
| A-7 | Organizations tolerate multi-tenant hosting for non-regulated workloads | Objection tracking | Self-hosted moves from post-MVP to MVP |

---

## 9. Future considerations

- **Segment 3.2 will reshape the product.** Self-hosted deployment, SAML, SCIM, and
  data residency are large investments that primarily serve regulated enterprises.
  The decision of when to commit is the single largest roadmap fork —
  see [`future-roadmap.md`](future-roadmap.md).
- **The Member population may become the majority of value.** If AI Chat adoption
  outpaces developer API adoption, the product's center of gravity shifts from
  gateway to workplace assistant, changing competitors and pricing.
- **A partner channel may be required.** Regulated enterprises frequently buy
  infrastructure through systems integrators. Reaching segment 3.2 at scale may
  depend on channel partnerships rather than direct sales.
- **Departmental buying may emerge.** If individual departments purchase separately,
  the Company-level tenancy model may need a parent-organization construct above it.
  The domain model should not make Company the permanent top of the hierarchy.
- **Sovereign requirements may fragment deployment.** Government and defense buyers
  may require air-gapped operation, which conflicts with continuous provider catalog
  updates. This should be a deliberate decision, not an accidental one.

---

## 10. Cross references

| Document | Relationship |
| --- | --- |
| [`user-personas.md`](user-personas.md) | Individual profiles for the roles in §5 |
| [`problem-statement.md`](problem-statement.md) | Problems these segments experience |
| [`business-goals.md`](business-goals.md) | Commercial targets per segment |
| [`vision.md`](vision.md) | Long-term positioning across segments |
| [`mvp-features.md`](mvp-features.md) | Capabilities delivered for the primary segment |
| [`future-roadmap.md`](future-roadmap.md) | Capabilities required for segment 3.2 |
| [`product-requirements.md`](product-requirements.md) | Permission model for §5 roles |
| [`glossary.md`](glossary.md) | Company, Team, Employee, Role definitions |
