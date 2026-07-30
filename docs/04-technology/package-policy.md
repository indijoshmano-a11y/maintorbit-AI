# Package Policy

| Field | Value |
| --- | --- |
| Document | Package Policy |
| Version | 1.0 |
| Status | Draft — pending engineering review |
| Owner | Engineering |
| Last updated | 2026-07-30 |
| Audience | Engineering |
| Phase | 4 — Technology Standards |

---

## 1. Purpose

[`dependency-policy.md`](dependency-policy.md) governs *which* packages exist. This
document governs *how they are managed*: where versions are declared, how they are
pinned, how the tree is reproduced, and how packages are consumed and published.

Its aim is that the version resolved on a developer's machine, in CI, and in production
is provably the same version — because a build that cannot be reproduced cannot be
audited, and an artifact that differs from the one tested is not the one that was tested.

---

## 2. Scope

**In scope:** version declaration, pinning, lockfiles, restore and reproducibility,
package sources, internal packages, and vendored code.

**Out of scope:** which packages are used (see the technology inventories); admission
criteria ([`dependency-policy.md`](dependency-policy.md)); upgrade classification
([`versioning-policy.md`](versioning-policy.md)).

---

## 3. Version declaration

### 3.1 .NET — central package management

**All NuGet versions are declared in a single `Directory.Packages.props` at the backend
solution root. No project file contains a version number.**

| Rule | Statement |
| --- | --- |
| PM-1 | Central package management is enabled; `ManagePackageVersionsCentrally` is set |
| PM-2 | Project files declare `PackageReference` **without** a `Version` attribute |
| PM-3 | Versions are declared once, centrally, and apply to every project |
| PM-4 | A version override in a project file requires a recorded reason |
| PM-5 | Shared build properties — `LangVersion`, `Nullable`, `TreatWarningsAsErrors`, target framework — live in `Directory.Build.props` |

**Why this matters here specifically.** The backend has five projects plus six test
projects, all sharing a large dependency set. Without central management, EF Core can be
at one version in `Infrastructure` and another in a test project, producing failures that
reproduce in CI and not locally — or worse, the reverse.

### 3.2 npm — workspace and manifest

| Rule | Statement |
| --- | --- |
| PM-6 | Exact versions in `dependencies` — **no range specifiers** for direct dependencies |
| PM-7 | `engines` declares the required Node version; `.nvmrc` pins it for developers |
| PM-8 | The container base image, `.nvmrc`, and `engines` must agree |
| PM-9 | The console and the extension have separate manifests; they share no runtime dependencies |

**PM-6 is stricter than the npm default.** Caret ranges mean a fresh install can resolve a
different minor version than the lockfile, and the divergence surfaces as a bug that only
occurs on a clean checkout.

**PM-8 prevents a recurring class of confusion**: code that builds locally on one Node
version and fails in CI on another. All three declarations must be updated together, and
the runtime finding in [`technology-stack.md`](technology-stack.md) §3.3 means all three
change at once under decision TD-1.

---

## 4. Lockfiles and reproducibility

| Rule | Statement | Rationale |
| --- | --- | --- |
| PM-10 | **Lockfiles are committed** for every manifest — backend, console, extension | The resolved tree is reviewable and reproducible |
| PM-11 | CI restores **from the lockfile**, never resolving fresh | Otherwise CI tests a different tree than the developer did |
| PM-12 | A lockfile change is a reviewable diff | An unexplained transitive change is worth a question |
| PM-13 | NuGet lock files enabled with restore-locked mode in CI | The .NET equivalent of PM-11 |
| PM-14 | **Images are built once and promoted**, never rebuilt per environment | [ADR-0018](../03-adr/ADR-0018-docker.md) rule 1 — the tested artifact is the deployed artifact |

**PM-12 deserves attention in review.** A pull request that changes one direct dependency
and forty transitive ones is telling you something. Most of the time it is routine; the
value is in noticing when it is not.

---

## 5. Package sources

| Rule | Statement |
| --- | --- |
| PM-15 | Sources are declared explicitly in configuration — `nuget.config` and `.npmrc` — never implied |
| PM-16 | Only the official public registries, plus any internal feed |
| PM-17 | **Package source mapping is configured** so a package name resolves only from its expected source |
| PM-18 | Third-party GitHub Actions are pinned by **commit SHA**, never by tag |

**PM-17 defends against dependency confusion**, where an attacker publishes a package to a
public registry using the name of an internal one. It costs a configuration file and
removes an entire attack class — worth doing before an internal feed exists, not after.

**PM-18 is the same principle for CI.** A tag is mutable; a commit SHA is not.

---

## 6. Internal packages

**Default position: MaintOrbit AI publishes no internal packages.**

The backend is a single solution with project references
([ADR-0002](../03-adr/ADR-0002-modular-monolith.md)), so there is nothing to package. The
console and extension are separate applications that share no runtime code.

| Situation | Approach |
| --- | --- |
| Code shared between backend projects | Project reference within the solution |
| Code shared between console and extension | **None currently.** If it arises, a shared workspace package, not a published one |
| Client libraries for customers (FR-API-015, v1.1) | **Published** — TypeScript and Python, versioned per [`versioning-policy.md`](versioning-policy.md) |
| Code shared after module extraction | Published contract packages become necessary |

**Two future triggers will change this.** Customer-facing client libraries (v1.1) are the
first published artifacts and need their own versioning and support commitments. Module
extraction ([ADR-0002](../03-adr/ADR-0002-modular-monolith.md) §9) makes contract packages
necessary, because extracted services can no longer share project references.

Neither is needed now, and creating package infrastructure before it is needed is the kind
of premature structure that gate 4 of the dependency policy rejects.

---

## 7. Vendored code

Code copied into the repository rather than installed. It is **not** covered by any
package management mechanism, which is precisely the risk.

| Vendored item | Source | Obligation |
| --- | --- | --- |
| shadcn/ui components in `components/ui/` | shadcn/ui | Quarterly review against upstream; named owner |

| Rule | Statement |
| --- | --- |
| PM-19 | Vendored code records its source and the version or commit it was taken from |
| PM-20 | Vendored code is reviewed against upstream on a schedule |
| PM-21 | Vendored components are **not edited**; customization happens through tokens ([ADR-0024](../03-adr/ADR-0024-frontend-stack.md) FD-008) |
| PM-22 | Vendoring anything new requires the same admission gates as a dependency |

**PM-21 is what keeps PM-20 possible.** Once a vendored component is edited, comparing it
against upstream becomes a merge rather than a diff, and the review stops happening.

**Vendored code appears in no scan.** No vulnerability report, no upgrade notification, no
licence audit. It is the one dependency class where the schedule is the only control.

---

## 8. Restore and build

| Rule | Statement |
| --- | --- |
| PM-23 | A clean checkout restores and builds with a single documented command (NFR-PORT-004) |
| PM-24 | Restore is cached in CI, keyed on the lockfile hash |
| PM-25 | Build must complete within 15 minutes (NFR-MAINT-009) |
| PM-26 | The build fails on unresolved critical vulnerabilities (NFR-SEC-011) |
| PM-27 | The build fails on a disallowed licence class where a mechanical check exists |

**PM-25 is under continuous pressure.** Every gate added to
[ADR-0019](../03-adr/ADR-0019-github-actions.md) spends part of the budget, and the
failure mode is that gates get removed to recover build time. Parallelization and
selective execution by changed path are ongoing work, not one-time setup.

---

## 9. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Version drift between projects | Medium | Medium | Central package management (PM-1 … PM-4) |
| R-2 | CI resolves a different tree than the developer | High | Medium | Committed lockfiles; locked restore (PM-10 … PM-13) |
| R-3 | Dependency confusion via a public package shadowing an internal name | High | Low | Source mapping (PM-17) — configured before it is needed |
| R-4 | A mutable action tag is repointed to malicious code | High | Low | SHA pinning (PM-18) |
| R-5 | Vendored components drift from upstream and miss security fixes | Medium | **High** | Scheduled review; no editing (PM-19 … PM-21) |
| R-6 | Node version disagreement between `.nvmrc`, `engines`, and the base image | Medium | High | PM-8; verified in CI |
| R-7 | Build time exceeds budget, prompting gate removal | Medium | High | Caching, parallelization, selective execution |
| R-8 | A large transitive change passes review unexamined | Medium | High | Lockfile diffs are reviewable (PM-12) |

---

## 10. Future considerations

- **Client libraries (v1.1) change this document materially.** Publishing to public
  registries introduces release signing, deprecation policy, and a support commitment to
  consumers we do not control.
- **Module extraction requires contract packages.** Project references stop working across
  a service boundary, and contract versioning becomes a compatibility concern rather than
  an internal one.
- **Software bill of materials generation** follows naturally from committed lockfiles and
  will likely become a customer requirement.
- **Build provenance and artifact signing** may be required for SOC 2 (NFR-COMP-001).
- **An internal package feed** would be needed if internal packages ever exist. Source
  mapping (PM-17) is configured now so that adding one does not open a confusion window.

---

## 11. Cross references

| Document | Relationship |
| --- | --- |
| [`dependency-policy.md`](dependency-policy.md) | Which packages are permitted |
| [`versioning-policy.md`](versioning-policy.md) | How versions change |
| [`support-lifecycle.md`](support-lifecycle.md) | When versions must change |
| [`backend-technologies.md`](backend-technologies.md) | The NuGet set under central management |
| [`frontend-technologies.md`](frontend-technologies.md) | The npm set; vendored shadcn/ui |
| [`../03-adr/ADR-0018-docker.md`](../03-adr/ADR-0018-docker.md) | Immutable image promotion |
| [`../03-adr/ADR-0019-github-actions.md`](../03-adr/ADR-0019-github-actions.md) | Build gates referenced in §8 |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-MAINT-009/011, NFR-SEC-011/012, NFR-PORT-003/004 |
