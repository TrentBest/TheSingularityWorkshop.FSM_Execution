<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Execution Theory

**Execution is the controlled progression of a defined computation through time, under explicit semantics, bounded authority, and observable evidence.**

FSM_Execution owns the host-neutral boundary that admits an execution model, selects or hosts an appropriate engine, governs its lifecycle, and reports what can truthfully be known about its outcome. It is not synonymous with a workflow runner, scheduler, thread pool, process launcher, or universal interpreter.

The domain is intentionally broad: anything may become an execution model when its meaning, inputs, progress rules, effects, and outcomes can be explicitly described. That breadth is not permission to execute arbitrary input. A definition is data; it does not grant itself authority or turn text into executable code.

## 1. The fundamental vocabulary

These terms describe different layers and must not be collapsed into one abstraction.

| Term | Meaning | Question answered |
|---|---|---|
| **Execution model** | The rules that define a class of computation | What does this computation mean? |
| **Definition** | A versioned declaration of one requested model instance | What exactly is requested? |
| **Engine** | An implementation that realizes a model's semantics | How is this model progressed? |
| **Scheduler** | A policy/mechanism that decides when eligible work receives execution capacity | When may work run? |
| **Executor** | A component that performs a bounded unit of admitted work | Who performs this operation? |
| **Host** | The environment that composes engines and supplies concrete capabilities | Where does it run, and what may it access? |
| **Execution instance** | One admitted attempt to realize a definition | Which run and attempt are being observed? |
| **Trace/evidence** | Immutable observations and provenance associated with an instance | What happened, and what supports that claim? |

A workflow is one model. A dependency graph is another. A finite-state behavior, simulation, event processor, request/response operation, and distributed task are also models. A thread pool is not a model: it is one possible resource mechanism used by an engine or scheduler.

The model defines semantics. The engine implements them. The scheduler allocates opportunities to progress. The host supplies environmental authority. FSM_Execution governs the admitted instance and its outcome.

## 2. Model versus engine

An execution model defines:

- the fundamental unit of work;
- valid inputs and outputs;
- dependencies and ordering constraints;
- state transitions and completion conditions;
- whether and how work may overlap;
- time, clock, and deadline semantics;
- effects, idempotency, and recovery rules;
- required evidence and failure meanings.

An engine answers:

- how the definition is validated and admitted;
- how model semantics are interpreted;
- how eligible work is advanced;
- how resources and capabilities are obtained;
- how cancellation, faults, and timeouts are handled;
- how observations and outcomes are retained.

One model may have several engines (for example, an in-process and a remote engine) if they preserve the same declared semantics. One engine may host multiple model families only when it has explicit model adapters; it must not quietly reinterpret one model as another.

A sequential workflow runner is an engine for an ordered-step workflow. It is not the definition of execution.

## 3. The shared execution envelope

Every supported model should enter and leave through a common envelope without flattening its semantics.

```text
ExecutionRequest
  -> identity + model type/version + immutable definition
  -> structural and semantic validation
  -> preflight + resource/capability requirements
  -> host authorization and admission decision
  -> admitted execution instance + correlation identity
  -> model-specific engine <-> scheduler / clock / event source
  -> lifecycle observations + bounded effects + trace/evidence
  -> terminal outcome OR explicit handoff / indeterminate state
```

The shared envelope owns identity, admission, authority checks, lifecycle, cancellation request, observation, and outcome reporting. The model owns the meaning of its units, dependency rules, legal transitions, and completion criteria. The host owns concrete credentials, process/network/device access, and policy decisions.

A descriptor should declare its model identifier, schema and semantic version, engine compatibility, definition, determinism class, progress driver, concurrency constraints, resource claims, capability requirements, effect/replay policy, and outcome vocabulary. Unsupported or unspecified semantics are rejected or explicitly treated as unknown; they are not filled in with permissive defaults.

## 4. Families of execution models

| Family | Unit of work | Progress driver | Characteristic semantics |
|---|---|---|---|
| Sequential workflow | Ordered step | Workflow runner | Declared order; next step follows prior outcome |
| Dependency graph / dataflow | Node and edge | Graph scheduler | Eligibility follows prerequisite results |
| FSM behavior | State, transition, context | Update loop or event | Legal state transitions and guards |
| Event-driven / reactive | Event and subscription | Event source / dispatcher | Events make handlers or transitions eligible |
| Request/response | Typed request | Invocation engine | One bounded operation and response |
| Fixed-step simulation | State and timestep | Simulation clock | Discrete advancement by declared step size |
| Continuous / real-time | Process and control interval | Clock / real-time scheduler | Timing, deadlines, and missed-frame policy |
| Stream processing | Event/window/partition | Stream engine | Ordering, watermark, backpressure, checkpoint |
| Batch / map-reduce | Partition or batch | Worker scheduler | Partitioning, aggregation, retry semantics |
| Distributed task | Task, partition, remote attempt | Coordinator + remote workers | Transport, leases, reconciliation, partial failure |
| Interactive experience | Input/event + evolving state | Host input loop / simulation | Responsiveness, pause/resume, deterministic input capture |

These are families, not claims that every engine is implemented here. A composed system may combine them: a graph node may invoke a workflow; a workflow step may run a simulation; an FSM may govern an event processor; a batch job may dispatch distributed tasks. Composition preserves the boundary between each model's semantics.

## 5. Time: synchronous, asynchronous, periodic, and continuous

Time is part of execution semantics, not a decoration on an API.

**Synchronous execution** means the caller's operation does not complete until the operation's declared completion point is reached. It does not necessarily mean the CPU is busy the whole time. A synchronous call may block while waiting for I/O or a lock.

**Asynchronous execution** means the caller can yield control while the operation remains incomplete and later observes completion, failure, or cancellation. Async is a control-flow and resource-management property; it does not imply simultaneous CPU execution or parallelism. An asynchronous operation may be a single operation waiting on one network response.

**Concurrent execution** means multiple operations have overlapping lifetimes or are in progress during the same interval. They may take turns on one CPU, interleave at awaits, or run on different resources.

**Parallel execution** means multiple operations are actively making progress at the same instant, typically on separate cores, devices, or machines. Parallelism is one possible implementation of concurrency, not a synonym for it.

**Periodic execution** advances or samples a model according to a declared clock or interval. A fixed-step simulation may execute synchronously inside each tick while the overall simulation is driven asynchronously by a host loop.

**Continuous-time modeling** describes behavior as evolving over a continuous time variable. Its numerical engine may still approximate that behavior through discrete integration steps or event times. The model's mathematical time must not be confused with wall-clock execution duration.

These dimensions are independent. An API may be asynchronous while its engine serializes all work. A synchronous call may dispatch parallel work and block until it joins. A periodic simulation may use parallel kernels within each step but must synchronize at defined boundaries. A remote operation may be asynchronous from the caller's perspective without exposing its internal scheduling.

### Clock domains

An execution model must say which clock governs each time-sensitive rule:

- **Wall clock**: calendar/UTC time for expiry, audit timestamps, and external deadlines.
- **Monotonic clock**: elapsed duration and timeout measurement; it must not move backward when wall time is adjusted.
- **Model clock**: simulated or logical time advanced by model rules.
- **Event/sequence time**: ordering established by source sequence, offsets, or logical clocks when physical timestamps are insufficient.

Never compare timestamps from unrelated clock domains as if they were interchangeable. Persisted deadlines need an explicit clock/expiry interpretation. Replay of a simulation should use recorded model time and inputs, not the current wall clock.

## 6. Concurrency and parallelism

Concurrency changes the set of possible interleavings. Parallelism changes which operations can physically overlap. Both can change observable behavior and external effects.

A model must declare:

1. which units may be eligible simultaneously;
2. which dependencies must complete first;
3. which resource claims are exclusive, shared, or bounded;
4. whether output order is significant;
5. how shared state is isolated or synchronized;
6. how failure and cancellation propagate;
7. how much concurrency the host may allocate;
8. what fairness, starvation, and backpressure rules apply.

### Dependency types

At minimum, distinguish:

- **Data dependency**: a consumer requires a value produced by another unit.
- **Ordering dependency**: one operation must happen before another even if no data is passed.
- **Resource exclusion**: operations contend for a mutable or capacity-limited resource.
- **Evidence dependency**: an action is eligible only after a particular result is validated.
- **Lifecycle dependency**: a task, incident, or instance must be in an allowed state.
- **Human/authority gate**: execution waits for a host-authenticated decision.

A graph scheduler must reject missing dependencies and cycles before effects begin, define how failed prerequisites affect descendants, and prevent a changed graph from silently altering an in-flight instance.

### Safe parallel admission

A conservative scheduler admits work only when all prerequisites are satisfied and its resource claims are compatible with active work. Read-only source inspections may overlap when their scopes are immutable and independent. Two writes to the same candidate branch generally require serialization or an explicit merge protocol. A CI result must remain bound to the exact candidate SHA it tested.

Parallel execution must not weaken task lease fencing, capability scope, evidence provenance, or human approval. Concurrency limits must be explicit at several levels: per instance, per host, per tenant/resource, and globally where necessary. A worker count alone is not a complete resource policy.

### Cancellation and joining

Cancellation is a request to stop, not proof that work stopped. An engine must record which units observed cancellation, which completed before it, which were skipped, and which could not be stopped. Before reporting a terminal aggregate outcome, the model must define whether active child work is joined, detached, or handed off. Detached work needs an identity and owner; it cannot disappear from accounting.

## 7. Scheduling and resource governance

A scheduler selects eligible work and allocates finite resources. It may be centralized, cooperative, event-driven, work-stealing, device-specific, or distributed. The policy should be separate from the model's meaning.

A scheduler contract should define:

- eligibility and priority;
- capacity and admission limits;
- fairness and starvation handling;
- resource acquisition and release;
- preemption or non-preemption;
- deadline and missed-deadline behavior;
- backpressure and queue bounds;
- cancellation and shutdown;
- observability of queued, ready, running, suspended, and blocked work.

Do not introduce a universal scheduler before a second genuinely different model demonstrates the shared need. A sequential workflow engine does not need a graph scheduler merely because the future domain includes graphs. A real-time simulation scheduler and a remote job dispatcher may share admission or lifecycle contracts while retaining very different timing and failure semantics.

## 8. Effects, retries, idempotency, and recovery

Execution may be pure computation, local mutation, or an external effect. The engine must know which boundary it crossed.

A pure calculation can often be repeated from the same inputs. A file write may be repeatable only with a defined replacement/commit protocol. A network request may have reached the remote system even if the client timed out. A physical actuator may have moved even if acknowledgement was lost.

Every effectful operation should define an idempotency identity or reconciliation method. Retry policy must distinguish:
- known-not-started;
- known-failed-with-no-effect;
- completed;
- possibly-effectful / indeterminate.

A timeout does not prove an operation stopped. Cancellation does not imply rollback. Compensation is a new domain operation, not a universal undo button. Exactly-once external effects cannot be claimed merely because a local journal records one invocation.

## 9. Lifecycle and outcome semantics

A shared lifecycle vocabulary may include:

`Created -> Validating -> Admitted -> Ready -> Running <-> Suspended -> {Succeeded, Failed, Cancelled, TimedOut, Indeterminate, Rejected}`

This is an illustrative envelope, not a universal state machine. A model may add states or use a smaller valid subset. An execution instance must have stable identity, attempt identity, correlation identity, timestamps with clock provenance, and a defined owner for every active child operation.

Outcomes must be truthful:

- **Invalid**: the definition or semantic preflight is unacceptable.
- **Rejected**: host policy or capability admission denied execution.
- **Succeeded**: the model's declared completion condition is satisfied.
- **Failed**: a terminal error is known.
- **Cancelled**: cancellation was observed and the model's cancellation condition is satisfied.
- **Timed out**: a declared deadline expired; external work may still be running.
- **Indeterminate**: an effect may have occurred but cannot yet be established.
- **Suspended / handed off**: progress is intentionally paused or delegated with explicit ownership and resume conditions.

Do not collapse timeout into cancellation, rejection into failure, or indeterminate into success/failure for caller convenience.

## 10. Authority, isolation, and host boundaries

The model may request named capabilities. Only the host can grant them. The grant should be scoped to the exact execution instance, task, resource, repository/branch/SHA/path, destination, budget, expiry, and permitted action. It must be revocable and revalidated at the last responsible moment before a side effect.

A valid definition, successful model response, schema-valid tool call, green CI result, or agreement among multiple models is not authorization. Model output is untrusted data. No serialized expression or shell-like string becomes executable because it parses.

The host owns credentials, process and device access, networking, persistence locations, and external transport. FSM_Execution owns the host-neutral contract and must not import WPF, browser APIs, provider SDKs, or ambient machine privileges into the core.

## 11. Determinism, replay, and evidence

Determinism is a declared property, not a universal promise. Classify each model or operation as:
- deterministic from fixed inputs;
- deterministic only when external observations and ordering are recorded;
- nondeterministic by design;
- externally effectful and not safely replayable;
- uncertain after interruption.

A useful trace records definition/version, engine/version, exact inputs, model clock, external observations, scheduling decisions where relevant, resource/capability identities (without secrets), child identities, outputs, diagnostics, and outcome provenance.

A trace records what the engine observed. It does not automatically prove an external side effect occurred exactly once, a remote worker accepted a task, or a scientific model is physically accurate. A renderer, SVG, chart, or GUI is an observation of evidence, not the source of execution state or proof.

## 12. Composition across the Workshop

The ownership boundaries are distinct:

```text
FSM_API        finite-state behavior primitives
MicroBundles   reusable capabilities, content, and behavior
FSM_COS        dependency resolution and composition -> RuntimeAssembly
FSM_Execution  admission, scheduling contracts, lifecycle, outcomes
Host           environment, concrete capabilities, policy, presentation
```

FSM_COS composes a runtime assembly; it does not implicitly execute it. FSM_Execution governs an admitted execution model; it does not recompose MicroBundles. A host may request composition and then submit an explicitly defined model that consumes the assembly. The adapter must state which parts of the assembly are executable and how their semantics are defined.

Physics contributes model assumptions, units, constraints, and experimental evidence. Learning contributes problem-to-proof progression. CiAi contributes exact-run evidence and deterministic verdict boundaries. HeadlessAi contributes model invocation, not execution authority. GUI and Renderer present traces and state without becoming the authoritative loop.

## 13. A model-neutral contract checklist

Before a model is admitted as a supported FSM_Execution model, answer all of the following:

1. What is its stable identity and version?
2. What is the immutable definition and schema?
3. What do its instructions mean?
4. What are its units of work and dependency types?
5. Is progress synchronous, asynchronous, event-driven, periodic, continuous, or remote?
6. What clock domains and deadlines apply?
7. Which work may be concurrent or parallel, and why?
8. What resource claims and bounds apply?
9. What capabilities are required, and which host grants them?
10. What are its legal lifecycle states and completion conditions?
11. How are cancellation, timeout, shutdown, and child work handled?
12. Which effects are pure, idempotent, compensatable, or indeterminate?
13. What can be replayed, and which observations must be captured?
14. What evidence supports each reported outcome?
15. What happens when the host, scheduler, process, network, or persistence layer fails?

A missing answer is a design gap, not an invitation for the engine to guess.

## 14. An evidence-led implementation path

The current sequential workflow implementation is the reference model. Preserve its validated-before-side-effects behavior, host-registered handlers, ordered execution, structured outcomes, cooperative cancellation, and retained lifecycle.

Advance the domain in this order:

1. Formalize a versioned model descriptor and execution request around the existing workflow contract.
2. Separate common admission, capability, lifecycle, trace, and outcome concerns only where multiple models demonstrate the same need.
3. Specify asynchronous completion and child-operation ownership without equating async with parallel.
4. Prove a dependency-graph model with bounded concurrency, explicit dependencies, resource claims, cancellation, and deterministic tests.
5. Prove an FSM_API-backed behavior model or fixed-step simulation model to demonstrate a distinct progress driver.
6. Add durable checkpoints and recovery only after effect and replay semantics are explicit.
7. Add remote/distributed execution only after identity, transport trust, leases, partial failure, reconciliation, and indeterminate outcomes are tested.

Do not build a speculative universal interpreter or scheduler. Build a small execution envelope and model-specific engines. Promote an abstraction only when at least two real models need it and a contract test demonstrates the shared invariant.

## 15. What is implemented versus conceptual

The repository currently implements a sequential version-1 workflow runner and retained in-process workflow lifecycle, alongside separate governed work-task lifecycle, lease, capability, evidence, persistence, and host-adapter components at their documented maturity levels.

The following remain conceptual or future work unless a specific source, test, and exact-head CI run prove otherwise: general dependency-graph scheduling, parallel workflow steps, bounded worker pools, general asynchronous child orchestration, real-time scheduling, distributed leases, cross-host scheduling, exactly-once external effects, universal checkpoint/replay, and a universal execution manifest.

Do not infer implementation from this theory. Each capability must be labelled with its evidence level in the roadmap and domain documentation.

---

## Design invariant

**The model defines what execution means. The engine realizes that model. The scheduler decides when eligible work receives resources. The host grants what execution may do. FSM_Execution governs the admitted instance and what can truthfully be said about its outcome.**

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
