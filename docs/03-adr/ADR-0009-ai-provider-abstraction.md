# ADR-0009 — Abstract AI providers behind a narrow port with opaque pass-through

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0009 |
| **Title** | Abstract AI providers behind a narrow port, with opaque parameter pass-through |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-007, GD-008 |
| **Supersedes** | — |

---

## 1. Context

Provider neutrality is Pillar 1 of the product vision and is described there as
**permanent and non-negotiable**. It is the reason an enterprise trusts a control plane
with its entire AI surface, and it is the one claim cloud-vendor and provider-affiliated
competitors structurally cannot make.

Three providers ship at MVP (FR-PROV-002: OpenAI, Anthropic, Google Gemini), Azure
OpenAI at v1.1 (FR-PROV-003), and customer-hosted OpenAI-compatible endpoints at v1.2
(FR-PROV-015). NFR-MAINT-006 requires that adding a provider touch nothing outside the
abstraction and its configuration.

The complication is that providers genuinely differ — in authentication scheme, request
and response shape, streaming protocol, token reporting, tool-calling semantics, error
taxonomy, and increasingly in capabilities that have no equivalent elsewhere. The P-08
persona (ML lead) explicitly requires access to provider-specific behaviour and states
that an abstraction hiding it would be an abandonment trigger.

## 2. Problem Statement

How can providers be abstracted uniformly enough for routing and failover to work, while
still exposing provider-specific capability that a lowest-common-denominator interface
would destroy?

## 3. Decision

**One narrow port declared in the Application layer, one adapter per provider in the
Infrastructure layer, plus opaque parameter pass-through for anything the port does not
model.**

The port models only what routing, metering, and resilience require:

| Capability | Requirement |
| --- | --- |
| Execute a completion | FR-GW-002 |
| Execute a streaming completion | FR-GW-003 |
| Report token usage — provider-reported, or flagged as estimated | FR-GW-016 |
| Classify errors into the normalized taxonomy | FR-GW-006 |
| Validate credentials | FR-PROV-005 |
| Report health | FR-PROV-006 |
| Pass tool definitions with native fidelity | FR-GW-021 |

**Opaque pass-through**: provider-specific parameters the port does not model are carried
through as an opaque bag and applied by the adapter. This is a deliberate, documented leak
in the abstraction.

**Adapters translate; they never decide.** An adapter absorbs authentication differences,
shape translation, streaming protocol differences, token-reporting location, and error
classification. An adapter must **never** make a routing decision, enforce policy, record
usage, or reach into another module.

**Neutrality is enforced in code review**: routing logic may contain no provider-specific
preference that is not derived from customer configuration or measured behaviour. This is
a gate, not a guideline.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Uniform lowest-common-denominator interface, no pass-through | Only capabilities every provider supports | Cleanest abstraction; **blocks the P-08 persona entirely** and prevents customers using the capabilities they chose a provider for. Neutrality would become "equally limited" rather than "equally supported" |
| Provider-specific interfaces, no common abstraction | Each provider exposed natively | Routing, failover, and uniform metering become impossible. Defeats the product |
| Adopt one provider's API as the universal interface | Everything speaks OpenAI's shape internally | Effectively what the *external* Compatibility Interface does for migration (ADR-0016). Rejected **internally** because it structurally privileges one provider's model of the world, which is a slow erosion of neutrality |
| Use an existing open-source abstraction library | LiteLLM-style multi-provider library | Removes significant work. Rejected: it is a core differentiator, we would inherit its abstraction choices and release cadence, and NFR-PORT-002 plus our error-taxonomy and token-reporting requirements are specific |
| Route to providers through a third-party gateway | Delegate to a commercial gateway | Makes a competitor a hard dependency of our core value proposition |

## 5. Pros

- **Adding a provider is confined to one adapter**, satisfying NFR-MAINT-006.
- **Provider SDK churn is contained** in `Infrastructure`, never reaching the domain
  (ADR-0001).
- **Pass-through preserves provider-specific capability**, keeping the P-08 persona.
- **Routing and failover work uniformly** because the port models exactly what they need.
- **Neutrality is structural**: no provider has a privileged position in the internal
  model.
- The narrow port makes each adapter small enough to reason about and test in isolation.

## 6. Cons

- **The abstraction is not uniform.** A request using pass-through parameters is
  provider-specific and may not fall back to an alternative provider meaningfully — a
  limitation that must be documented rather than hidden.
- **Adapters will accumulate divergence** as providers add capabilities at different
  rates. Each new capability is a decision: model it in the port, or leave it to
  pass-through.
- **Token reporting differs** in availability and location per provider, so FR-GW-016's
  estimation flag will be exercised more than one would like.
- **Error classification is a judgement per provider** and directly determines retry and
  fallback eligibility. A misclassification produces either wasted retries or unnecessary
  failures.
- Building and maintaining adapters is ongoing work with no end state.

## 7. Consequences

- **Retry and fallback eligibility is a property of the normalized error category**, not
  a per-call decision (GD-009). This keeps resilience deterministic and inspectable, which
  is what the P-02 persona requires — but it means error classification correctness is
  load-bearing.
- **Both the normalized error and the original must survive to the caller** (FR-GW-006):
  the normalized form so client code can branch reliably across providers, the original so
  a developer can diagnose what actually happened.
- **Estimated token counts must be flagged and their proportion exposed** (FR-USG-007),
  because cost accuracy (NFR-DATA-003, 2% tolerance) depends on it.
- **"Provider" will need to generalize.** FR-PROV-015 introduces customer-hosted
  endpoints, which are provider-shaped but neither external nor commercial. The port must
  not assume a public, paid, vendor-operated API.
- **Provider-side prompt caching changes cost calculation.** Several providers price
  cached input differently, so token reporting must accommodate distinct token classes
  rather than a simple input/output pair — **this affects Phase 4 schema design directly**.
- **No commercial relationship may influence routing.** This is recorded here so that
  pressure to change it is recognizable when it arrives.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | The abstraction leaks as providers diverge, forcing provider-specific branches into the router | Medium | **High** | Divergence absorbed in adapters; the port stays narrow; pass-through carries the rest; router provider-branching is a review gate |
| R-2 | Error misclassification causes wasted retries or unnecessary failures | Medium | Medium | Classification is per-adapter and unit-tested against recorded provider responses; retry and fallback rates monitored per FR-ANL-003 |
| R-3 | Token estimation exceeds the 5% ceiling of NFR-DATA-005, degrading cost accuracy | High | Medium | Estimated proportion exposed and alerted; provider reporting preferred wherever available |
| R-4 | Embeddings and multimodal (v1.1) do not fit the completion-shaped port | Medium | High | The port may need to become a small family of capability-specific ports rather than one interface |
| R-5 | Pass-through requests silently lose fallback capability | Medium | Medium | Documented behaviour; decision record shows when fallback was skipped and why |
| R-6 | Commercial pressure to favour a provider in routing | Medium | Medium | Recorded as a permanent anti-goal; code review gate; a routing-transparency report may become necessary |

## 9. Future Revisions

Revisit when:

- **Embeddings and multimodal ship (v1.1).** Neither fits a completion-shaped interface
  cleanly. The likely outcome is splitting the port into capability-specific ports —
  an amendment to this ADR, not a supersession.
- **Customer-hosted endpoints ship (v1.2).** The definition of "provider" broadens to
  include non-commercial, non-external endpoints.
- **Provider-side prompt caching becomes material to cost.** Token classes multiply and
  the reporting model must expand.
- **A provider introduces a genuinely non-abstractable capability** that a significant
  customer depends on. The answer is pass-through plus documented fallback limitations,
  not distorting the port.
- **Agentic and tool-execution semantics diverge sharply between providers.** This is the
  most likely source of pressure on the port in the next two years.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.4 provider abstraction; §3.9 error taxonomy |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-007 |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Where adapters are invoked |
| [`ADR-0008-credential-encryption.md`](ADR-0008-credential-encryption.md) | Credentials adapters consume |
| [`ADR-0016-rest-api.md`](ADR-0016-rest-api.md) | The external Compatibility Interface, distinct from this internal port |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Port-and-adapter placement |
| [`../01-product/vision.md`](../01-product/vision.md) | Pillar 1 — neutrality |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-08 requirements |
