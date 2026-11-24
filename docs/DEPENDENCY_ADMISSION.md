# Dependency admission and dormant work

## Purpose

A dependency that has not finished is not a failed consumer task. It is a neutral admission hold. This slice adds a durable local record for that hold and fences task claiming until the provider capability has a terminal, exact-identity observation.

## State semantics

- **Waiting** — provider work is incomplete or evidence has not arrived. The task remains dormant; no execution lease can be claimed.
- **Satisfied** — the host has authenticated the provider observation and verified its exact repository, candidate, source SHA, capability, policy version, workflow run/attempt, and evidence digest. The task may be claimed.
- **Blocked** — the provider reached a terminal failure or the declared capability was rejected. The dependent task cannot be claimed.

A provider's Waiting state does not fail the consumer's own build. Consumer build/test failures remain consumer-owned. A terminal provider failure blocks only the dependent path. A later task or capability cannot silently replace the exact candidate identity.

## Implemented in this slice

- `DependencyGateIdentity` binds one task gate to provider repository, candidate ID, full source SHA, capability, and policy version.
- `JsonlDependencyAdmissionStore` persists append-only snapshots with fsync, strict replay validation, exact event sequence, and idempotent identical delivery.
- Stale/mismatched provider identity, skipped event sequence, backwards timestamps, conflicting duplicate sequence, malformed terminal evidence, and terminal-state regression are rejected.
- `PersistentWorkTaskCoordinator.SubmitWaiting` records the Waiting gate before creating the task under the same per-task reservation used by Claim. The coordinator refuses to create an execution lease while the task's gate is Waiting or Blocked. A Satisfied observation permits normal claim.
- The admission journal is separate from the task snapshot journal. Hosts must create the Waiting gate before making the task eligible for dispatch and must reconcile orphan admission records on startup. This is single-process local durability, not distributed atomicity.

## Trust boundary and remaining integration

The store is a correlation and durability contract, not an authentication primitive. `ApplyVerifiedObservation` must only be called by a host adapter after authenticating the sender and independently verifying the provider's immutable evidence. Model output, issue text, callback metadata, or an unverified artifact is never sufficient.

This slice now has a composite receipt and claim fence, but does not yet implement a remote/distributed journal, authenticated CiAi callback ingress, verified artifact retrieval, an event transport, or a durable event-driven dispatcher. The host must wake only the task identified by a verified gate event; it must not poll or allocate a HeadlessAi turn while the task remains Waiting. Existing CiAi composite expressions remain the source of AND/OR policy; this journal must not become a second policy evaluator.

## Verification

`JsonlDependencyAdmissionStoreTests` covers the single-gate path and composite Waiting/Satisfied/Blocked claim fences. `JsonlCompositeDependencyAdmissionStoreTests` covers composite restart persistence, expression/evidence-set binding, identity and sequence rejection, idempotency, and terminal-state regression.


## Composite pipeline integration boundary

CiAi is the sole owner of prerequisite expression semantics. The host must not reproduce AND/OR evaluation in this repository.

The intended host sequence is:

1. Persist the consumer task and its immutable candidate identity.
2. Resolve the named CiAi pipeline definition and persist its schema version, pipeline name, canonical definition SHA-256, and exact leaf identities.
3. Keep the task dormant while any CiAi leaf evidence is absent or the authoritative CiAi evaluator returns `Waiting`.
4. Authenticate the CiAi sender and durably accept the metadata-only artifact-ready notice using the v1 ingress contract. Resolve the notice through the pre-existing request/task mapping; never trust task identity from callback or model text.
5. Retrieve and verify the immutable evidence artifact against the exact request, run/attempt, candidate SHA, workflow identity, digest, and host policy.
6. Convert verified leaf evidence into CiAi's existing typed leaf evaluations and call `CiAiPipelineGateEvaluator.Evaluate`. Persist the exact expression digest, evidence-set digest, and returned three-valued result as one admission decision.
7. Wake only tasks whose expression depends on the changed gate, and only when the authoritative result is `Satisfied`. `Waiting` remains dormant; `Blocked` remains denied.

The original dependency journal remains a one-gate compatibility path. A separate `JsonlCompositeDependencyAdmissionStore` now persists a composite receipt without inventing a single provider run identity. The receipt binds task ID, consumer repository/candidate/source SHA, pipeline, CiAi canonical expression SHA-256, policy version, the complete verified leaf-evidence-set SHA-256, three-valued result, sequence, and timestamp. It is append-only, fsync-backed, restart-replayed, idempotent for identical sequence delivery, and rejects identity changes, sequence gaps, timestamp regression, conflicting duplicates, and terminal regression.

`PersistentWorkTaskCoordinator.SubmitCompositeWaiting` creates the durable Waiting receipt before task creation and binds it to the exact task/repository/source SHA. `Claim` now fences both single-gate and composite Waiting/Blocked admissions. A composite Satisfied decision permits normal claim. This is the durable composite admission/claim-fence slice; it does not yet call CiAi itself. The trusted host must invoke `CiAiPipelineGateEvaluator.Evaluate` on verified leaf evaluations and pass that exact result plus a digest of the complete verified evidence set to `ApplyVerifiedDecision`. The journal deliberately does not implement AND/OR semantics.

## Current end-to-end status

The repository already contains a detailed CiAi artifact ingress wire contract and local notice/request ledgers. The callback contract explicitly remains a design contract: the HTTPS receiver, JWT authentication, provider transport, artifact retrieval integration, and remote acknowledgement are not yet implemented. Existing GitHub Actions artifact provenance verification is a separate trusted-dispatch path and must not be mistaken for the CiAi artifact callback implementation.

A first working vertical slice therefore still requires authenticated notice ingress, immutable artifact retrieval/verification, composite CiAi evaluation, durable composite admission, and event-driven task wake-up. Normal PR CI passing proves compilation/tests only; it does not prove those privileged integrations.
