# CiAi artifact callback and evidence boundary

This contract describes the Execution-side receipt and consumption boundary. It deliberately separates authoritative workflow evidence from diagnostic test-result reports.

## Two distinct evidence contracts

### Authoritative target: ciai.workflow-evidence.v1

CiAi's current alpha producer contract is `ciai.workflow-evidence.v1`. Alpha contract evolution stays within V1; V2 is not a parallel or fallback contract.

**Execution integration status: blocked/incomplete.** This branch's lifecycle journal and repair policy still require `ciai.workflow-evidence.v1`, and this branch does not yet contain the authoritative V1 packet parser, current-policy revalidation, durable V1 evidence staging/recovery, or a composed V1 packet-delivery/receipt path. Therefore this branch does not yet consume the current CiAi authoritative packet and must not be described as V1-compatible or ADP-ready.

Only an independently validated V1 packet, rebound to the persisted dispatch intent and exact provider run/attempt under current host policy, may drive task lifecycle decisions or repair policy.

The authoritative consumer must bind the packet to the persisted dispatch intent and exact provider run/attempt, including:

- repository and task/correlation identity;
- trusted workflow path, workflow ref, and workflow-definition SHA;
- workflow event and event SHA;
- candidate branch/ref and exact candidate commit SHA;
- exact run ID and run attempt;
- complete required-check discovery and each required check's terminal status/conclusion;
- packet digest and observation time.

Current code still requires the obsolete V2 identifier in WorkTaskRepairAttempt and WorkTaskRepairDecisionPolicy; that implementation mismatch is intentionally called out above and remains a blocker. The target contract is V1. Do not treat changing the string constant alone as completing the migration: the V1 parser, trust re-evaluation, durable staging/recovery, exact packet digest binding, tests, and delivery receipt path must move together.

### Diagnostic only: ciai.test-results.v1

The test-results report may contain bounded TRX summaries, failed test identities, reason codes, and source-artifact lineage. It is not the workflow gate and cannot:

- move a task into RepairRequired or ReviewReady;
- establish that required checks passed or failed;
- satisfy a required check;
- grant a repository capability;
- authorize a write, merge, package publication, or deployment.

Execution may consume this report only after the task is already RepairRequired from a matching independently validated V1 packet event and durable authoritative attempt. The current V2-only lifecycle policy means this precondition is not yet satisfiable through the current CiAi V1 producer. The report must match the same repository, candidate SHA, run ID, and attempt. Its provider-observed job status/conclusion and nested source-artifact identity are independently checked. The consumer returns a diagnostic-only context for HeadlessAi; it does not create a WorkTaskRepairAttempt or mutate task state.

## Artifact-ready notice

The host-neutral ingress accepts the exact request body bytes and validates the notice against the persisted dispatch mapping and exact run binding. The HTTP adapter exposes:

- POST /api/v1/integrations/ciai/artifact-ready

The route enforces a 64 KiB body limit. The host-supplied ICiAiArtifactNoticeAuthenticator must authenticate the sender before the notice is accepted. A successful response means only that a valid notice identity was durably receipted; it does not mean the artifact bytes have been downloaded or verified.

Duplicate notice identities are idempotent. A repeated identity with different payload content is a conflict and fails closed.

## Retrieval and nested source-artifact binding

The report artifact retrieval coordinator:

- accepts only configured HTTPS origins;
- rejects redirects;
- enforces response-size limits;
- verifies the exact notice-bound digest and media type.

The nested source-artifact archive verifier accepts exactly the configured TRX and provenance entries, rejects unsafe/duplicate paths, and bounds archive size, extracted size, and compression ratio.

CiAiSourceArtifactBindingFactory creates the nested source binding only from both:
1. independently observed provider run/job/artifact metadata; and
2. the exact downloaded archive bytes whose provider digest and size are verified.

The factory does not fetch or authenticate provider metadata. The host must obtain it from authenticated provider API responses, not from fields inside the report JSON.

## HeadlessAi and human approval

The diagnostic context is passed to HeadlessAi only when the task is already RepairRequired and the exact prior completed turn/candidate are supplied. The accepted report remains opaque and digest-bound; it is context, not authorization.

HeadlessAi may propose bounded operations. Execution rechecks policy, task lease, capability scope, expiry, and path/action limits. Operations requiring human approval are persisted in the approval queue. A durable Executing fence is written before external side effects; an interrupted operation is indeterminate and is never automatically replayed.

## Production-host work still required

- Implement and register a request-scoped OIDC/JWT authenticator for ICiAiArtifactNoticeAuthenticator (signature, issuer, audience, subject, scope, expiry, and sender-repository authorization).
- Implement the authenticated provider observer for exact run/job/artifact metadata and the provider archive-download adapter, including safe redirect handling and credential separation.
- Wire the ingress and approval endpoints into the authoritative host with explicit DI registrations and authorization policy.
- Complete a live end-to-end run using a real candidate commit and real GitHub Actions artifacts.

The deterministic tests exercise these boundaries with fakes; they do not prove the live provider adapters are deployed or configured.
