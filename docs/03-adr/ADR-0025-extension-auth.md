# ADR-0025 — Extension authenticates by OAuth2/PKCE and gathers context explicitly

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0025 |
| **Title** | The VS Code Extension authenticates via OAuth2 with PKCE and transmits only explicitly selected context |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Security |
| **Implements** | XD-001 … XD-013; CTX-1 … CTX-6 |
| **Supersedes** | — |

---

## 1. Context

The VS Code Extension carries disproportionate strategic weight. It is where the P-03
persona forms their opinion of the platform, and it is the clearest expression of
`mission.md` §4.1 — governed AI that is more convenient than the ungoverned alternative,
at the moment of use.

It also creates the platform's most direct data-egress risk. An editor extension has
access to a developer's entire workspace: source code, configuration files, credentials in
`.env` files, customer data in fixtures. FR-EXT-014 forbids transmitting file content the
developer has not explicitly included.

The obvious authentication approach — generate a Platform API Key and paste it into
settings — puts a durable secret into a file that may be committed, synchronized across
machines, or shared in a screenshot. That is precisely the credential sprawl the product
exists to eliminate.

## 2. Problem Statement

How should the Extension authenticate without placing a durable secret on disk, and how
should it decide what leaves the developer's machine?

## 3. Decision

### (a) Authentication — OAuth2 authorization code with PKCE

| Property | Decision |
| --- | --- |
| Flow | OAuth2 authorization code with PKCE — no client secret can be embedded in a distributed extension |
| Refresh credential storage | **VS Code SecretStorage — the OS keychain.** Never in settings files, never synchronized |
| Access credential | Short-lived, held in **memory only** |
| Derivation | **From a Session, not a Platform API Key** |
| Sign-out | Clears secret storage and revokes server-side |

**Deriving the credential from a Session is the decisive choice.** Every revocation path in
ADR-0007 then applies unchanged — administrative session termination and Employee
deprovisioning both cut off the Extension with no separate mechanism to build or forget.

### (b) Context gathering — an explicit privacy boundary

Six rules, enforced in **one shared command pipeline**:

| # | Rule |
| --- | --- |
| **CTX-1** | Nothing is transmitted that the developer has not selected, opened and explicitly acted on, attached, or covered by a workspace rule they configured |
| **CTX-2** | The Extension **never walks the workspace opportunistically** to build context |
| **CTX-3** | Exclusion patterns apply after gathering and before transmission, honouring the workspace's ignore configuration |
| **CTX-4** | Content matching common secret shapes is removed before transmission, and the removal is disclosed |
| **CTX-5** | **What will be sent is visible to the developer before it is sent** |
| **CTX-6** | Size limits are enforced client-side, with truncation disclosed rather than silent |

**All commands share one pipeline.** Per-command context handling would mean the privacy
boundary is implemented several times, and the weakest implementation would define the
platform's actual behaviour.

### (c) Other binding decisions

- **The webview holds no credentials and makes no network calls.** It communicates only
  through VS Code's message-passing channel.
- **Governance is enforced server-side.** The Extension is a modifiable artifact; a
  client-side policy check is advisory at best.
- **No file modification at MVP.** Output goes to the panel; diff application arrives at
  v1.1 (FR-EXT-012).
- **Conversation history lives server-side**, so Extension conversations appear in the
  console under the same retention policy.
- **Lazy activation** — on command invocation or panel open, never on editor startup.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Pasted Platform API Key in settings | Simplest integration | **Puts a durable secret in a file that may be committed or synchronized.** This is the credential sprawl problem the product exists to solve; reproducing it in our own client would be indefensible |
| Device code flow | No browser redirect handling | Viable and simpler in some respects. Rejected: a worse experience when a browser is available, which it almost always is on a developer machine |
| Workspace indexing for context | Index the repository for richer assistance | Substantially more capable, and what several competitors do. **Rejected on CTX-2** — opportunistic gathering is exactly what FR-EXT-014 forbids, and the trust cost of a surprise upload would be severe for a governance product |
| Client-side governance enforcement | Evaluate policy locally for instant feedback | Bypassable by a modified client. Retained only as UX affordance; enforcement stays server-side |
| Local conversation history | Store history on disk | Creates a second store with different retention behaviour — an inconsistency the P-06 persona would identify immediately |
| File modification at MVP | Apply suggestions directly | An assistant that edits code before developers trust its output gets uninstalled |

## 5. Pros

- **No durable secret on disk in a readable file.** The refresh credential is in the OS
  keychain and is not synchronized.
- **Revocation is free** — Session derivation means ADR-0007's mechanisms apply unchanged.
- **The privacy boundary is enforced once**, in one pipeline, rather than per command.
- **CTX-5 makes the boundary trustworthy.** A developer who can see what will be sent can
  correct a mistake; one who cannot must trust an invisible rule and will eventually be
  surprised.
- **CTX-4 catches the most likely real leak** — a developer selecting a configuration block
  containing a credential. The Extension is the last point at which that can be stopped
  before it reaches a third-party provider.
- **Workspace context rules (v1.1) are committable and therefore reviewable**, giving teams
  a lightweight governance mechanism that shows up in a diff rather than in someone's local
  settings.

## 6. Cons

- **Conservative context gathering makes the Extension less capable** than competitors that
  index the workspace. This is a real competitive cost, accepted knowingly.
- **CTX-5 adds a step** before each request, which some developers will find tedious.
- **OAuth2 is more complex** than reading a key from settings — a browser round trip and
  redirect handling.
- **Server-side governance means a round trip before a block is known**; no instant local
  feedback.
- **Server-side history requires connectivity** to view past conversations.
- **CTX-4 detection is imperfect** and must not be presented as a guarantee.

## 7. Consequences

- **Context gathering rules are a reviewed security boundary.** Changes to CTX-1 … CTX-6
  require security review, not ordinary code review. The risk is expansion over time until
  FR-EXT-014 is violated by accretion rather than by decision.
- **CTX-4 must be presented as best-effort**, never as a guarantee. Overstating it would
  encourage developers to rely on it.
- **Error messages must distinguish the developer's problem from the organization's.** A
  budget rejection is not something a developer can fix by changing their prompt;
  presenting it as a generic failure wastes their time and generates support load.
- **Version compatibility must be checked explicitly.** The Extension and platform version
  independently, and self-hosted customers will routinely run older platform versions —
  a scenario that becomes routine at v2.1.
- **Private distribution is required** for self-hosted customers who restrict marketplace
  access. This is a packaging requirement to establish now, not a surprise later.
- **Extension usage must be attributed by Surface** (FR-EXT-007) so it is distinguishable
  in analytics from Gateway and Chat traffic.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Context gathering expands over time until FR-EXT-014 is violated | **Critical** | Medium | One pipeline; CTX rules are a reviewed boundary; changes require security review |
| R-2 | A developer transmits a credential in a selection | High | **High** | CTX-4 detection; CTX-5 disclosure; server-side governance as backstop |
| R-3 | Conservative context makes the Extension less useful than competitors | High | **High** | Workspace inclusion rules (v1.1) give teams explicit, reviewable control |
| R-4 | Refresh credential extracted from the keychain by local malware | High | Low | Short access lifetimes; server-side revocation; outside the platform's threat boundary |
| R-5 | Version incompatibility with self-hosted platforms produces confusing failures | Medium | High | Explicit compatibility check with a clear message |
| R-6 | Latency makes in-editor assistance feel sluggish | High | Medium | Streaming; time to first token is the metric that matters, not total duration |
| R-7 | The webview is granted network access during development and it persists | High | Low | Content security policy in the webview; architecture review |

## 9. Future Revisions

Revisit when:

- **Diff application ships (FR-EXT-012, v1.1).** Writing to files requires a different
  level of trust and a reviewable preview. It should not be rushed.
- **Workspace context rules ship (FR-EXT-013, v1.1).** Committable rules reviewed in pull
  requests are a genuinely useful governance surface and deserve proper design rather than
  treatment as configuration.
- **The JetBrains extension arrives (FR-EXT-015, v2.0).** It should share **nothing but
  contracts** — sharing implementation across editor platforms is a well-known trap; the
  API is the correct shared surface.
- **Competitive pressure on context richness becomes decisive.** If conservative gathering
  costs adoption materially, the answer is expanding *explicit* developer control
  (workspace rules, attachments), never silent gathering.
- **Agentic workflows are considered.** Multi-step operations touching many files require a
  different interaction model, context boundary, and approval model. Out of scope, and
  worth stating so it is not assumed.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/vscode-extension-architecture.md`](../02-architecture/vscode-extension-architecture.md) | Full extension design; XD-001 … XD-013 |
| [`../02-architecture/request-flow.md`](../02-architecture/request-flow.md) | F-5 extension command flow |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Session model the credential derives from |
| [`ADR-0016-rest-api.md`](ADR-0016-rest-api.md) | The API the Extension consumes |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Governance enforcement point |
| [`ADR-0017-object-storage.md`](ADR-0017-object-storage.md) | Attachment storage, v1.1 |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-03 adoption and abandonment criteria |
| [`../01-product/mission.md`](../01-product/mission.md) | §4.1 — the governed path must be the convenient one |
| [`../01-product/problem-statement.md`](../01-product/problem-statement.md) | §3.1 — the credential sprawl this must not reproduce |
