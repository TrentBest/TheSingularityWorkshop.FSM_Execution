# ADP implementation status

This page separates implemented contracts from the remaining production-host work. The test suite currently exercises the provider boundaries with deterministic fakes; that is not a claim that a live GitHub-to-HeadlessAi repair cycle has been completed.

## Implemented in the current integration branch

- **Artifact-ready ingress contract:** exact UTF-8 request bytes are authenticated by a host-supplied ICiAiArtifactNoticeAuthenticator, validated against the persisted dispatch mapping, and durably receipted before acknowledgement.
- **HTTP ingress adapter:** POST /api/v1/integrations/ciai/artifact-ready enforces a 64 KiB request-body limit and maps accepted, duplicate, conflict, unauthorized, and invalid-notice outcomes.
- **Report retrieval and verification:** the artifact retrieval coordinator consumes only explicitly registered HTTPS origins, rejects redirects, enforces bounded content, and verifies the notice-bound digest.
- **Nested source-artifact verification:** the archive verifier accepts exactly the configured TRX and provenance entries, checks the provider-observed archive digest and size, and bounds extraction and compression ratio. CiAiSourceArtifactBindingFactory binds the extracted payload to provider-observed run/job/artifact metadata.
- **Diagnostic report consumption:** CiAiTestResultReportConsumer validates ciai.test-results.v1, the immutable dispatch/run binding, candidate SHA, provider-observed job status/conclusion, report digest, and nested source-artifact identity. It returns an internally constructed diagnostic context only; it cannot mutate task state or create a repair attempt.
- **Authoritative lifecycle gate:** WorkTaskRepairDecisionPolicy now rejects lifecycle attempts unless EvidenceSchema is exactly ciai.workflow-evidence.v2. Accepted task events persist that schema, candidate SHA, exact run/attempt, and authoritative packet digest. Diagnostic test reports are accepted for repair context only after a matching authoritative RepairRequired event and durable v2 attempt already exist.
- **HeadlessAi handoff:** an accepted report is carried as opaque, digest-bound context with the exact-run evidence reference. Repair turns require the prior completed turn, prior candidate SHA, accepted actionable report, and matching exact-run evidence.
- **Human approval:** approved proposals resume from their original durable HeadlessAi turn, revalidate current policy and a fresh narrow capability, and use durable Executing/Executed states. An interrupted Executing operation is indeterminate and is never replayed automatically.
- **Host composition:** ExecutionAgentTurnOrchestrator provides InvokeRepairAndRouteAsync for accepted CiAi feedback and ResumeApprovedAsync for explicit human-approved operations.

## Remaining before a live production demonstration

1. Implement the producer and consumer for ciai.workflow-evidence.v2. The current Execution lifecycle policy now requires this contract, but the authoritative v2 packet parser/producer is not yet complete in this branch. Do not substitute ciai.test-results.v1.
2. Implement and register a request-scoped OIDC/JWT authenticator for ICiAiArtifactNoticeAuthenticator. It must validate signature, issuer, audience, subject, scope, expiry, and sender-repository authorization.
3. Implement a GitHub provider observer that obtains run, exact job, and artifact metadata from authenticated provider API responses. Do not construct CiAiProviderSourceArtifactObservation from the report JSON.
4. Implement the authenticated provider artifact-download adapter, including safe handling of the provider's archive redirect and a separately pinned, credential-free storage-origin download.
5. Wire the ingress and approval endpoints into the selected host (ServerApp or another authoritative host), with explicit DI registrations and authorization policy.
6. Run a live proof using a real candidate commit and real GitHub Actions artifacts: fail → authoritative v2 evidence → diagnostic report → bounded HeadlessAi proposal → human approval → isolated candidate operation → exact-SHA green rerun → draft PR.
7. Reconcile repository branches to the two-branch standard (master and development) in one reviewed batch. No branch has been deleted as part of this work.

## Safety and release boundaries

- ciai.test-results.v1 remains diagnostic input. It does not authorize repository writes, merges, publication, or deployment.
- ciai.workflow-evidence.v2 and exact-run evidence remain the authoritative CI/check-gate contract.
- Repository operations require an explicit host capability and, where policy requires, a durable human approval.
- No PR merge, canonical-branch write, branch deletion, NuGet publication, release, or deployment is performed by this implementation work.
