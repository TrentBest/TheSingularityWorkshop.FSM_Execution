<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Callback Contract

**Contract:** `execution.ciai-artifact-ingress.v1`  
**Status (2026-11-23):** the feature branch includes an RS256-authenticated ingress core, ASP.NET Core HTTPS route adapter, durable notice receipt, CiAi authoritative composite AND/OR evaluator bridge, bounded immutable artifact retrieval, authoritative `ciai.workflow-evidence.v2` packet validation, and append-only immutable packet staging with replay digest verification. It also includes a durable, idempotent consumer wake-up outbox with startup reconciliation and at-least-once delivery bookkeeping. Provider acknowledgement transport, relevant-event-only composite reevaluation, scheduler/ServerApp wiring, and the live Physics-to-raWWar pilot remain outstanding.  
**Owners:** Foundry owns the receiving endpoint, task correlation, authorization, durable receipt, evidence validation, and lifecycle decision. CiAi owns the evidence packet, immutable artifact publication, ready-notice delivery, and provider-side receipt ledger.

## Decision

Foundry consumes **a metadata-only artifact-ready notice and then pulls the immutable artifact**. It does not accept the full CI evidence packet as the callback body.

This reuses CiAi's existing [Capability Artifact Handoff Contract](https://github.com/TrentBest/TheSingularityWorkshop.CiAi/blob/development/docs/ARTIFACT_HANDOFF_CONTRACT.md): a notice is not the artifact, retrieval is read-only, the consumer verifies the bytes, and a receipt acknowledges exact artifact identity. The artifact payload for this integration is the UTF-8 JSON serialization of `ciai.workflow-evidence.v2`. Packet v1 is not accepted as authoritative candidate evidence for trusted-ref dispatch and must never be reinterpreted as v2.

A receipt confirms durable notice acceptance or artifact retrieval/integrity, depending on which receipt is being discussed. Neither receipt authorizes code changes, repository writes, retries, review handoff, merge, release, or publication.

## 1. Receiver endpoint

The Foundry host exposes this versioned route:

```http
POST /api/v1/integrations/ciai/artifact-ready
Content-Type: application/json
Authorization: Bearer <short-lived-host-issued-token>
```

The route is HTTPS-only on the deployment's standard TLS endpoint. The path is stable for contract version 1. A deployment may use a private network or gateway, but must not silently change the route or wire schema for a compatible v1 client.

The request body is the CiAi `ArtifactReadyNotice` v1 shape, including its nested `ArtifactManifest`. The callback carries metadata only:

- `contractVersion`, `noticeId`, `recipientIdentity`, and `notifiedAt`;
- manifest `requestId`, `operationId`, and `idempotencyKey`;
- `providerRepository`, capability identity/version, and immutable implementation revision;
- `artifactId`, `artifactVersion`, `mediaType`, `sizeBytes`, immutable `retrievalUri`, SHA-256 digest, and expiry;
- `sourceSha`, `providerRunId`, and intended `clientIdentity`.

The callback body must not contain artifact bytes, credentials, bearer tokens, capability IDs, or executable instructions. The sender must preserve the exact notice payload across delivery retries.

## 2. Authentication and sender authorization

The Foundry host issues or provisions a short-lived credential to the registered CiAi sender through its approved secret/identity channel. The sender presents it only in the HTTP Authorization header. Humans must never paste tokens into prompts, issues, comments, or request bodies.

The v1 bearer token is a signed JWT validated by the host against a configured issuer and signing-key set. Required claims:

| Claim | Requirement |
|---|---|
| `iss` | Exact configured trusted issuer |
| `aud` | `execution-ci-evidence-ingress` |
| `sub` | Registered CiAi sender identity |
| `scope` | Includes `foundry.ci-evidence.deliver` |
| `repositories` | Explicit repository allowlist for this sender |
| `iat`, `nbf`, `exp` | Valid now; maximum token lifetime five minutes |
| `jti` | Unique token identifier for replay/risk telemetry |

The host validates signature, issuer, audience, subject, scope, expiry/not-before, sender registration, repository allowlist, and recipient identity before accepting a notice. The host must not trust repository, task, or recipient claims merely because they appear in the body. The notice's `recipientIdentity` and manifest `clientIdentity` must both match the configured Foundry receiver identity.

Authentication failure returns 401; an authenticated sender without the required scope, repository authorization, or recipient binding receives 403. Credential expiry pauses delivery and is recovered through the host's secure re-authentication process. The same request/idempotency identity is retried after re-authentication.

## 3. Request validation and task correlation

The host validates the notice schema/version, all required identifiers, timestamps, digest, media type, payload size, HTTPS URI, expiry, sender identity, and recipient identity before durable acceptance.

Foundry creates and persists the request mapping before it asks CiAi to produce evidence. The current host ingress validates the RS256 JWT issuer, audience, registered subject/key, scope, bounded lifetime, repository allowlist, and recipient binding; it then requires an actionable persisted mapping and matches request ID, operation ID, idempotency key, provider identity, and exact candidate source SHA before the notice can be durably accepted. It also validates the v1 contract version, artifact digest/size metadata, HTTPS retrieval URI shape, and availability window. `MapCiAiArtifactReadyIngress` exposes this through an ASP.NET Core POST route, requires `Request.IsHttps`, `application/json`, exactly one Authorization header, and a streaming 64 KiB body bound. It maps accepted receipts to 202, exact duplicates to 200, conflicts to 409, and authorization failures to 401. The deployment host still owns trusted TLS/proxy forwarding, network rate limiting, dependency registration, and credential/key provisioning; the route does not download artifacts or validate the full workflow-evidence packet. The mapping binds the exact `requestId` and `operationId` to:

- Foundry task ID and correlation ID;
- expected target repository, isolated candidate ref/branch, candidate full SHA, and workflow path;
- expected trusted workflow ref (`refs/heads/development`) and an exact approved full workflow-definition SHA (or a bounded, explicit set of approved revisions) captured before dispatch;
- registered candidate-checkout job name, branch-resolution step name, and checkout-verification step name from the trusted workflow revision;
- expected CI run ID and run attempt when already known;
- authorized CiAi sender/provider identity and the exact idempotency key;
- configured artifact retrieval origin and provider acknowledgement destination;
- request expiry and task attempt/time budget.

The callback does not get to select or create a task. An unknown request, wrong operation, mismatched idempotency key, expired request, wrong recipient, or provider mismatch is rejected. Foundry obtains task/correlation identity from its own persisted request mapping, not from model output or an untrusted callback field.

**GitHub dispatch registration prerequisite:** GitHub accepts `workflow_dispatch` events only when the workflow file exists on the repository's default branch. Once registered, the API may select another branch/tag as the run ref, and GitHub uses the workflow version present at that selected ref. Therefore, a workflow file that exists only on a feature branch or `development` is not yet dispatchable. The host must verify the registered workflow identity and the exact trusted workflow revision selected for the run; it must not silently dispatch against `master`, promote workflow files, or treat default-branch registration as approval of candidate code. Initial registration is a repository-governance operation and remains outside Foundry's automatic mutation authority. See [GitHub's manual workflow dispatch requirements](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow) and [workflow trigger reference](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows).

For this evidence artifact, the manifest's `providerRepository` and optional `providerRunId` identify the trusted CiAi producer and its own execution provenance; they must match the registered provider identity and provider policy, not be confused with the target repository's CI run. The manifest's `sourceSha` must match the packet's `candidateSha`, never its workflow-definition SHA. The packet's `repository`, `workflowPath`, `runId`, and `runAttempt` must match the Foundry request mapping. Its `workflowRef` must equal the registered trusted workflow ref, and `workflowSha` must match the exact approved workflow revision recorded before dispatch; a branch name alone is not a workflow-trust decision. Its `candidateRef` and `candidateSha` must match the task's isolated feature ref and exact candidate commit. For separate candidate checkout, `event` must be `workflow_dispatch`, `candidateCheckoutEvidence` must be present, and `candidateCheckoutVerified` must be true after CiAi recomputes it from the exact run/attempt job and step observations. The nested `jobName`, `resolutionStepName`, and `verificationStepName` must exactly match the names registered for that trusted workflow revision; do not accept names chosen by the packet. A missing expected target-run identity is not permission to accept an arbitrary run; the host must apply the explicit request policy for that stage.

## 4. Durable callback receipt and idempotency

The implemented ingress facade authenticates and correlates the notice before it calls the existing durable receipt ledger. The ledger computes SHA-256 over the exact received UTF-8 request bytes and stores the validated notice, payload digest, sender identity, idempotency key, and receipt. The HTTP adapter must return an accepted response only after this call completes successfully; no network endpoint or retrieval scheduler is included yet.

The durable composite-admission bridge is wired to CiAi source pin `d4d2aa01985cc03b1a102b00f98e4b77eb666ac6`; the v2 workflow-evidence packet parser/types are independently pinned to `faebc0fa3add76812b8360b6ff6df0820ee2e96e` because the two open CiAi PR heads are separate, unmerged source lines. Foundry CI checks out and verifies both immutable commits separately. The Foundry contract project compiles `DependencyReleaseGate` and `CiAiPipelineGateExpression` from the first checkout and the packet/parser plus run/job observation contracts from the second. Neither source line is being merged or published by this integration. `CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist` verifies the expression pipeline and canonical `DefinitionSha256` against the task's persisted identity, rejects undeclared leaf-evidence keys, calls `CiAiPipelineGateEvaluator.Evaluate`, and persists its exact Waiting / Satisfied / Blocked result. Foundry does not reimplement CiAi's AND/OR semantics. The trusted host must still authenticate and independently verify every leaf evidence item and compute the complete evidence-set digest before calling the bridge; this adapter is not an artifact downloader or packet verifier.

The idempotency scope is sender identity plus manifest `idempotencyKey`. The `noticeId` is also unique within that sender. Outcomes:

- Same sender, key, and exact payload digest: return the original receipt as `duplicate`; do not enqueue a second retrieval or lifecycle event.
- Same sender and key with different payload digest: reject as an idempotency conflict; preserve the original receipt and record the conflict.
- Reused `noticeId` with different content: reject as a conflict.
- First valid notice: persist one stable receipt and enqueue one retrieval operation.

Accepted response:

```json
{
  "contract": "execution.ciai-artifact-ingress.v1",
  "receiptId": "stable-host-receipt-id",
  "noticeId": "sender-notice-id",
  "idempotencyKey": "stable-request-artifact-key",
  "outcome": "accepted",
  "acceptedAt": "2026-10-31T00:00:00Z"
}
```

An exact retry returns HTTP 200 with the same `receiptId` and `outcome: "duplicate"`. A first durable acceptance returns HTTP 202. A durable policy rejection may return a stable `rejected` receipt with a 4xx status. A malformed body that cannot be safely identified is rejected without pretending a durable receipt exists.

**Accepted means only that the notice is durably recorded.** It does not mean the artifact has been retrieved, its digest verified, its packet accepted, its CI run passed, or the task is review-ready.

## 5. Artifact retrieval and verification

After durable notice acceptance, `CiAiArtifactRetrievalService` retrieves the immutable artifact using read-only access. It rechecks the exact receipt digest, accepted notice identity, registered sender, provider repository allowlist, receiver identity, request/operation/idempotency mapping, candidate source SHA, active exact GitHub run binding, and the pre-dispatch artifact origin before issuing a GET. It does not execute, apply, or otherwise act on artifact contents.

The `retrievalUri` is treated as untrusted metadata and must match the provider-specific origin configured when the Foundry request was created. The production `CiAiArtifactHttpTransport` enforces HTTPS through the manifest/policy, port 443, no user-info/query/fragment, DNS/IP checks that reject loopback/private/link-local/reserved and other non-global destinations, no redirects, no proxy, no cookies, and no automatic decompression. DNS results are checked at connection time and the socket connects to a checked address. The host supplies short-lived provider-scoped authorization through `ICiAiArtifactCredentialProvider`; credentials are never embedded in the URI or model context and are not forwarded to another origin. The 10-second connection and 60-second total retrieval deadlines are enforced by the transport/service.

V1 limits:

| Input | Limit |
|---|---:|
| Ready-notice request body | 64 KiB |
| Evidence artifact | 16 MiB |
| Artifact media type | `application/json` |
| Artifact encoding | UTF-8, uncompressed |
| HTTP connection timeout | 10 seconds |
| Artifact retrieval deadline | 60 seconds |

`CiAiArtifactRetrievalService` streams the response and stops immediately if it exceeds the manifest size or the 16 MiB ceiling. It requires HTTP 200 from the exact configured URI, `application/json`, exact byte-count equality, and SHA-256 equality with the immutable manifest. The service returns a defensive-copy `CiAiVerifiedArtifact`. `CiAiWorkflowEvidencePacketValidator` now parses the exact UTF-8 bytes using CiAi's authoritative `CiWorkflowEvidencePacket.FromJson` implementation, rejects duplicate JSON properties, and validates the v2 schema, repository, workflow path/ref/SHA, candidate ref/SHA, workflow event, exact run ID/attempt, and registered candidate checkout job/step names against the persisted dispatch mapping and independently bound run. It also requires complete discovery/job evidence and recomputes the packet digest. The validator returns immutable task-bound evidence; it does not itself persist the packet, acknowledge the provider, or wake a task. Digest mismatch, size mismatch, redirect, expired artifact, wrong origin, disallowed DNS address, or non-JSON response is rejected.

The parsed packet is reconciled against the manifest and Foundry's persisted request mapping: exact repository, workflow path, trusted workflow ref and approved workflow SHA, candidate feature ref and full candidate SHA, run ID, run attempt, and request correlation. The manifest `sourceSha` binds to `candidateSha`. The run-evidence URL and job evidence URLs must identify that exact workflow run. Foundry retains both SHA roles separately; GitHub's run-level workflow SHA is not the code-under-test candidate SHA. Missing discovery/jobs, an empty job set, non-terminal or unsuccessful outcomes, and required-check omissions are not success. After validation, Foundry retains the immutable packet and its receipt/provenance in the same task/correlation evidence history as prior attempts. The verified packet may be supplied as bounded, read-only context to the next HeadlessAi turn; it remains untrusted evidence and cannot grant capabilities or authorize the model's proposed actions.

CiAi's packet preserves step facts but deliberately does not decide every consumer's required-step policy. Foundry applies the required job/step policy registered for the task. A packet's own `IsSuccessful` value is not a substitute for Foundry's expected-check evaluation. Even complete successful evidence can only move the task through Foundry's evidence policy; it cannot grant repository capabilities or privileged operations.



### CiAi workflow-evidence v2 packet validation (implemented on this feature branch)

The `CiAiWorkflowEvidencePacketValidator` consumes the verified artifact bytes and the exact `CiAiRequestTaskMappingSnapshot`. It refuses unbound, cancelled, expired, or not-yet-valid mappings. It uses the authoritative CiAi `CiWorkflowEvidencePacket.FromJson` parser from the pinned CiAi source checkout rather than maintaining a second packet schema in Foundry. The validator enforces strict UTF-8, a 16 MiB ceiling, JSON depth 32, no duplicate property names, contract version/schema v2, exact repository/workflow/candidate identities, exact workflow-definition and candidate SHAs, `workflow_dispatch`, exact run ID and attempt, and the pre-registered candidate checkout job/resolution/verification step names. It requires CiAi's recomputed `CandidateCheckoutVerified` and complete run/job discovery. It returns the parsed packet with request/task/operation/idempotency identity, exact run binding, and SHA-256 of the packet bytes. `IsSuccessful` remains a separate outcome fact: a complete but failed workflow is validly parsed evidence and must block a successful-build dependency rather than be mislabeled as malformed evidence.

This validation result is currently in-memory only. The next stage must durably stage the exact packet bytes and validation metadata before provider acknowledgement or any event-driven task wake-up.

## 6. Provider acknowledgement

The provider acknowledgement destination and its authentication profile are registered in Foundry's trusted CiAi provider configuration. They are not accepted as arbitrary callback-supplied URLs.

After retrieval and digest verification, Foundry sends CiAi's existing `ArtifactReceiptAcknowledgement` contract to that configured destination. It includes the exact request/artifact/version/digest, Foundry client identity, stable acknowledgement/deduplication key, retrieved and integrity-verified facts, acknowledgement time, and reconciled provider repository/run/source SHA. Local validation is reported separately:

### Provider-host HTTP contract

The registered provider host—not the CiAi library—owns the HTTPS receiver. The default v1 route is:

```http
POST /api/v1/integrations/ciai/artifact-receipts
Content-Type: application/json
Authorization: Bearer <short-lived-provider-host-issued-token>
```

Foundry stores the complete destination URI and a distinct provider-acknowledgement authentication profile in its trusted provider registration. The path above is the v1 route contract; deployments may configure a different origin, but must not silently substitute a different path or wire schema for a compatible v1 registration. The request body is the existing typed `ArtifactReceiptAcknowledgement` JSON, not a new Foundry-specific DTO. It contains `contractVersion`, `acknowledgementId`, `deduplicationKey`, `requestId`, `artifactId`, `artifactVersion`, `digest`, `clientIdentity`, `retrieved`, `integrityVerified`, `acknowledgedAt`, `localValidation`, `providerRepository`, `providerRunId`, and `sourceSha`.

The provider host provisions the credential to the Foundry workload through its approved workload-identity or secret-delivery channel. The token is short-lived (maximum five minutes) and validated by the provider host against its configured issuer/key set, `aud=ciai-artifact-receipt-ack`, `sub=<registered Foundry client identity>`, `scope=ciai.artifact-receipt.record`, valid `iat`/`nbf`/`exp`, unique `jti`, and repository/artifact authorization. The Foundry `sub` value and provider signing-key/issuer registration are deployment configuration, not package constants. The provider host must authorize the exact registered Foundry client and request/artifact scope; it must not trust arbitrary body fields as authorization. This credential direction is separate from the CiAi-to-Foundry ingress JWT: never reuse, forward, or broaden the ingress token for acknowledgement. No credential value is stored in Foundry task records, artifacts, logs, or model context.

The provider receiver resolves the exact immutable manifest before recording the acknowledgement and calls its existing durable receipt ledger. Its response is a credential-free JSON envelope:

```json
{
  "contract": "ciai.artifact-receipt-acknowledgement.v1",
  "receiptId": "stable-provider-receipt-id",
  "acknowledgementId": "foundry-acknowledgement-id",
  "deduplicationKey": "stable-request-artifact-key",
  "outcome": "recorded",
  "recordedAt": "2026-10-31T00:00:00Z"
}
```

The `receiptId` is generated by the provider host and remains stable for exact retries. HTTP/status mapping:

| HTTP | Provider outcome | Foundry behavior |
|---|---|---|
| 202 | `recorded`: first durable acknowledgement | Mark acknowledgement delivered |
| 200 | `duplicate`: exact retry; original receipt returned | Mark delivered; do not create a new acknowledgement |
| 400 | Malformed or unsupported acknowledgement | Stop; surface contract error |
| 401 | Missing, invalid, or expired provider credential | Pause, re-authenticate through the host, retry the same acknowledgement identity |
| 403 | Foundry client, scope, repository, or artifact not authorized | Stop and escalate registration/policy |
| 409 | Deduplication-key reuse with different acknowledgement content | Stop and escalate; never overwrite the original |
| 413 / 415 | Body too large / unsupported media type | Stop; do not retry unchanged |
| 422 | Exact manifest, digest, or acknowledgement provenance does not reconcile | Stop and retain the rejection evidence |
| 429 | Provider rate limit | Honor `Retry-After` within the bounded acknowledgement budget |
| 5xx / network timeout | Provider unavailable or response ambiguous | Retry the identical body/key with bounded exponential backoff and jitter |

The provider host must persist the acknowledgement receipt before returning 202/200. A timeout after durable recording is safe because Foundry retries the exact body and deduplication key and receives the original stable receipt. A conflict or permanent 4xx is not retried unchanged. The provider host may return a structured error envelope with `contract`, `code`, `message`, `retryable`, and `correlationId`; it must not return credentials, internal stack traces, secret-bearing URLs, or raw artifact content.



- `Accepted` means the artifact and packet schema/provenance were accepted as evidence input; it does **not** mean the CI run passed.
- `Rejected` means the artifact or its provenance failed consumer validation.
- `NotRun` means consumer validation did not complete.

The provider's receipt ledger must deduplicate exact acknowledgement retries and reject conflicting reuse of the deduplication key. A lost acknowledgement response is retried with the same identity. Acknowledgement does not command immediate artifact deletion; retention policy remains provider-owned.

If retrieval or verification fails, Foundry records the failure and does not send a successful integrity acknowledgement. Expired credentials pause retrieval/acknowledgement and require secure host re-authentication without changing the original task/correlation/request identity.

## 7. HTTP outcomes and retry policy

| HTTP | Meaning | Sender behavior |
|---|---|---|
| 202 | Notice durably accepted | Mark this outbox delivery accepted; retain provider artifact per retention policy |
| 200 | Exact duplicate; original receipt returned | Mark delivery accepted; do not create another notice |
| 400 | Malformed/unsupported wire shape | Do not retry unchanged payload; surface contract error |
| 401 | Missing/invalid/expired authentication | Pause and re-authenticate securely, then retry same payload/key |
| 403 | Sender, scope, recipient, or repository not authorized | Stop and escalate policy/configuration issue |
| 409 | Idempotency or notice identity conflict | Stop and escalate; never overwrite the first receipt |
| 413 | Notice body too large | Do not retry unchanged payload |
| 415 | Unsupported media type | Do not retry unchanged payload |
| 422 | Valid JSON but invalid/expired/mismatched notice | Stop and surface structured rejection |
| 429 | Rate limited | Retry after `Retry-After` within bounded policy |
| 5xx / network timeout | Receiver unavailable or response ambiguous | Retry same exact payload with exponential backoff and jitter |

The sender retains the outbox item until it receives a durable 202/200 receipt. A timeout after the host committed the receipt is safe: retrying the exact payload returns the original receipt. Retry delay starts at one second, doubles to a five-minute ceiling, includes jitter, and stops at notice expiry or the configured delivery budget. Do not create a new idempotency key to escape a failed delivery.

The host returns structured, credential-free error codes and a correlation/receipt reference. It never returns tokens, internal stack traces, secret-bearing URLs, or raw artifact content.

A structured error body uses `contract`, `code`, `message`, `retryable`, and `correlationId`; it may include `receiptId` only when a durable receipt exists. Messages are safe for logs and must not disclose internal exception details.

## 8. Security and lifecycle boundaries

- The callback receiver is a host adapter; this document does not claim it is currently running.
- The notice, artifact, digest, packet, and receipt are untrusted data until independently validated.
- A digest proves byte equality to the declared digest, not producer trust or semantic correctness.
- The callback and artifact pull are not repository write capabilities.
- No arbitrary shell/process/desktop execution is allowed.
- No direct writes to `master` or `development`, no automatic merge, branch deletion, deployment, release, or NuGet publication.
- CODE RED is operational urgency, not permission escalation.
- Preserve every packet/attempt and its provenance; never replace a failed attempt with a later green result.
- Keep provider execution facts, Foundry task decisions, and human review authorization as separate records.

## Implementation status and next steps

This document is the normative v1 host callback contract carrying CiAi evidence packet v2. Foundry now also includes `JsonlCiAiRequestTaskMappingJournal`, a host-neutral single-process dispatch-correlation core. `CreateDispatchIntent` durably records the immutable task/request/provider/workflow/candidate expectations before dispatch; `BindRun` then accepts one exact GitHub run binding only after the host independently verifies repository, workflow path/ref/SHA, candidate ref/SHA, event type, run ID, run attempt, and binding time against that intent. An identical binding is idempotent; a conflicting run binding or reuse is rejected. `GetActionable` refuses unknown, unbound, cancelled, or expired requests, so a ready notice cannot become task evidence until the dispatch run is bound. The journal replays its append-only records and rejects torn or conflicting history. It stores only an acknowledgement auth-profile identifier, never credentials. This local JSONL implementation is single-process and is not a cross-process transaction or GitHub dispatch adapter. Foundry now also includes `CiAiDispatchSubmissionCoordinator` and the host-owned `ICiAiWorkflowDispatcher` boundary. `SubmitAsync` persists the immutable request mapping before invoking the dispatcher, invokes it only for a newly appended intent, and returns `SubmittedAwaitingReconciliation` because the GitHub dispatch response does not supply a trusted run binding. An exact existing intent returns `ExistingIntentRequiresReconciliation` without redispatch; a conflicting intent never reaches the host. If dispatch throws or is cancelled after intent persistence, the durable intent remains and a retry must reconcile before any new dispatch is considered. This closes the blind duplicate-dispatch path without implementing GitHub transport or storing credentials. Foundry now also includes `CiAiDispatchRunMarker`, a strict parser for the `foundry.ciai-dispatch-marker.v1` UTF-8 JSON record that a trusted workflow must emit as run-scoped evidence. The marker is limited to 4 KiB and an exact six-property schema (`contract`, `requestId`, `operationId`, `idempotencyKey`, `runId`, `runAttempt`); duplicate/unknown fields, unsupported versions, invalid identities, and non-positive run identities are rejected. Binding the parsed marker to independently observed GitHub metadata also requires the exact run ID and attempt to match. Parsing validates only the wire contract: it does not prove where the bytes came from. The host must retrieve the marker from the artifact or trusted job evidence belonging to that exact run/attempt and independently verify the run metadata before constructing an observation. Foundry now also includes `CiAiDispatchRunReconciler`, a deterministic fail-closed core for a future host adapter: it requires exact request ID, operation ID, and idempotency-key identity read from the exact trusted workflow run/attempt, validates the run's repository, workflow path/ref/SHA, candidate ref/SHA, event, and bounded observation time against the durable intent, and binds only when exactly one candidate matches. Zero matches remain unbound; multiple matching runs are ambiguous and remain non-actionable; exact retries are idempotent. The adapter must obtain the request-identity marker from trusted workflow-produced run evidence, not from callback data or a search query. The reconciler does not call GitHub, dispatch workflows, retrieve job evidence, authenticate a host, or make run inputs appear in GitHub's run-list API. If the trusted workflow does not expose a verifiable marker for the exact run/attempt, the host must leave the intent unbound rather than infer identity from branch/SHA/time alone. Dispatch-intent recovery after a crash between GitHub accepting a dispatch and Foundry recording the run binding still requires a host adapter that discovers candidates from GitHub and independently retrieves and verifies the trusted marker; it must not guess. The ASP.NET Core endpoint adapter is now implemented in `Foundry.GitHubHost` as `MapCiAiArtifactReadyIngress`. It maps the default `POST /api/v1/integrations/ciai/artifact-ready` route, requires an HTTPS request, accepts only `application/json`, requires exactly one bearer Authorization header, enforces a 64 KiB body limit while streaming (including chunked bodies), and maps authentication, malformed payload, non-actionable mapping, and receipt-conflict outcomes to bounded HTTP responses. The endpoint delegates all trust decisions to `CiAiArtifactReadyIngress` and returns only the durable receipt ID and payload digest—not the stored notice body. The hosting application must register the configured ingress and durable ledgers, provision/rotate sender public keys, configure trusted TLS termination/proxy forwarding, and enforce network rate limits. The endpoint adapter does not configure or start a server by itself. The GitHub dispatch/metadata adapter, trusted workflow marker emission, exact-run marker retrieval, host-side crash-window recovery adapter, remote provider acknowledgement sender, relevant-event-only callback-to-task orchestration, and ServerApp production host wiring remain outstanding. The CiAi composite evaluator bridge, immutable artifact retrieval, packet v2 validation, durable packet staging/recovery, and durable consumer wake-up outbox are implemented on this feature branch; the outbox still requires a production scheduler adapter and startup/worker wiring. A real `workflow_dispatch` pilot also requires the workflow to be registered on the target repository's default branch; a feature-only workflow is insufficient. This repository-governance prerequisite is not performed automatically by Foundry. The callback/receipt envelope remains `execution.ciai-artifact-ingress.v1`; the immutable artifact schema is `ciai.workflow-evidence.v2`. Foundry now includes a host-neutral `JsonlCiAiArtifactNoticeReceiptLedger` core for already-authenticated, normalized notices. It computes no trust itself: the future HTTP adapter must authenticate the sender, validate the full notice and exact raw-body digest, and only then call the ledger. The ledger persists the exact UTF-8 notice body and a stable receipt before returning `Accepted`, computes SHA-256 from those bytes, binds the body `noticeId` and `manifest.idempotencyKey` to the normalized identity, returns the original receipt for an exact idempotent retry, reports conflicts for changed payloads or reused notice IDs, replays durable records after restart, and rejects torn/duplicate journal history. `GetAll()` exposes the durable inbox for host-worker reconciliation; because receipt persistence and retrieval scheduling are not yet one transaction, a future worker must rescan the ledger on startup and idempotently recover every accepted notice not yet processed. This JSONL implementation is single-process and does not provide cross-process or distributed serialization. The following remain unimplemented in Foundry: HTTPS listener/controller and TLS/rate-limit enforcement, JWT key provisioning and rotation, host-to-provider authentication provisioning, provider retrieval adapter, artifact staging/verification worker, remote provider acknowledgement sender, and callback-to-task orchestration. CiAi's current artifact handoff types and local outbox/receipt ledger do not yet provide authenticated remote transport or remote immutable artifact hosting. Both sides must implement their adapters against this contract and prove retries, duplicates, stale/wrong-SHA rejection, missing evidence, credential expiry/re-authentication, and a complete red-to-green MyVR pilot before describing the loop as live.

Related work:
- [Foundry issue #8 — CODE RED orchestration](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/issues/8)
- [CiAi issue #27 — exact-SHA evidence and bounded reruns](https://github.com/TrentBest/TheSingularityWorkshop.CiAi/issues/27)
- [CiAi artifact handoff contract](https://github.com/TrentBest/TheSingularityWorkshop.CiAi/blob/development/docs/ARTIFACT_HANDOFF_CONTRACT.md)
- [CiAi evidence packet PR #28](https://github.com/TrentBest/TheSingularityWorkshop.CiAi/pull/28)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>


## 9. Durable consumer wake-up outbox

`JsonlCiAiTaskWakeupOutbox` records a consumer wake-up only from a persisted `Satisfied` composite-admission snapshot returned by the authoritative CiAi evaluator bridge. `Waiting` and `Blocked` snapshots are rejected and cannot enter the outbox.

The outbox event identity is deterministic over the exact task, consumer repository/candidate/source SHA, pipeline, canonical expression digest, policy version, admission sequence, and complete evidence-set digest. That identity is also the scheduler idempotency key. A retry or startup reconciliation therefore cannot create a second logical wake-up for the same admission decision.

The host delivery protocol is deliberately at-least-once:

1. Read pending entries from `GetPending()`.
2. Persist `RecordDeliveryAttempt` before calling the task scheduler.
3. Deliver the exact task/candidate identity and stable idempotency key to the scheduler.
4. Call `MarkDelivered` only after the scheduler confirms durable acceptance.
5. If the host crashes or the response is ambiguous, retry the same event and idempotency key. The scheduler must deduplicate that key.

On host startup, call `ReconcileSatisfiedAdmissions` against the durable composite-admission journal before draining pending entries. This repairs the crash window where CiAi's Satisfied decision reached its admission journal but the process stopped before appending the outbox record. Reconciliation is safe to repeat.

The JSONL implementation is a single-process durability primitive, not a distributed queue or cross-process lock. It does not itself reevaluate gate expressions, deliver network events, or bypass the task `Claim` fence. The production host must invoke CiAi's evaluator only for relevant verified provider events, reconcile the complete current leaf-evidence set, enqueue the resulting Satisfied decision, and connect this outbox to the durable scheduler. Waiting tasks remain dormant and do not consume LLM turns. ServerApp wiring and the live Physics-to-raWWar pilot remain separate integration gates.
