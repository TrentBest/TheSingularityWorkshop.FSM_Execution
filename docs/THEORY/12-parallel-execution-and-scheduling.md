<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 12 — Parallel Execution, Graphs, and Resource Scheduling

**Chapter type:** execution semantics and scheduler design  
**Evidence scope:** the current workflow model executes steps sequentially. It does not provide parallel steps, a general dependency-graph scheduler, worker-pool governance, or distributed scheduling.

## 01 — Parallelism is a semantic choice

Parallel execution allows multiple units to make progress at the same instant on distinct execution resources. It can reduce elapsed time, increase throughput, or exploit hardware, but it also expands the set of possible interleavings and partial outcomes.

Parallelism is not a switch that can safely be added to an ordered list. A model must state which operations are independent, which resources they share, and how their outcomes are combined. If that information is missing, serialization is the safe default.

## 02 — Concurrency, parallelism, and scheduling

- **Concurrency**: multiple operations have overlapping lifetimes or are in progress during the same interval.
- **Parallelism**: multiple operations make progress simultaneously on separate execution resources.
- **Scheduling**: deciding when eligible work receives available resources.
- **Dependency**: a condition that must be satisfied before a unit becomes eligible.
- **Resource claim**: a declaration of the capacity or exclusion an operation needs.

A single-threaded event loop can be concurrent without parallel execution. A CPU worker pool can execute independent tasks in parallel. A distributed worker system may be both concurrent and parallel while introducing network delay and partial failure.

The scheduler should be a replaceable mechanism behind explicit model semantics. The model must not depend on a particular thread pool, CPU count, GPU, or host operating system unless those are part of its declared contract.

## 03 — Dependency graphs

A dependency graph represents work units as nodes and prerequisite relationships as edges. A node becomes eligible when its required incoming dependencies have satisfied their declared conditions.

Not all edges mean the same thing:

- **Data edge**: the consumer needs a value produced by the prerequisite.
- **Ordering edge**: the prerequisite must finish first, even if no value is passed.
- **Evidence edge**: a validated result is required before the action is admitted.
- **Lifecycle edge**: a task or execution instance must reach an allowed state.
- **Resource edge**: work must wait while a conflicting resource claim is held.
- **Authority edge**: a host grant or authenticated human decision is required.

A graph validator should reject missing nodes, duplicate identities, illegal edge types, and cycles before side effects begin. It should define whether a failed prerequisite blocks descendants, marks them skipped, or allows an alternate path. It should also freeze the admitted graph version so an in-flight execution cannot silently change meaning when the source definition is edited.

## 04 — Safe parallel admission

Two nodes may run together only when:
1. all required prerequisites are satisfied;
2. their resource claims are compatible;
3. their capability grants are valid for the exact execution instance;
4. neither depends on the other's output or ordering;
5. the model defines how their outcomes are joined;
6. cancellation and failure propagation are defined.

Read-only inspections over immutable inputs may be good candidates. Writes to the same branch, file set, database row, device, or external resource generally need serialization, isolation, or an explicit conflict-resolution protocol.

An operation's label is not proof of safety. A handler named `Read` may mutate a cache; a model may invoke a remote API with hidden effects. Resource declarations must be backed by the host adapter and tested behavior.

## 05 — Resource scheduling and fairness

A scheduler operates under finite capacity. It should make the following policies explicit:

- per-instance, per-tenant, per-resource, and global concurrency limits;
- CPU, memory, GPU, connection, file-handle, and remote quota budgets;
- priority, fairness, starvation prevention, and aging;
- admission rejection or queueing when capacity is exhausted;
- work stealing, batching, or locality rules when applicable;
- preemption and checkpoint semantics, if supported;
- deadline and missed-deadline behavior;
- backpressure, queue bounds, and shutdown/drain policy.

A thread count is not a complete scheduler policy. A host may need to limit the number of concurrent remote calls separately from CPU work, and a GPU may have a different memory or stream budget from the CPU worker pool.

## 06 — Result ordering and determinism

Parallel completion order is not necessarily declaration order. A model must specify whether:
- outputs are collected in source order or completion order;
- reductions are order-independent;
- nondeterministic ordering is acceptable;
- a stable tie-breaker is required;
- traces capture scheduling decisions and observed completion order.

Floating-point reductions, shared mutable state, event delivery, and external side effects can vary with interleaving. If reproducibility matters, record the input set, engine version, scheduler configuration, random seeds where applicable, external observations, and the relevant ordering decisions.

A deterministic final value does not imply a deterministic trace. A deterministic trace does not prove an external side effect happened exactly once.

## 07 — Partial failure and cancellation

Parallel work can partially succeed. If one node fails, others may already have completed or crossed effect boundaries. The model must define whether siblings continue, are cancelled, or are allowed to finish for evidence collection.

Before returning an aggregate outcome, the engine must account for every admitted child:
- completed successfully;
- failed;
- cancelled and observed cancellation;
- skipped because prerequisites failed;
- still running under an explicit handoff;
- indeterminate after an external effect or lost acknowledgement.

Cancellation is cooperative. A timeout cannot forcibly stop an unresponsive worker. A parent must not claim full cancellation while detached work may still mutate a resource. Retry must be scoped to failed/indeterminate nodes according to idempotency and reconciliation rules, not simply restart the entire graph.

## 08 — Beyond one machine

A distributed scheduler adds more than remote threads. It must account for:
- worker identity and authentication;
- durable task assignment and expiring leases;
- fencing tokens against stale workers;
- transport retries and duplicate delivery;
- checkpoint and result provenance;
- clock skew and deadline interpretation;
- network partitions and worker loss;
- reconciliation of uncertain external effects;
- tenant isolation and resource quotas.

A local lease is not a distributed lock. A successful dispatch response does not prove a remote worker completed the task. Exactly-once execution is not guaranteed by a unique task ID or a local journal; idempotency and reconciliation remain necessary.

## 09 — Implementation map and limits

The current [WorkflowRunner](../../src/Execution.Workflow/WorkflowRunner.cs) executes a declared sequence. The [WorkflowExecutionCoordinator](../../src/Execution.Workflow/WorkflowExecutionCoordinator.cs) retains multiple run instances and supports per-run cancellation and cooperative timeout, but it does not schedule nodes within a graph or allocate a general worker pool.

The existing [scheduling and concurrency chapter](08-scheduling-and-concurrency.md) describes dependency categories and the first safe path toward parallel work. The central [Execution Theory](../EXECUTION_MODEL_THEORY.md) defines the shared model-neutral contract.

General dependency-graph execution, parallel workflow steps, worker pools, resource arbitration, distributed leases, remote scheduling, and cross-host recovery are not implemented. They should be added one model at a time, with deterministic tests for dependency admission, cycle rejection, concurrency bounds, cancellation, partial failure, and evidence provenance.

## Questions for the reader

1. Why is an ordered workflow not automatically a dependency graph?
2. Which dependency types can be satisfied by immutable evidence, and which require an exclusive lock?
3. Why should graph cycles be rejected before execution?
4. What does a scheduler need to know beyond the number of available threads?
5. Why can parallel execution change scientific or financial results even when all tasks receive the same inputs?
6. Why is a remote dispatch acknowledgement not a terminal task outcome?

## Exercise

Take the source-inspection / analysis / patch / CI / evidence / draft-PR example from [Chapter 08](08-scheduling-and-concurrency.md).

Create a dependency graph and assign resource claims. Identify the operations that may overlap, the operations that must serialize, and the exact evidence required before the next stage becomes eligible. Then simulate one worker timing out after it may have committed a remote side effect. State which outcome is known, which is indeterminate, and how the coordinator should reconcile before retry.

## Further reading

- [Execution Theory](../EXECUTION_MODEL_THEORY.md)
- [Chapter 08 — Scheduling, dependencies, and concurrency](08-scheduling-and-concurrency.md)
- [Chapter 11 — Asynchronous execution and work ownership](11-asynchronous-execution.md)
- [Workflow semantics](../WORKFLOW_SEMANTICS.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
