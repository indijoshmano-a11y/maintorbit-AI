# Non-Functional Requirements

| Field | Value |
| --- | --- |
| Document | Non-Functional Requirements |
| Version | 1.0 |
| Status | Draft — targets require validation through prototyping and load testing |
| Owner | Product & Engineering |
| Last updated | 2026-07-30 |
| Audience | Engineering, QA, Security, Operations, Product |

---

## 1. Purpose

This document defines the quality attributes MaintOrbit AI must exhibit: how fast,
how reliable, how secure, how observable, and how maintainable. These are requirements,
not aspirations — each is stated so that it can be measured and failed.

Functional requirements define what the platform does; these define whether it is
usable in production. For a system placed in the request path of a customer's
production traffic, the second matters more.

**Status note.** The numeric targets below are engineering hypotheses derived from the
product constraints in [`product-requirements.md`](product-requirements.md) §9. They
must be validated by prototyping and load testing during Phase 2 and revised where
they prove unachievable or unnecessarily strict. A target that is quietly missed is
worse than one that is deliberately revised.

---

## 2. Overview

### 2.1 Requirement conventions

**Identifier format:** `NFR-<CATEGORY>-<NNN>`. Identifiers are permanent.

**Categories:**

| Prefix | Category | Primary persona |
| --- | --- | --- |
| `NFR-PERF` | Performance and latency | P-03 |
| `NFR-SCAL` | Scalability | P-02 |
| `NFR-AVAIL` | Availability and reliability | P-02 |
| `NFR-DATA` | Data integrity and accuracy | P-05, P-06 |
| `NFR-SEC` | Security | P-06 |
| `NFR-PRIV` | Privacy and data protection | P-04, P-06 |
| `NFR-COMP` | Compliance and auditability | P-06 |
| `NFR-OBS` | Observability and operability | P-02 |
| `NFR-MAINT` | Maintainability and evolvability | Engineering |
| `NFR-USE` | Usability and accessibility | P-04 |
| `NFR-DR` | Backup and disaster recovery | P-02 |
| `NFR-PORT` | Portability and deployment | P-02 |

**Priority** uses the MoSCoW scale defined in
[`product-requirements.md`](product-requirements.md) §4.

### 2.2 The governing constraint

One requirement dominates the others:

> **The Gateway sits in the customer's production request path.**

Everything else follows. It cannot be slow, because slowness compounds into customer
experience. It cannot be unavailable, because unavailability becomes a customer
outage. It cannot fail unpredictably, because unpredictable dependencies get removed.

This is why [`mission.md`](mission.md) §4.6 mandates conservative engineering in the
data path, and why the performance and availability sections below are the strictest
in this document.

---

## 3. Performance and latency (`NFR-PERF`)

**Latency budget definition.** Gateway overhead is the total time attributable to the
platform, measured as end-to-end request duration minus the time spent awaiting the
provider. It includes authentication, authorization, routing, policy evaluation, rate
and budget checks, and response handling. It excludes provider inference time, which
the platform does not control.

| ID | Requirement | Target | Pri | Release |
| --- | --- | --- | --- | --- |
| NFR-PERF-001 | Gateway overhead on a non-streaming request, p50 | ≤ 15 ms | M | MVP |
| NFR-PERF-002 | Gateway overhead on a non-streaming request, p95 | ≤ 50 ms | M | MVP |
| NFR-PERF-003 | Gateway overhead on a non-streaming request, p99 | ≤ 100 ms | M | MVP |
| NFR-PERF-004 | Additional latency before the first streamed token, p95 | ≤ 50 ms | M | MVP |
| NFR-PERF-005 | Per-chunk overhead during streaming, p95 | ≤ 5 ms | M | MVP |
| NFR-PERF-006 | Governance policy evaluation, p95, within the NFR-PERF-002 budget | ≤ 20 ms | M | MVP |
| NFR-PERF-007 | Authentication and authorization decision, p95 | ≤ 10 ms | M | MVP |
| NFR-PERF-008 | Budget and rate-limit check, p95 | ≤ 5 ms | M | MVP |
| NFR-PERF-009 | Web console interactive page load, p95 | ≤ 2.0 s | M | MVP |
| NFR-PERF-010 | Analytics query over a 30-day range, p95 | ≤ 3.0 s | M | MVP |
| NFR-PERF-011 | Analytics query over a 12-month range, p95 | ≤ 10.0 s | S | MVP |
| NFR-PERF-012 | AI Chat time to first token, p95, excluding provider time | ≤ 200 ms | M | MVP |
| NFR-PERF-013 | Usage data query freshness — the lag between a request and its appearance in analytics | ≤ 60 s | M | MVP |
| NFR-PERF-014 | Cost data freshness | ≤ 5 min | M | MVP |
| NFR-PERF-015 | Audit event availability for search after the recorded action | ≤ 30 s | M | MVP |
| NFR-PERF-016 | Public API response time, excluding analytics endpoints, p95 | ≤ 300 ms | M | MVP |
| NFR-PERF-017 | Performance targets must hold under the sustained load defined in NFR-SCAL-002 | — | M | MVP |
| NFR-PERF-018 | Gateway overhead must be measured continuously in production and exposed to customers | — | M | MVP |

> **NFR-PERF-018 exists because of the P-03 persona.** Developers will not trust a
> published latency claim they cannot verify. Publishing measured overhead is both a
> trust mechanism and an internal forcing function — a target that customers can see
> does not quietly regress.

---

## 4. Scalability (`NFR-SCAL`)

| ID | Requirement | Target | Pri | Release |
| --- | --- | --- | --- | --- |
| NFR-SCAL-001 | Concurrent Companies supported on a single deployment | ≥ 500 | M | MVP |
| NFR-SCAL-002 | Sustained Gateway throughput | ≥ 500 requests/second | M | MVP |
| NFR-SCAL-003 | Peak Gateway throughput for at least 5 minutes | ≥ 2,000 requests/second | M | MVP |
| NFR-SCAL-004 | Concurrent streaming connections | ≥ 10,000 | M | MVP |
| NFR-SCAL-005 | Employees per Company | ≥ 10,000 | M | MVP |
| NFR-SCAL-006 | Teams per Company | ≥ 1,000 | S | MVP |
| NFR-SCAL-007 | Usage Records retained and queryable per Company | ≥ 500 million | M | MVP |
| NFR-SCAL-008 | Analytics query performance must not degrade beyond NFR-PERF-010 as record volume grows to NFR-SCAL-007 | — | M | MVP |
| NFR-SCAL-009 | The Gateway must scale horizontally without shared session state | — | M | MVP |
| NFR-SCAL-010 | A single Company's load must not degrade service for other Companies | — | M | MVP |
| NFR-SCAL-011 | Background processing must scale independently of request handling | — | M | MVP |
| NFR-SCAL-012 | The platform must apply backpressure rather than failing unpredictably when capacity is exceeded | — | M | MVP |
| NFR-SCAL-013 | Adding Gateway capacity must not require a service interruption | — | M | MVP |

> **NFR-SCAL-010 is the noisy-neighbour requirement** and it is a security property as
> much as a performance one. Per-Company resource limits must be enforced in the
> request path, not discovered during an incident.

---

## 5. Availability and reliability (`NFR-AVAIL`)

| ID | Requirement | Target | Pri | Release |
| --- | --- | --- | --- | --- |
| NFR-AVAIL-001 | Gateway monthly availability | ≥ 99.9% | M | MVP |
| NFR-AVAIL-002 | Gateway monthly availability | ≥ 99.95% | M | v1.2 |
| NFR-AVAIL-003 | Web console monthly availability | ≥ 99.5% | M | MVP |
| NFR-AVAIL-004 | AI Chat monthly availability | ≥ 99.5% | M | MVP |
| NFR-AVAIL-005 | Public API monthly availability | ≥ 99.9% | M | MVP |
| NFR-AVAIL-006 | Planned maintenance must not require Gateway downtime | — | M | MVP |
| NFR-AVAIL-007 | Failure of the metering, analytics, or notification subsystem must not fail a Gateway request | — | M | MVP |
| NFR-AVAIL-008 | Failure of authentication, authorization, budget, or governance enforcement must fail the request closed | — | M | MVP |
| NFR-AVAIL-009 | Every outbound call must have an explicit timeout; no unbounded wait is permitted | — | M | MVP |
| NFR-AVAIL-010 | A single provider's outage must not affect requests routed to other providers | — | M | MVP |
| NFR-AVAIL-011 | Fallback to an alternative provider must complete within the request timeout defined by the customer | — | M | MVP |
| NFR-AVAIL-012 | Degraded operation must be reported to customers through a status surface within 5 minutes of detection | M | MVP |
| NFR-AVAIL-013 | The platform must survive the loss of any single infrastructure node without data loss | — | M | MVP |
| NFR-AVAIL-014 | Deployment must be possible without request loss | — | M | MVP |
| NFR-AVAIL-015 | All failure modes affecting the Gateway must be documented, with observed behaviour stated | — | M | MVP |

> **NFR-AVAIL-007 and NFR-AVAIL-008 together are the fail-open/fail-closed policy**
> introduced in [`product-requirements.md`](product-requirements.md) FR-GW-017 and
> FR-GW-018. Availability subsystems degrade open; security and financial controls
> degrade closed. Every subsystem must be classified into one of the two categories
> during design, and the classification must be covered by tests.
>
> **NFR-AVAIL-015 is a competitive requirement as much as an operational one.** The
> P-02 persona rejects vendors who describe failure behaviour vaguely.

---

## 6. Data integrity and accuracy (`NFR-DATA`)

| ID | Requirement | Target | Pri | Release |
| --- | --- | --- | --- | --- |
| NFR-DATA-001 | Usage Records lost or unattributed | Zero | M | MVP |
| NFR-DATA-002 | Audit events lost | Zero | M | MVP |
| NFR-DATA-003 | Cost figures must reconcile to provider invoices within a published tolerance | ≤ 2% variance | M | MVP |
| NFR-DATA-004 | The causes of any cost variance must be identifiable and reportable | — | M | MVP |
| NFR-DATA-005 | Requests where token counts were estimated rather than provider-reported | ≤ 5% | S | MVP |
| NFR-DATA-006 | Usage and audit records must be immutable once written | — | M | MVP |
| NFR-DATA-007 | Neither usage nor audit recording may be sampled under any load condition | — | M | MVP |
| NFR-DATA-008 | A failure to write a usage or audit record must be detected, alerted, and reconciled | — | M | MVP |
| NFR-DATA-009 | Aggregated figures must be reproducible: the same query over the same period must return the same result | — | M | MVP |
| NFR-DATA-010 | Historical cost must remain accurate after a provider price change | — | M | MVP |
| NFR-DATA-011 | Historical attribution must remain accurate after an organizational change | — | M | v1.1 |
| NFR-DATA-012 | Data exports must be complete and consistent with the interface at the time of export | — | M | MVP |

> **NFR-DATA-001, -002, and -007 are absolute.** They admit no tolerance and no
> degradation under load. They are the requirements that make the platform's core
> claims — complete cost attribution and complete audit — defensible rather than
> approximate. They are also the requirements most likely to come under pressure at
> scale; see §16.

---

## 7. Security (`NFR-SEC`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-SEC-001 | All data in transit must be encrypted using current recommended transport security, with obsolete protocol versions disabled. | M | MVP |
| NFR-SEC-002 | All data at rest must be encrypted. | M | MVP |
| NFR-SEC-003 | Provider credentials must be encrypted with keys distinct from those protecting general application data. | M | MVP |
| NFR-SEC-004 | Provider credentials must not be retrievable in plaintext through any interface, by any role, after creation. | M | MVP |
| NFR-SEC-005 | Provider credentials must not appear in logs, error messages, traces, or diagnostic output under any condition. | M | MVP |
| NFR-SEC-006 | Platform API Key secrets must be stored only as irreversible hashes. | M | MVP |
| NFR-SEC-007 | Tenant isolation must be enforced at the data-access layer such that an application-layer defect cannot cause cross-tenant exposure. | M | MVP |
| NFR-SEC-008 | Tenant isolation must be verified by automated tests executed on every build. | M | MVP |
| NFR-SEC-009 | All input crossing a trust boundary must be validated against an explicit schema. | M | MVP |
| NFR-SEC-010 | The platform must defend against the injection, authentication, access-control, and misconfiguration classes of common web application vulnerability. | M | MVP |
| NFR-SEC-011 | Dependencies must be scanned for known vulnerabilities on every build, and builds must fail on unresolved critical findings. | M | MVP |
| NFR-SEC-012 | Secrets must never be committed to source control; automated scanning must enforce this. | M | MVP |
| NFR-SEC-013 | Administrative access to production must require multi-factor authentication and must be audited. | M | MVP |
| NFR-SEC-014 | The platform must undergo independent penetration testing before general availability, and at least annually thereafter. | M | MVP |
| NFR-SEC-015 | A documented process must exist for receiving and responding to externally reported vulnerabilities. | M | MVP |
| NFR-SEC-016 | Rate limiting must protect authentication endpoints against credential-stuffing and brute-force attempts. | M | MVP |
| NFR-SEC-017 | Session tokens must be invalidated on password change, role change, and administrative termination. | M | MVP |
| NFR-SEC-018 | The platform must apply defined security headers and a restrictive content security policy to all web surfaces. | M | MVP |
| NFR-SEC-019 | Credential encryption keys must be rotatable without customer-visible interruption. | S | v1.1 |
| NFR-SEC-020 | The platform must support customer-managed encryption keys. | C | v2.0 |

> **NFR-SEC-003, -004, and -005 protect the platform's highest-value asset.**
> A compromise of stored provider credentials is existential rather than
> embarrassing — it grants an attacker spend authority and a data-egress channel
> across every customer. These requirements deserve disproportionate design scrutiny
> and should be the subject of a dedicated threat model.

---

## 8. Privacy and data protection (`NFR-PRIV`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-PRIV-001 | Prompt and completion content must not be retained unless a Company explicitly enables retention for a given scope. | M | MVP |
| NFR-PRIV-002 | Content retention must default to disabled on every new Company and every new Team. | M | MVP |
| NFR-PRIV-003 | Prompt and completion content must never be used to train any model. | M | MVP |
| NFR-PRIV-004 | Content must not appear in logs, traces, error reports, or diagnostic output. | M | MVP |
| NFR-PRIV-005 | Audit records must reference content without containing it. | M | MVP |
| NFR-PRIV-006 | Retention periods must be configurable, documented, and enforced by automated deletion. | M | MVP |
| NFR-PRIV-007 | Employees must be able to delete their own conversations, with deletion propagating to all copies within a stated period. | M | MVP |
| NFR-PRIV-008 | The platform must support export of all data held about an identified Employee. | M | MVP |
| NFR-PRIV-009 | The platform must support deletion of all data relating to an identified Employee, excluding records required for audit integrity, which must be pseudonymized instead. | M | MVP |
| NFR-PRIV-010 | The platform must document, per surface, exactly what is recorded and who can see it. | M | MVP |
| NFR-PRIV-011 | Employees must be shown what their Company can observe about their usage. | M | MVP |
| NFR-PRIV-012 | Data transfers to AI providers must be documented, including the provider's own retention terms where published. | M | MVP |
| NFR-PRIV-013 | The platform must support data residency selection. | M | v2.1 |
| NFR-PRIV-014 | Access to retained content must require a separately authorized and audited process. | M | v1.1 |

> **NFR-PRIV-009 contains a deliberate tension.** Complete deletion conflicts with
> audit immutability (NFR-DATA-006). The resolution — pseudonymize rather than delete
> audit records — should be reviewed with legal counsel before implementation, since
> its adequacy depends on jurisdiction.

---

## 9. Compliance and auditability (`NFR-COMP`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-COMP-001 | The platform must maintain sufficient control documentation to support a SOC 2 Type II examination. | M | v1.1 |
| NFR-COMP-002 | Audit records must be retained for a configurable period of at least 12 months by default. | M | MVP |
| NFR-COMP-003 | Audit records must be tamper-evident. | M | v1.1 |
| NFR-COMP-004 | The platform must produce a report of all data processing performed for a Company over a period. | S | v1.1 |
| NFR-COMP-005 | Subprocessors must be documented and customers notified in advance of changes. | M | MVP |
| NFR-COMP-006 | The platform must support customer-initiated data export in a documented, machine-readable format at any time. | M | MVP |
| NFR-COMP-007 | Payment card data must never transit or be stored by the platform. | M | MVP |
| NFR-COMP-008 | The platform must publish accurate statements of its own security and compliance posture, including known gaps. | M | MVP |
| NFR-COMP-009 | The platform must support ISO 27001 certification requirements. | S | v2.0 |
| NFR-COMP-010 | Automated content detection must have its accuracy characteristics measured and published. | M | v1.1 |

---

## 10. Observability and operability (`NFR-OBS`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-OBS-001 | All application logs must be structured and machine-parseable. | M | MVP |
| NFR-OBS-002 | Every request must carry a correlation identifier propagated across all subsystems and returned to the caller. | M | MVP |
| NFR-OBS-003 | The platform must expose metrics covering request rate, error rate, latency distribution, and saturation for every subsystem. | M | MVP |
| NFR-OBS-004 | The platform must expose distributed traces covering the full request path including provider calls. | M | MVP |
| NFR-OBS-005 | Health endpoints must distinguish liveness from readiness and report dependency status. | M | MVP |
| NFR-OBS-006 | Customers must be able to retrieve the complete routing decision record for any request by its correlation identifier. | M | MVP |
| NFR-OBS-007 | Metrics must be exportable in an open format consumable by standard monitoring systems. | M | MVP |
| NFR-OBS-008 | Alerting must exist for every condition that would breach an availability or data-integrity requirement. | M | MVP |
| NFR-OBS-009 | Logs must never contain credentials, tokens, or prompt content. | M | MVP |
| NFR-OBS-010 | The platform must expose per-Company operational metrics without exposing cross-tenant data. | M | MVP |
| NFR-OBS-011 | A public status page must report current and historical availability. | M | MVP |
| NFR-OBS-012 | Runbooks must exist for every alerting condition before it is enabled in production. | M | MVP |

> **NFR-OBS-006 is a differentiating requirement.** The ability to answer "what
> happened to this specific request" completely — target selected, fallbacks
> attempted, retries, policy decisions, latency at each step — is exactly what the
> P-02 persona demands and what most competing products do not fully provide.

---

## 11. Maintainability and evolvability (`NFR-MAINT`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-MAINT-001 | Modules must communicate only through published contracts and events; direct access to another module's internals must be prevented. | M | MVP |
| NFR-MAINT-002 | Layer dependencies and module boundaries must be verified by automated architecture tests on every build. | M | MVP |
| NFR-MAINT-003 | Any module must be extractable into a separately deployable service without changes to other modules' logic. | M | MVP |
| NFR-MAINT-004 | Automated test coverage of domain and application logic | ≥ 80% | M | MVP |
| NFR-MAINT-005 | Every public API must have contract tests. | M | MVP |
| NFR-MAINT-006 | Adding support for a new AI provider must not require changes outside the provider abstraction and its configuration. | M | MVP |
| NFR-MAINT-007 | Database migrations must be forward-only, reversible in effect, and executable without service interruption. | M | MVP |
| NFR-MAINT-008 | Public API changes must be backward-compatible within a version, with a documented deprecation policy and minimum notice period. | M | MVP |
| NFR-MAINT-009 | The build must be reproducible and complete within 15 minutes. | S | MVP |
| NFR-MAINT-010 | A complete local development environment must be startable with a single documented command. | M | MVP |
| NFR-MAINT-011 | Dependencies must be centrally version-managed and reviewed for necessity each release. | M | MVP |
| NFR-MAINT-012 | Every architecturally significant decision must be recorded as an ADR. | M | MVP |

---

## 12. Usability and accessibility (`NFR-USE`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-USE-001 | All web surfaces must meet WCAG 2.1 Level AA. | M | MVP |
| NFR-USE-002 | All interactive functionality must be operable by keyboard alone. | M | MVP |
| NFR-USE-003 | The console must be usable at viewport widths from 360 px upward. | M | MVP |
| NFR-USE-004 | Time from signup to a first successful governed request, median | ≤ 15 min | M | MVP |
| NFR-USE-005 | Migration of an existing provider integration must require changing only the base URL and credential. | M | MVP |
| NFR-USE-006 | Every error message must state what happened, why, and what to do next. | M | MVP |
| NFR-USE-007 | Every destructive action must state precisely what will be lost before confirmation. | M | MVP |
| NFR-USE-008 | Terminology must match [`glossary.md`](glossary.md) exactly across all surfaces, with no synonyms. | M | MVP |
| NFR-USE-009 | AI Chat must be usable without training or documentation. | M | MVP |
| NFR-USE-010 | The console must remain usable while data is loading, showing progressive rather than blocking states. | S | MVP |
| NFR-USE-011 | The platform must support browsers within the two most recent major versions of the major engines. | M | MVP |
| NFR-USE-012 | The interface must support localization. | C | v2.0 |

> **NFR-USE-004 and NFR-USE-005 are the two most commercially significant
> requirements in this document.** They are the direct measures of
> [`mission.md`](mission.md) §4.1, the primary weapon against the internal-build
> alternative described in [`competitor-analysis.md`](competitor-analysis.md) §8, and
> the mechanism by which coverage goal G2.1 is achieved.

---

## 13. Backup and disaster recovery (`NFR-DR`)

| ID | Requirement | Target | Pri | Release |
| --- | --- | --- | --- | --- |
| NFR-DR-001 | Recovery point objective for transactional data | ≤ 5 min | M | MVP |
| NFR-DR-002 | Recovery time objective for the Gateway | ≤ 1 hour | M | MVP |
| NFR-DR-003 | Recovery time objective for the console and analytics | ≤ 4 hours | M | MVP |
| NFR-DR-004 | Recovery point objective for usage and audit records | Zero loss | M | MVP |
| NFR-DR-005 | Backups must be encrypted and stored separately from primary storage. | — | M | MVP |
| NFR-DR-006 | Restoration must be tested at least quarterly, with results recorded. | — | M | MVP |
| NFR-DR-007 | A documented, rehearsed disaster recovery plan must exist before general availability. | — | M | MVP |
| NFR-DR-008 | Deleted Companies must be recoverable within the grace period defined by FR-TEN-013. | — | M | MVP |
| NFR-DR-009 | The platform must support recovery into a different region. | — | S | v2.1 |

> **NFR-DR-004 permits no data loss** for usage and audit records, which is stricter
> than NFR-DR-001. This follows from NFR-DATA-001 and -002, and it constrains the
> durability design of the ledger specifically — it cannot be satisfied by the same
> mechanism used for general transactional data.

---

## 14. Portability and deployment (`NFR-PORT`)

| ID | Requirement | Pri | Release |
| --- | --- | --- | --- |
| NFR-PORT-001 | Every component must run in a container. | M | MVP |
| NFR-PORT-002 | No dependency may be introduced that cannot run in a customer-controlled environment. | M | MVP |
| NFR-PORT-003 | All configuration must be supplied by environment, with no environment-specific build artifacts. | M | MVP |
| NFR-PORT-004 | The platform must run on a single host for development and evaluation. | M | MVP |
| NFR-PORT-005 | Deployment must be fully automated and repeatable. | M | MVP |
| NFR-PORT-006 | Rollback to the previous version must be possible without data loss. | M | MVP |
| NFR-PORT-007 | The platform must be deployable in a customer-controlled environment without product modification. | M | v2.1 |
| NFR-PORT-008 | The platform must support air-gapped operation. | C | Later |

> **NFR-PORT-002 is the constraint with the longest reach.** It is inexpensive to
> honour from the start and extremely expensive to retrofit. Every dependency decision
> during Phase 2 is bound by it, and violating it converts v2.1 in
> [`future-roadmap.md`](future-roadmap.md) from a packaging exercise into a
> re-architecture.

---

## 15. Verification

Each category has a defined verification method. A requirement without a verification
method is not a requirement.

| Category | Verification method | Cadence |
| --- | --- | --- |
| Performance | Load testing against defined scenarios; continuous production measurement | Per release; continuous |
| Scalability | Load testing at target and 2× target volume | Per release |
| Availability | Chaos and failure-injection testing; production measurement | Per release; continuous |
| Data integrity | Reconciliation testing; injected write-failure scenarios | Per release; continuous |
| Security | Automated scanning per build; independent penetration test | Per build; annually |
| Privacy | Data-flow review; retention enforcement tests | Per release |
| Compliance | Control documentation review; external examination | Per release; annually |
| Observability | Verified during failure-injection exercises | Per release |
| Maintainability | Architecture tests, coverage gates in CI | Per build |
| Usability | Accessibility audit; measured onboarding sessions | Per release |
| Disaster recovery | Restoration exercise | Quarterly |
| Portability | Clean-environment deployment test | Per release |

**Gating.** NFR-DATA-001, NFR-DATA-002, NFR-DATA-007, and NFR-SEC-007 are release
gates. A failure in any of them blocks release regardless of other results.

---

## 16. Assumptions

| # | Assumption | Impact if wrong |
| --- | --- | --- |
| A-1 | Gateway overhead targets in §3 are achievable with the planned architecture | Core adoption assumption fails; architecture requires revision |
| A-2 | Governance evaluation fits within the NFR-PERF-002 budget | Evaluation becomes asynchronous, weakening enforcement per FR-GOV-015 |
| A-3 | Complete, unsampled usage and audit capture is affordable at NFR-SCAL-007 volume | Direct conflict with efficiency goal G4.5; forces tiered storage design |
| A-4 | Cost variance can be held within the 2% tolerance of NFR-DATA-003 | Published tolerance widens; P-05 credibility weakens |
| A-5 | Provider-reported token counts are available on ≥ 95% of requests | NFR-DATA-005 unachievable; estimation becomes the norm |
| A-6 | 99.9% Gateway availability is achievable on a single-region deployment | Multi-region moves from v2.1 into MVP, significantly expanding scope |
| A-7 | Tenant isolation at the data-access layer does not compromise the performance targets | Isolation strategy requires revision; see open question Q-2 |
| A-8 | 80% test coverage is a meaningful proxy for correctness in this domain | Coverage becomes a target rather than a signal |

---

## 17. Future considerations

- **The completeness requirements will come under cost pressure.** NFR-DATA-007 (no
  sampling) is affordable at MVP volume and expensive at NFR-SCAL-007 volume. The
  correct response is tiered storage with unchanged completeness, never sampling. This
  should be settled as an ADR before it arrives as a budget conversation — the
  conflict between this section and goal G4.5 in
  [`business-goals.md`](business-goals.md) is known and scheduled, not accidental.
- **Latency targets will tighten.** As the market matures, gateway overhead becomes a
  compared metric. Targets set at MVP are a floor, not a ceiling, and NFR-PERF-018
  makes any regression publicly visible.
- **Availability targets will need contractual backing.** Segment 3.2 customers will
  require service level agreements with financial remedies. NFR-AVAIL-002 should be
  achieved and demonstrated before any such commitment is made.
- **Privacy requirements will diverge by jurisdiction.** NFR-PRIV-009's
  pseudonymization approach is a reasonable default but its adequacy is
  jurisdiction-dependent, and multi-region operation will surface conflicting
  obligations.
- **Agentic workloads will invalidate several targets.** Per-request latency budgets
  and per-request metering both assume a request is a meaningful unit. Trace-level
  equivalents will be needed — see [`future-roadmap.md`](future-roadmap.md) §5.
- **NFR-MAINT-004's coverage target is a weak proxy.** Coverage measures execution,
  not correctness. It should be supplemented over time by mutation testing or
  property-based testing in the domain layer, where correctness matters most.

---

## 18. Cross references

| Document | Relationship |
| --- | --- |
| [`product-requirements.md`](product-requirements.md) | Functional requirements these attributes constrain |
| [`mission.md`](mission.md) | Principles §4.5, §4.6, §4.7 realized as testable targets |
| [`mvp-features.md`](mvp-features.md) | §8 success criteria referencing these targets |
| [`business-goals.md`](business-goals.md) | Efficiency goals in tension with §6 |
| [`user-personas.md`](user-personas.md) | P-02 and P-03 requirements driving §3, §5, §10 |
| [`future-roadmap.md`](future-roadmap.md) | Releases where deferred targets apply |
| [`competitor-analysis.md`](competitor-analysis.md) | Competitive significance of §12 |
| `docs/02-architecture/` | Phase 2 — how these targets are met |
| `docs/06-deployment/` | Phase 3 — operational procedures supporting §13 |
