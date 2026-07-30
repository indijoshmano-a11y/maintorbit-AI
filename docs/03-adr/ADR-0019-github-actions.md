# ADR-0019 — Use GitHub Actions for CI/CD with build-gating quality checks

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0019 |
| **Title** | Use GitHub Actions for CI/CD, with architecture and security checks as build gates |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | Phase 0 infrastructure selection; AT-1 … AT-12 enforcement |
| **Supersedes** | — |

---

## 1. Context

Several architectural decisions in this set are **only real if a pipeline enforces them**:

| Decision | Depends on CI enforcement |
| --- | --- |
| ADR-0001, ADR-0002 | Layer and module boundaries (AT-1 … AT-12) — otherwise they are conventions |
| ADR-0003 | Nullable and async correctness promoted to errors |
| ADR-0005 | Tenant isolation verified by test on every build (NFR-SEC-008) |
| ADR-0008 | Secret scanning (NFR-SEC-012) |
| ADR-0010 | Latency regression gate (NFR-PERF-018) |
| ADR-0016 | Contract tests and specification sync (NFR-MAINT-005) |
| ADR-0017, ADR-0008 | Portable implementations exercised, not just cloud ones |

Phase 0 selected GitHub Actions. The code is hosted on GitHub, and the repository already
contains `.github/workflows/`.

## 2. Problem Statement

What executes the build, test, and deployment pipeline, and which checks must gate a
merge rather than merely report?

## 3. Decision

**GitHub Actions, with a defined set of build-gating checks.**

**Gating — a failure blocks merge or deployment:**

| Gate | Enforces |
| --- | --- |
| Build with warnings as errors | ADR-0003 nullable and async correctness |
| Unit and integration tests | Correctness |
| **Architecture tests AT-1 … AT-12** | ADR-0001 layering, ADR-0002 module boundaries |
| **Tenant isolation tests** | NFR-SEC-008 — verified on every build |
| **Secret scanning** | NFR-SEC-012 |
| **Dependency vulnerability scan** | NFR-SEC-011 — build fails on unresolved critical findings |
| **Portable-implementation smoke test** | NFR-PORT-002 — the single-host topology starts and works |
| Contract tests | NFR-MAINT-005 |
| Frontend bundle size budget | NFR-PERF-009 |
| Accessibility audit | NFR-USE-001 |

**Reporting — recorded, alerted, not blocking:**

- Coverage trend against the 80% target (NFR-MAINT-004)
- Latency benchmark trend (NFR-PERF-018) — see §7 for why this is reporting at MVP
- Image size trend

**Pipeline structure:**

```
pull request  →  build · test · architecture · security · portable smoke
     merge     →  build immutable images · publish
   promote     →  migration runner · rolling deployment · health gate
```

Images are **built once and promoted** (ADR-0018 rule 1), never rebuilt per environment.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Azure DevOps Pipelines | Natural pairing with Azure VM hosting | Splits tooling across two platforms when the code already lives on GitHub. No decisive capability advantage |
| Self-hosted runner platform | Jenkins, TeamCity, Drone | Full control, no per-minute cost. Rejected: infrastructure to operate for a small team, with no benefit at this stage |
| GitLab CI | Strong integrated pipeline | Would require moving source hosting |
| No pipeline — manual build and deploy | Fastest to start | The architecture depends on gating checks; without them, ADR-0001 and ADR-0002 are aspirations |

## 5. Pros

- **Co-located with the source**, so pull-request gating is native rather than integrated.
- **Gating checks make architectural decisions enforceable** rather than advisory — this
  is the primary value, not convenience.
- **Immutable promotion** means the tested artifact is the deployed artifact.
- Composite actions allow shared pipeline logic without duplication across workflows.
- Broad ecosystem for the scanners and tools the gates require.

## 6. Cons

- **Vendor coupling.** Workflow definitions are GitHub-specific and would need rewriting
  elsewhere.
- **Hosted runner cost** grows with build frequency and duration.
- **Secrets live in GitHub's secret store**, adding a system to the trust boundary for
  deployment credentials — though not for Provider Credentials, which never appear in
  configuration (ADR-0008).
- **Build time is a delivery constraint.** NFR-MAINT-009 targets 15 minutes; a growing
  gate set works against that.
- Self-hosted customers cannot use our pipeline, so their upgrade path is separate.

## 7. Consequences

- **Architecture tests must run on every build, not nightly.** A boundary violation caught
  a day later has already been built upon.
- **The portable-implementation smoke test is what keeps NFR-PORT-002 true.** Without it,
  the portable key custodian (ADR-0008) and portable object storage (ADR-0017) rot
  silently and v2.1 becomes a re-architecture. This gate is doing more work than it
  appears to.
- **The latency benchmark is reporting-only at MVP and must become gating.** Benchmark
  noise on shared runners makes it unreliable as a gate initially, but NFR-PERF-018
  requires published, continuously measured overhead — and a target nobody enforces
  regresses. Promoting this to a gate requires a stable measurement environment and is an
  open item.
- **Build time must be actively managed.** Every gate added spends part of the 15-minute
  budget; parallelization and caching are ongoing work.
- **Deployment credentials require least-privilege scoping** and rotation.
- **Self-hosted customer upgrades need their own documented path**, separate from this
  pipeline (v2.1).

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Gates are disabled or made non-blocking under delivery pressure | **High** | **High** | Disabling a gate requires architecture review and a recorded reason; gate status visible in the pull request |
| R-2 | Build time grows past NFR-MAINT-009, encouraging gate removal | Medium | High | Parallelization, caching, and selective execution by changed path |
| R-3 | Latency benchmark never becomes a gate, so NFR-PERF-018 regressions ship | High | Medium | Stable measurement environment is an explicit open item, not an aspiration |
| R-4 | Deployment credentials compromised via the pipeline | High | Low | Least-privilege scoping; rotation; environment protection rules |
| R-5 | Flaky tests erode trust in the gates | Medium | High | Flaky tests are defects, quarantined and fixed, never retried into passing |
| R-6 | Portable smoke test is weakened to a build check rather than a functional one | Medium | Medium | It must start the single-host topology and exercise a real path, not just build images |

## 9. Future Revisions

Revisit when:

- **The latency benchmark can be made reliable enough to gate.** This requires dedicated
  measurement infrastructure and is the most valuable pending improvement to the pipeline.
- **Build time consistently exceeds the target** despite optimization — self-hosted
  runners become worth their operational cost.
- **Deployment orchestration changes** (ADR-0018 §9). Kubernetes would change the
  promotion and rollout stages substantially.
- **Self-hosted customer upgrades ship (v2.1).** Release artifact publication and upgrade
  tooling become pipeline responsibilities.
- **Compliance requires attested builds.** SOC 2 (NFR-COMP-001) may drive provenance and
  supply-chain attestation requirements.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.7 deployment process |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §8 the architecture tests this enforces |
| [`ADR-0018-docker.md`](ADR-0018-docker.md) | Images this builds and promotes |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Layer rules enforced here |
| [`ADR-0002-modular-monolith.md`](ADR-0002-modular-monolith.md) | Module boundaries enforced here |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Latency regression gate |
| [`ADR-0017-object-storage.md`](ADR-0017-object-storage.md) | Portable implementation smoke test |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SEC-008/011/012, NFR-MAINT-004/005/009, NFR-PORT-002 |
| `../../.github/workflows/` | Workflow definitions |
