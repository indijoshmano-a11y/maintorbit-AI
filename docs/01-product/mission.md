# Mission

| Field | Value |
| --- | --- |
| Document | Mission |
| Version | 1.0 |
| Status | Draft — pending review |
| Owner | Product |
| Last updated | 2026-07-30 |
| Audience | Engineering, Product, Design, Leadership |

---

## 1. Purpose

The vision states where MaintOrbit AI is going. This document states how the team
operates while getting there: the mission, the operating principles that resolve
day-to-day trade-offs, and the commitments made to users.

Its practical function is to settle arguments without escalation. When two reasonable
engineers disagree about a design, the principles below should determine the answer.

---

## 2. Overview

A mission that cannot be violated is not a mission — it is decoration. The statement
and principles here are written so that they *can* be traded against, and so that
choosing against them is visible when it happens.

---

## 3. Mission statement

> **We give enterprises complete control over their AI usage — every provider, every
> request, every dollar — without asking anyone to work more slowly.**

Three clauses, each doing work:

- **Complete control.** Partial coverage is close to worthless. A governance layer
  that sees 70% of traffic cannot answer any question with confidence. We optimize
  for total coverage over feature depth.
- **Every provider, every request, every dollar.** The three units of account:
  provider connections, gateway requests, and cost records. If it is not attributable
  to all three, it is not governed.
- **Without asking anyone to work more slowly.** The binding constraint. Any feature
  that makes the governed path slower or more awkward than the ungoverned path is a
  net negative, however good the governance it adds.

---

## 4. Operating principles

Ordered. Where two principles conflict, the earlier one wins.

### 4.1 The governed path is the fastest path

Developers route around friction. The correct response is not policy enforcement but
better ergonomics: the platform must be genuinely easier than calling a provider
directly — one credential instead of many, automatic failover, streaming that works,
cost visibility for free.

*In practice:* every developer-facing feature is measured against the alternative of
using the provider SDK directly. If ours is worse, it ships when it is better.

### 4.2 Neutrality is not negotiable

We never rank providers by anything other than the customer's own configured policy
and observed performance. No commercial relationship influences routing. This is
permanent — see [`vision.md`](vision.md) §5.

*In practice:* routing logic contains no provider-specific preference that is not
derived from customer configuration or measured behavior. This is a code-review gate,
not a guideline.

### 4.3 Observe before you enforce

Every control ships in observe mode first, showing what it *would* have done. Only
after a customer has seen its behavior against real traffic can it be switched to
enforce. Blocking controls that fire unexpectedly destroy trust in the platform far
faster than a missed policy violation does.

*In practice:* every policy, quota, and budget has a monitor mode, and monitor is the
default on creation.

### 4.4 Cost data is a product surface, not a report

Finance is a first-class user. Cost is presented in business terms — team, product,
feature, customer — not only in tokens. Cost data is accurate to a stated tolerance
and freshness, and both are published rather than implied.

*In practice:* the accuracy and latency of cost data are treated as availability
metrics with defined targets, not as best-effort background jobs.

### 4.5 The audit trail is immutable and complete

If it happened, it is recorded. Records are append-only, tamper-evident, and retained
according to a stated schedule. This is the one area where we accept performance and
storage cost to preserve integrity, because an audit trail with gaps is worse than
none — it creates false confidence.

*In practice:* audit writes are not sampled, not best-effort, and not silently
dropped under load. A failure to record is an incident.

### 4.6 Boring where it counts

The gateway sits in the request path of a customer's production systems. It is
infrastructure. It gets conservative engineering: proven dependencies, explicit
timeouts, bounded queues, circuit breakers, graceful degradation. Novelty is spent on
product surfaces, never on the data path.

*In practice:* the gateway's dependency list is reviewed for necessity every release,
and every external call has an explicit timeout and fallback.

### 4.7 Data minimization by default

We store the least data that delivers the value. Metadata — tokens, latency, cost,
model, identity — is retained by default. Prompt and completion content is retained
only when a customer explicitly opts in, per scope, with a configured retention
period. The default is the safe one.

*In practice:* content logging is off on creation, requires an explicit action to
enable, and is itself an audited event.

### 4.8 Design for extraction

Modules communicate through published contracts and events, never by reaching into
each other's internals. This is not architectural fashion; it is what allows the
platform to scale components independently as load grows unevenly, and to satisfy
customers who need certain functions deployed separately.

*In practice:* enforced by architecture tests in
`backend/tests/MaintOrbit.ArchitectureTests`, not by convention.

### 4.9 Ship the complete slice

A feature is done when it exists end to end: API, permission model, audit events,
usage metering, UI, documentation, and tests. Half-delivered features accumulate into
a product that appears complete and behaves inconsistently.

*In practice:* the definition of done includes every layer, and partial work does not
merge to `main`.

---

## 5. What we commit to users

| Audience | Commitment |
| --- | --- |
| **Developers** | One endpoint, one credential, a stable interface. Breaking changes only across versions, announced in advance. Latency overhead held to a published budget. |
| **Platform engineers** | Transparent failure modes. Every routing decision, retry, and fallback is inspectable after the fact. No unexplained behavior in the data path. |
| **Finance** | Cost figures that reconcile against provider invoices within a stated tolerance, with the tolerance and its causes documented. |
| **Security & compliance** | Complete, immutable audit records. Documented data flows. Honest statements about where data goes and how long it is kept — including when the answer is unflattering. |
| **Employees** | Sanctioned AI assistance that is genuinely useful, with clear disclosure of what their organization can see about their usage. |
| **Administrators** | Deprovisioning that actually revokes access, everywhere, immediately. |

The employee commitment is worth stating plainly: usage visibility is a feature for
the organization and a surveillance concern for the individual. We disclose what is
recorded rather than obscuring it, and monitoring capabilities are designed to be
visible to the people being monitored.

---

## 6. How we work

- **Documentation precedes implementation.** Phases 0 and 1 exist because the cost of
  discovering an architectural mistake in code is an order of magnitude higher than
  discovering it in a document.
- **Decisions are recorded.** Anything expensive to reverse becomes an ADR in
  `docs/07-adr/`. "Why is it like this" should have a written answer.
- **Boundaries are tested, not trusted.** Module isolation, layer dependencies, and
  tenant isolation are verified by automated tests. An unenforced boundary is not a
  boundary.
- **Security is a design input.** Threat modeling happens during design, not before
  release. The platform holds customers' provider credentials — a compromise is
  existential, not embarrassing.
- **We are honest about limitations.** Cost estimates carry a stated tolerance,
  availability carries a stated target, and content filtering carries a stated
  failure rate. Overstating capability in a governance product is the fastest way to
  lose the customers who need it most.

---

## 7. Anti-goals

Explicit statements of what we will not do, recorded so that pressure to do them is
recognizable when it arrives:

- **We will not sample the audit trail** for cost or performance reasons.
- **We will not enable content logging by default**, however useful the data would be
  to us.
- **We will not accept provider incentives** that influence routing.
- **We will not ship enforcement without an observation period** available first.
- **We will not use customer prompt or completion content to train models** — ours or
  anyone's.
- **We will not make the ungoverned path more convenient** than the governed one,
  including during onboarding and trials.

---

## 8. Assumptions

| # | Assumption | Consequence if wrong |
| --- | --- | --- |
| A-1 | Ergonomics drive adoption more reliably than mandate | Enforcement-led adoption would justify a different product emphasis |
| A-2 | Customers value observe-before-enforce enough to accept the extra step | Slower time-to-value; may need enforce-by-default for some control types |
| A-3 | Metadata-only default retention is sufficient for most customers' analytics needs | Content logging becomes near-universal, raising the privacy and storage profile |
| A-4 | Cost figures can reconcile to provider invoices within a tolerance customers accept | Undermines Pillar 3; requires direct provider billing integration sooner |
| A-5 | Complete audit capture is affordable at target scale | Forces a tiered retention model rather than uniform completeness |
| A-6 | Employees accept organizational visibility into sanctioned AI usage | Adoption resistance from the end-user population; drives stronger transparency features |

---

## 9. Future considerations

- **Principle 4.5 will be stress-tested at scale.** Complete, immutable audit capture
  is affordable at early volumes and expensive at large ones. The response is tiered
  storage with unchanged completeness, not sampling — and that decision should be
  made deliberately, in advance, as an ADR.
- **Principle 4.7 will conflict with product ambition.** Quality evaluation, prompt
  optimization, and model recommendation all work better with content. The resolution
  is per-scope opt-in with clear value exchange, never a silent default change.
- **The employee commitment may require formalization.** As usage analytics deepen,
  the gap between organizational visibility and individual privacy widens. A published
  transparency standard — what is visible to whom, always — may become necessary.
- **Principle 4.6 constrains hiring and culture.** Conservative data-path engineering
  is a discipline, not a preference. It should be explicit in engineering interviews.
- **Neutrality may require governance.** If commercial pressure on routing becomes
  material, an external audit or published routing-transparency report may be the only
  credible proof.

---

## 10. Cross references

| Document | Relationship |
| --- | --- |
| [`vision.md`](vision.md) | The end state this mission pursues |
| [`problem-statement.md`](problem-statement.md) | The conditions being addressed |
| [`business-goals.md`](business-goals.md) | Measurable expression of mission progress |
| [`non-functional-requirements.md`](non-functional-requirements.md) | Where principles 4.1, 4.5, 4.6, 4.7 become testable targets |
| [`product-requirements.md`](product-requirements.md) | Where principles become requirements |
| [`mvp-features.md`](mvp-features.md) | First application of principle 4.9 |
| [`glossary.md`](glossary.md) | Definitions of terms used above |
