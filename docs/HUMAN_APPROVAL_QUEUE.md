# Human Approval Queue

## Purpose

ADP must not stop when a model proposes an operation outside its delegated authority. It records a durable approval request and lets an authenticated human inspect the exact proposal. The GUI is a client of the host's approval-query surface; it is not the authority store.

## Request lifecycle

```text
LLM response
  -> parse and validate operation proposal
  -> policy check
      -> permitted operation: host issues a narrow capability and executes
      -> human approval required: append Pending approval request
      -> invalid / denied: reject and record reason
  -> human reviews exact proposal and evidence in GUI
      -> Approve / Reject
  -> host revalidates task, candidate SHA, policy, and capability
  -> execute approved operation or leave it rejected
```

A pending request contains the task/correlation identity, repository, exact candidate SHA, model identity, operation kind, human-readable summary and rationale, exact JSON arguments, SHA-256 of those arguments, creation/expiry timestamps, and sequence. The proposal is immutable after creation. A decision is a second append-only snapshot with actor, reason, timestamp, and incremented sequence.

## Implemented in this slice

- `HumanApprovalRequest` is a versioned immutable proposal contract.
- `JsonlHumanApprovalRequestStore` persists requests and decisions in an append-only, fsync-backed JSONL journal.
- Replay validates request schema, argument digest, sequence continuity, and immutable proposal identity.
- Pending requests can be queried globally or filtered by repository.
- Only Pending requests can be decided; a second decision is rejected.
- Tests cover restart recovery, pending-query filtering, durable approval, immutable proposals, argument-digest validation, expiry bounds, and duplicate decisions.

## Authority boundary

**Approval is not execution.** The store does not grant a repository capability, create a branch, write a file, dispatch CI, merge a pull request, publish a package, or deploy anything. The host must authenticate the human, record the decision, revalidate the exact task and candidate identity, re-evaluate current policy, and issue a fresh narrowly scoped capability immediately before executing the approved operation.

The JSONL implementation is a single-process local durability primitive. It is not distributed storage, does not provide cross-process locking, and must be placed on a trusted host volume with appropriate access controls.

## GUI integration contract

The host should expose a read-only pending-approval query and an authenticated decision command. The GUI should display, at minimum:

- repository, task, and candidate SHA;
- requesting LLM/provider identity and operation kind;
- summary and rationale;
- exact proposed arguments (with safe formatting);
- creation and expiry time;
- decision state and, after decision, actor/reason/time.

The GUI must never infer approval from a click preview, a model response, or a task status label. It should submit an explicit decision command to the authenticated host and refresh from the authoritative queue. This slice also adds a reusable ASP.NET Core adapter: GET `/api/v1/adp/approvals` returns the pending queue (optionally filtered by `repository`), and POST `/api/v1/adp/approvals/{approvalId}/decision` accepts an explicit `approve` or `reject` decision with a reason. Both routes require a host-supplied `IHumanApprovalQueueAuthorizer`; the adapter has no anonymous or permissive default. The ServerApp host must register the journal, implement the authorizer against its real authentication system, and map the routes before a GUI can use them. The live GUI client and production ServerApp wiring remain outstanding.
