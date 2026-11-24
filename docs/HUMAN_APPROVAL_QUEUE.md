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

A pending request contains the task/correlation/turn identity, repository, exact candidate SHA, model identity, operation kind, human-readable summary and rationale, exact JSON arguments, SHA-256 of those arguments, creation/expiry timestamps, and sequence. The proposal is immutable after creation. A decision is a second append-only snapshot with actor, reason, timestamp, and incremented sequence.

## Structured model operations

HeadlessAi can return a bounded list of structured operation proposals: an operation identifier, summary, rationale, and JSON-object arguments. These are still untrusted model data; HeadlessAi does not decide whether they are allowed.

Execution's ExecutionAgentOperationRouter processes the ordered proposals against the closed WorkTaskAction vocabulary and host-owned IExecutionAgentOperationPolicy:

- unknown operations are rejected;
- a host policy rejection stops the plan;
- a policy decision requiring human authority creates or retrieves one idempotent durable approval request;
- a policy-approved operation must still pass the live WorkTaskCapabilityRegistry demand before the host executor is called;
- processing stops at the first approval/rejection barrier; later proposals remain deferred.

The last-mile host executor must re-read current task state and re-demand the capability immediately before its side effect. An approved request is not auto-executed by the queue. The host resumes it through `ExecutionAgentTurnOrchestrator.ResumeApprovedAsync`, which reloads the exact completed turn from the durable journal and revalidates the candidate and current policy. The approval journal's one-way `Executing` fence prevents automatic replay after an ambiguous interruption; the host must reconcile that outcome.

## Implemented in this slice

- `HumanApprovalRequest` is a versioned immutable proposal contract.
- `JsonlHumanApprovalRequestStore` persists requests and decisions in an append-only, fsync-backed JSONL journal.
- Replay validates request schema, argument digest, sequence continuity, and immutable proposal identity.
- Pending requests can be queried globally or filtered by repository.
- Structured model operation proposals are mapped to the closed Execution action vocabulary and routed through host policy, capability demand, or the human approval queue.
- Replayed model turns use deterministic approval identities and return the existing exact request instead of creating duplicates.
- Only Pending requests can be decided; a second decision is rejected.
- An approved request resumes from the exact original completed HeadlessAi turn in the durable turn journal. Execution rechecks task/turn/candidate identity, current host policy, approval expiry, and a fresh narrow capability before invoking the host executor.
- The approval journal records `Executing` before the side effect and `Executed` only after a durable completion receipt. An interrupted `Executing` record is indeterminate and blocks automatic replay; an already `Executed` request returns its stored receipt without repeating the side effect.
- Tests cover restart recovery, pending-query filtering, durable approval and execution, immutable proposals, argument-digest validation, expiry bounds, capability revalidation, and duplicate execution prevention.

## Authority boundary

**Approval is not execution.** The store does not grant a repository capability, create a branch, write a file, dispatch CI, merge a pull request, publish a package, or deploy anything. An approved proposal is only executable through the explicit host resume path, with a newly demanded capability and current host policy. The host must authenticate the human, record the decision, revalidate the exact task and candidate identity, re-evaluate current policy, and issue a fresh narrowly scoped capability immediately before executing the approved operation.

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
