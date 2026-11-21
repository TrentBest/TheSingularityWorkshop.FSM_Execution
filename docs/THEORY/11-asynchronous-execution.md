<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 11 — Asynchronous Execution and Work Ownership

**Chapter type:** execution semantics and lifecycle theory  
**Evidence scope:** the current sequential workflow runner and retained coordinator expose asynchronous APIs and cooperative cancellation; general child-task orchestration and detached-work management are not implemented as a shared engine.

## 01 — Asynchronous is not another word for parallel

Asynchronous execution describes how an operation's progress relates to its caller. The caller can yield control while the operation remains incomplete and later observes a result, failure, or cancellation.

Parallel execution describes simultaneous progress on multiple execution resources. These are independent dimensions:

| Case | Async? | Parallel? | Example |
|---|---:|---:|---|
| Blocking file read | No | No | Caller waits while the thread is blocked |
| Async network request | Yes | Not necessarily | Caller yields while one request waits for a response |
| CPU work on one thread | No | No | Synchronous calculation |
| Two CPU jobs on two cores | May be | Yes | Parallel numerical kernels |
| Async orchestration of parallel jobs | Yes | Yes | Coordinator awaits a group of worker operations |
| Event loop processing one callback at a time | Yes | Not necessarily | Many I/O operations progress without one thread per request |

An asynchronous method may still execute all CPU work on one thread. A parallel operation may be exposed through a synchronous API that blocks until workers finish. Never infer parallelism from the presence of `async`, `Task`, a callback, or a non-blocking API.

## 02 — What asynchronous execution changes

Async execution changes the lifetime and ownership of work:

- the initiating call may return before the operation completes;
- the operation needs an identity that survives beyond the initiating stack frame;
- exceptions may surface later through an awaited result or observation channel;
- cancellation is requested through a token, message, or model-specific control path;
- the host must retain enough ownership to observe completion and clean up resources;
- shutdown must define whether active work is joined, cancelled, drained, or handed off.

An async API is not safe merely because it returns a future or task. The system must account for the work until its terminal outcome is known or ownership has been explicitly transferred.

## 03 — Futures, tasks, callbacks, and event loops

Different runtimes expose asynchronous completion differently:

- **Future / promise:** a value that will become available later, either as success or failure.
- **Task:** a runtime representation of an operation's eventual completion; it may represent I/O waiting, CPU work, or composition of other tasks.
- **Callback:** a continuation invoked when an event or operation completes.
- **Event loop:** a scheduler that selects ready callbacks and advances them cooperatively.
- **Actor/message loop:** a serialized owner processes one message at a time while multiple messages may be queued or in flight.

These are implementation mechanisms, not execution models by themselves. A workflow model may be implemented with tasks; an event-driven model may use callbacks; a simulation may use a host loop. The declared model still determines what ordering, completion, and failure mean.

## 04 — I/O-bound and CPU-bound work

Async I/O and parallel CPU work solve different resource problems.

**I/O-bound work** spends much of its lifetime waiting for a device or remote system. A true asynchronous I/O operation lets the runtime release the waiting thread and resume when the result is ready. This can improve resource use without running the operation in parallel.

**CPU-bound work** spends its time computing. Marking it async does not create more CPU capacity. If the model permits independent calculations to run simultaneously, the engine may schedule them on a bounded worker pool or device. The engine must still account for resource limits, ordering, and cancellation.

A host should not wrap every operation in a worker thread. It should select the mechanism that matches the operation's resource behavior and declared semantics.

## 05 — Structured concurrency: child work belongs to a scope

When an execution instance starts child work, it should define a parent-child ownership relationship.

A structured scope answers:

1. Which parent instance created this child?
2. Which cancellation signal reaches it?
3. Who observes its exception and terminal result?
4. Does the parent wait for the child before completing?
5. What happens when one child fails?
6. Can the child outlive the parent, and if so, who adopts it?
7. How are child identities and evidence linked to the parent?

The default should be structured lifetime: children are joined or explicitly cancelled before the parent reports completion. Detached work is a deliberate handoff, not a fire-and-forget convenience. It needs its own durable identity, owner, capability scope, timeout policy, and observation path.

An engine must not report a parent as fully complete while unaccounted child operations can still mutate external state.

## 06 — Cancellation, deadlines, and failure propagation

Cancellation is cooperative. A request asks work to stop; the operation must observe that request at a safe point. It does not forcibly terminate a thread, undo a network request, or reverse a physical effect.

A structured async scope should define:

- whether cancellation propagates from parent to child;
- whether child failure cancels siblings;
- whether all children are awaited before the parent returns;
- whether failures are aggregated or one is selected as primary;
- whether cancellation is distinguishable from failure;
- how a deadline is measured and which clock governs it;
- how an operation that ignores cancellation is represented.

A timeout is a deadline policy that usually requests cancellation. It is not proof that the work stopped. The current workflow coordinator implements a cooperative timeout and reports `TimedOut` distinctly; a non-cooperative handler can keep the run active until it returns. Caller cancellation takes precedence when both signals are requested before the terminal lifecycle transition.

If an async operation may have crossed an external effect boundary, a timeout or lost response may produce an indeterminate result. The system must reconcile before retrying.

## 07 — Backpressure and bounded async work

Async APIs can make it easy to start more work than the host can support. An unbounded queue of pending operations can exhaust memory, connections, file handles, remote quotas, or downstream capacity.

A host-neutral execution contract should expose bounds for:

- maximum active children per execution instance;
- maximum queued work and queue overflow policy;
- global and per-resource concurrency;
- provider/network connection limits;
- request and response size;
- elapsed time and attempt budget;
- cancellation and shutdown drain time.

Backpressure is part of correctness. When capacity is exhausted, the engine should wait, reject, shed optional work, or apply a declared admission policy. It should not silently create an unbounded backlog.

## 08 — Deterministic testing of asynchronous behavior

Async tests should prove lifecycle and ordering, not merely wait long enough and hope a race occurs.

Prefer:
- controllable `TaskCompletionSource` gates to pause at known points;
- explicit signals for handler entry and completion;
- virtual or injected clocks for deadline behavior;
- bounded `WaitAsync` guards to prevent a hung test;
- assertions about snapshots before and after completion;
- tests for cancellation before start, during work, and during shutdown;
- tests for exceptions, duplicate completion, and child work that ignores cancellation.

Avoid relying on arbitrary sleeps as the only proof that an event has occurred. A test should control the boundary it intends to examine.

## 09 — Implementation map and limits

1. [WorkflowRunner.cs](../../src/Execution.Workflow/WorkflowRunner.cs) — awaits each registered handler in declared sequential order.
2. [WorkflowExecutionCoordinator.cs](../../src/Execution.Workflow/WorkflowExecutionCoordinator.cs) — retains a run identity, starts once, observes lifecycle, supports cancellation and cooperative timeout.
3. [WorkflowExecutionCoordinatorTests.cs](../../tests/Execution.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs) — tests timeout classification, cancellation precedence, non-cooperative handlers, and retained lifecycle.
4. [Workflow semantics](../WORKFLOW_SEMANTICS.md) — the normative version-1 workflow contract.

The current workflow runtime is asynchronous at its API and handler boundary, but each workflow's steps remain sequential. General structured child-work scopes, detached-work adoption, async fan-out/fan-in, global backpressure, and a general async scheduler remain future work. Multiple retained workflow instances do not by themselves constitute a general-purpose scheduler.

## Questions for the reader

1. Why does `async` not prove that two operations execute simultaneously?
2. What identity and ownership must survive after an initiating call returns?
3. Why should a parent normally join or cancel its children before reporting completion?
4. What is the difference between a timeout request and proof that work stopped?
5. Why can an unbounded async queue be a correctness problem?
6. Which test signal would prove that a handler entered a critical section without relying on a sleep?

## Exercise

Design an async parent execution that launches three children: a remote read, a CPU analysis, and an authorized repository write.

Define:
- each child's identity and parent correlation;
- which operations may overlap and why;
- the resource and capability limits;
- how child failure affects siblings;
- how cancellation propagates;
- which child results must be joined before the parent completes;
- how an uncertain repository write is reconciled before retry.

Then explain why making all three operations `async` does not automatically make them safe to run in parallel.

## Further reading

- [Execution Theory](../EXECUTION_MODEL_THEORY.md)
- [Chapter 08 — Scheduling, dependencies, and concurrency](08-scheduling-and-concurrency.md)
- [Chapter 12 — Parallel execution, graphs, and resource scheduling](12-parallel-execution-and-scheduling.md)
- [Workflow semantics](../WORKFLOW_SEMANTICS.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
