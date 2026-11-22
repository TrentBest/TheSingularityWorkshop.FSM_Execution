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
- Stale/mismatched provider identity, skipped event sequence, conflicting duplicate sequence, malformed terminal evidence, and terminal-state regression are rejected.
- `PersistentWorkTaskCoordinator` accepts the optional admission store and refuses to create an execution lease while the task's gate is Waiting or Blocked. A Satisfied observation permits normal claim.
- The admission journal is separate from the task snapshot journal. Hosts must create the Waiting gate before making the task eligible for dispatch and must reconcile orphan admission records on startup. This is single-process local durability, not distributed atomicity.

## Trust boundary and remaining integration

The store is a correlation and durability contract, not an authentication primitive. `ApplyVerifiedObservation` must only be called by a host adapter after authenticating the sender and independently verifying the provider's immutable evidence. Model output, issue text, callback metadata, or an unverified artifact is never sufficient.

This slice is a single-gate hold and does not yet implement a complete multi-gate AND/OR admission expression, a remote/distributed journal, authenticated CiAi callback ingress, automatic event transport, or a durable event-driven dispatcher. The host must wake only the task identified by a verified gate event; it must not poll or allocate a HeadlessAi turn while the task remains Waiting. Existing CiAi composite expressions remain the source of AND/OR policy; this journal must not become a second policy evaluator.

## Verification

`JsonlDependencyAdmissionStoreTests` covers restart persistence, exact provider identity, stale sequence rejection, idempotent delivery, terminal regression, and the persistent coordinator's no-claim-while-Waiting / no-claim-when-Blocked boundary.
