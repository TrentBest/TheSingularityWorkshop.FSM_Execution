# Quarantined Development Groups

## Purpose

Autonomous planning may prepare future work without admitting that work to the active execution system. The review store therefore accepts **opaque serialized candidate material**, not inactive manifests.

A development group is the unit of human review. It can contain one or more proposed work definitions plus the supporting research, documentation, evidence references, and revision history needed to assess them. Group membership and revision identity are review metadata; neither makes the candidate executable.

## Representation boundary

Candidate payloads are byte streams produced and retained through the FSM_Serialization boundary. The review store treats the payload as opaque bytes and records, at minimum:

- stable development-group identity;
- immutable revision identity and SHA-256 digest of the exact bytes;
- exact byte length and serialization-format/version identifier supplied by the producer;
- producer/mandate/task/turn provenance and creation time;
- supporting research, documentation, and evidence references;
- prior revision/supersession links;
- review state, decision, authenticated approver, reason, and decision time;
- post-approval materialization and validation outcome.

The store must preserve the original bytes unchanged. It must not parse candidate bytes into manifest classes, expose manifest properties, or place candidate definitions on the active Kanban before approval.

FSM_Serialization provides byte-stream and pack/unpack contracts; it is not a general-purpose object-graph serializer or schema registry. The domain that owns the manifest definition must own its representation contract. Execution's quarantine layer stores bytes and does not invent that domain schema.

## Review lifecycle

```text
bounded planner output
  -> serialize candidate material
  -> append opaque bytes to an unapproved development group
  -> retain supporting material and immutable revision digest
  -> authenticated human reviews the group/revision
       -> reject: retain bytes and decision; never materialize
       -> approve exact revision/digest:
            -> host reads bytes through the approved FSM_Serialization contract
            -> manifest-owning domain extracts the definition
            -> host validates schema, identity, dependencies, scope,
               budget, repo admission, and current policy
            -> only validated manifest classes may be admitted
               to the active Kanban
```

Approval is bound to the exact group revision and digest. If the bytes, supporting material covered by the review decision, or revision identity changes, the prior approval does not apply. A new revision requires a new decision.

Approval authorizes a **materialization attempt**, not execution. Materialized definitions remain inert until the normal active-work admission and execution authorization gates succeed.

## Separation from the existing operation approval queue

The existing `HumanApprovalRequest` queue is for a specific structured operation proposal with bounded JSON arguments and an exact candidate SHA. It is not the development-group review store and must not be repurposed as a container for proposed sprint manifests.

In particular, do not encode a proposed sprint as `ArgumentsJson`, create a `HumanApprovalRequest` per future task, or represent unapproved candidate bytes as a manifest with an `Approved = false` property. The two workflows have different objects and authority boundaries:

- operation approval: review one already-typed, bounded operation request;
- development-group review: review opaque serialized candidate material before a manifest definition exists.

They may share authenticated identity, decision audit, and GUI infrastructure, but their persistence contracts and state machines remain distinct.

## Fail-closed invariants

1. Unapproved or rejected groups cannot be claimed, scheduled, dispatched, or executed.
2. No manifest class is instantiated from candidate bytes before an authenticated approval of the exact revision and digest.
3. Approval never bypasses deserialization errors, schema checks, dependency resolution, repo admission, scope checks, budget checks, or current policy.
4. A failed or interrupted materialization is recorded against the approved digest; it does not silently create active work.
5. Original bytes and their digest remain available after approval, rejection, supersession, and failed materialization.
6. Active Kanban contains only validated, admitted manifest-backed work; quarantined proposals do not count toward actionable task reserve.
7. A changed revision cannot inherit an earlier approval.
8. Public status projections may expose safe counts and lifecycle summaries, but candidate bytes, private research, identities, and authenticated review actions remain behind the host's access controls.

## Acceptance tests

- Store and retrieve arbitrary bytes without parsing or mutating them.
- Verify digest and length on write and read; reject mismatches.
- Group multiple candidate revisions and supporting references without admitting tasks.
- Reject decisions leave the group quarantined and never invoke a materializer.
- Approval for revision A cannot authorize revision B, even if the group identity is unchanged.
- Materialization is invoked only after authenticated approval of the exact digest.
- Deserialization or any host validation failure leaves the group non-active and records the failure.
- Only a successfully materialized and validated definition can be admitted to active Kanban.
- Restart/replay preserves original bytes, revision chain, review decision, approver, and materialization outcome.
- Quarantined candidates are excluded from actionable task-reserve counts.
