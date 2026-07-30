# ADR-NNNN — Title

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-NNNN |
| **Title** | Short imperative statement of the decision |
| **Status** | Proposed / Accepted / Rejected / Deprecated / Superseded by ADR-NNNN |
| **Date** | YYYY-MM-DD |
| **Deciders** | Role or group |
| **Implements** | AD-NNN from `docs/02-architecture/` |
| **Supersedes** | — |

---

## 1. Context

What is true about the system, the requirements, and the constraints that makes this
decision necessary now. Facts, not opinions. Reference specific requirement
identifiers.

## 2. Problem Statement

The question this ADR answers, stated as a single question or a short paragraph. If
the problem cannot be stated in three sentences, it is probably two decisions.

## 3. Decision

What was decided, stated in the active voice and unambiguously. A reader should be
able to tell whether an implementation conforms.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |

Include the option of doing nothing where it is genuinely viable. An ADR with one
alternative is a justification, not a decision record.

## 5. Pros

What this decision buys, tied to specific requirements where possible.

## 6. Cons

What it costs. An ADR with no cons has not been thought about honestly.

## 7. Consequences

What must now be true, built, or maintained because of this decision. Include
consequences that fall on other teams and future work that is now foreclosed.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |

## 9. Future Revisions

The conditions under which this decision should be revisited, and what would replace
it. A decision with no revision trigger is dogma.

## 10. Related Documents

| Document | Relationship |
| --- | --- |

---

## Usage notes

- **Identifiers are permanent.** A superseded ADR is marked superseded, never deleted
  and never renumbered.
- **Status changes are edits to the ADR**, recorded with a date, not new documents.
- **One decision per ADR.** If the title needs "and", write two.
- **Write it before implementing**, not after. An ADR written to justify existing code
  is documentation, not a decision record.
