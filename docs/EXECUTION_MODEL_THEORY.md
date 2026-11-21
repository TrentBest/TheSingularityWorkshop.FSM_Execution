<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Execution Model Theory

**Execution is the act of causing a defined computation to progress under explicit semantics, bounded authority, and observable outcomes.**

This repository owns the host-neutral execution boundary for The Singularity Workshop. Its domain is not limited to workflows, jobs, scripts, or one scheduling strategy. It is the infrastructure through which a host can instantiate and run a declared execution model.

The phrase **anything executable** describes the breadth of the domain, not permission to execute arbitrary input. Every executable model must declare what its instructions mean, what capabilities it requires, how progress is scheduled, how effects are bounded, and how its outcomes are observed.

## 1. The central distinction: model versus engine

An execution model defines the rules of a computation. An execution engine realizes those rules.

A model answers:

- What is the unit of work?
- What makes work eligible to begin?
- What order or concurrency constraints apply?
- What state changes are legal?
- What counts as completion, failure, cancellation, or timeout?
- Which external effects may occur?
- What evidence must be produced?

An engine answers:

- How is a model instance admitted and validated?
- Which implementation is selected for its declared semantics?
- How are capabilities supplied and fenced?
- How is progress driven and bounded?
- How are lifecycle and outcomes retained?
- How is execution stopped, recovered, or handed off?

Execution must not confuse a model with a particular engine implementation. A sequential workflow runner is one engine for one model. It is not the definition of all execution.

## 2. The model-neutral execution envelope

Every model implementation should fit a common envelope without erasing its own semantics:

```text
ExecutionRequest
      |
      v
 model identity + version + definition
      |
      v
 validation and semantic preflight
      |
      v
 capability requirements + host grants
      |
      v
 execution plan / admitted instance
      |
      v
 model-specific progress engine
      |
      +---- lifecycle observations
      +---- bounded effects
      +---- trace and evidence
      |
      v
 terminal outcome or explicit handoff
```

The envelope standardizes admission, authority, lifecycle, observability, and outcome reporting. The model owns its instruction language, transition rules, dependency meaning, and completion criteria.

A model that cannot state these contracts is not ready to be executed by the shared runtime.

## 3. Execution models are plural

The same host-neutral envelope can support different kinds of computation. These are model families, not promises that every engine already exists in this repository.

| Model family | Fundamental unit | Progress rule | Typical Workshop relationship |
|---|---|---|---|
| Sequential workflow | Ordered step | Run next step after prior outcome | Current version-1 workflow runner |
| Dependency graph / dataflow | Node and dependency edge | A node becomes eligible when prerequisites satisfy its rule | CiAi pipelines, build graphs, scientific pipelines |
| Finite-state behavior | State, transition, context | An explicit update/event selects legal transitions | FSM_API supplies the state-machine primitive |
| Event-driven / reactive | Event and subscription | Matching events make handlers or transitions eligible | User I/O, services, interactive experiences |
| Fixed-step simulation | Model state and timestep | Advance by declared timestep and integration rules | Physics experiments and simulation hosts |
| Continuous / real-time process | Process and control interval | A scheduler supplies bounded execution opportunities | Device, rendering, and control hosts |
| Request/response operation | Typed request and response | Invoke a declared operation through a granted capability | FSM_REST and service adapters |
| Distributed / remote execution | Task, partition, and remote attempt | Dispatch through an explicit transport and reconcile evidence | Future multi-host execution |

These families may compose. A workflow step may launch a simulation; a state machine may govern a service; a dependency graph may build the artifacts consumed by a runtime. Composition does not make their semantics interchangeable.

## 4. What the neighboring repositories teach us

Execution should embrace proven Workshop boundaries rather than recreate them.

- **FSM_API** demonstrates a compact behavioral primitive: definitions, instances, contexts, transitions, and an explicit update path. Execution may host an FSM-driven model, but it must not become a second FSM implementation or assume every model is an FSM.
- **FSM_COS** demonstrates the value of a distinct composition phase. It resolves a requested MicroBundle dependency closure and returns a stable RuntimeAssembly. Composition prepares a system; Execution governs the progress of a computation. A RuntimeAssembly is not an execution request or result.
- **Physics** demonstrates evidence-bounded modeling. A model declares quantities, units, assumptions, valid ranges, and limitations; experiments record inputs and sampled states. Execution should carry model identity, run configuration, trace, and provenance without claiming that a successful run proves physical truth.
- **Learning** demonstrates the problem-to-proof progression: state a problem, model changing facts, express rules, run a deterministic trace, render the result, and compare against explicit expectations. Execution should make this evidence chain first-class while keeping renderers observational.
- **CiAi** demonstrates why authority and evidence are separate. A model response, valid definition, or green status is not permission to mutate a repository. Host-issued capabilities and exact-scope gates remain authoritative.

These are architectural lessons, not dependencies to add indiscriminately. Reuse a repository's actual contract when a tested integration needs it.

## 5. The execution contract has independent dimensions

A model descriptor should declare at least the following dimensions. A model may choose a supported value or explicitly state that a dimension is not applicable; silence must not be interpreted as a safe default.

1. **Identity and version** — stable model identifier, schema version, implementation compatibility.
2. **Definition** — immutable, inspectable data describing the requested computation.
3. **Semantics** — ordering, dependencies, transition rules, determinism, and concurrency.
4. **Admission** — validation and preflight checks completed before side effects.
5. **Capabilities** — named operations required, their scope, and the host authority that grants them.
6. **Progress** — who advances the model: runner, scheduler, event source, clock, or remote worker.
7. **Lifecycle** — accepted, ready, running, suspended, cancelling, succeeded, failed, cancelled, timed out, or indeterminate, where applicable.
8. **Effects and recovery** — idempotency, checkpoints, retries, compensation, replay, and uncertainty after interruption.
9. **Evidence** — trace, timestamps, input/configuration identity, implementation identity, diagnostics, and terminal outcome.

Not every model needs every lifecycle state or recovery feature. The model must define its own valid subset and the engine must not invent semantics on its behalf.

## 6. Authority is not part of the definition

An execution definition is data. It may request a capability, but it cannot grant itself that capability.

The host is the authority boundary. It supplies explicit capability implementations and grants them within a scope that can include:

- exact execution instance and correlation;
- repository, branch, base commit, candidate commit, and permitted paths;
- resource, tenant, environment, or destination;
- expiry, concurrency budget, and invocation budget;
- cancellation and revocation behavior.

The engine validates that each requested operation maps to a currently granted capability before execution. A model cannot broaden a grant by changing its own definition or returning a persuasive result.

No model-authored expression, shell command, script, provider response, or serialized object becomes executable merely because it parses.

## 7. Determinism, replay, and the evidence boundary

Determinism is a declared property, not a universal guarantee. A pure calculation with fixed inputs may be repeatable. An event-driven service, network call, or physical simulation may depend on time, environment, external observations, or nondeterministic scheduling.

Every model should identify which parts are:

- deterministic and replayable;
- deterministic only with recorded external inputs;
- externally effectful and not safely replayable;
- uncertain after interruption.

A trace records what the engine observed. It does not automatically prove that an external side effect happened exactly once, that a remote system accepted a request, or that a scientific model is accurate. Those claims require domain-specific evidence and verification.

Rendering is downstream observation. An SVG, chart, GUI, or visualization may explain a trace, but it must not become the source of execution state or proof.

## 8. Composition is not execution

The Workshop has distinct boundaries:

```text
FSM_API       behavioral primitives
MicroBundle  reusable capability/content/behavior
FSM_COS       dependency resolution and composition
FSM_Execution admission, authority, progress, lifecycle, outcomes
Host          environment, concrete capabilities, and presentation
```

A host may ask FSM_COS to assemble a RuntimeAssembly, then submit an appropriate executable model to FSM_Execution. This does not mean every assembly is executable, nor that every execution requires MicroBundles. The integration must declare how the assembly is consumed and what the model's semantics are.

Likewise, a host-scheduled FSM_API update, a workflow step, a physics timestep, and a CI job are not the same operation. They may share lower-level lifecycle or observation contracts only when a source-backed adapter and tests establish that equivalence.

## 9. Failure is a first-class outcome

An engine must not report success when required work did not run or when its result is unknown.

At minimum, distinguish:

- **Invalid** — definition or preflight failed before execution.
- **Rejected** — host policy or capability admission denied the request.
- **Succeeded** — the model's declared completion condition was satisfied.
- **Failed** — execution observed a terminal error.
- **Cancelled** — cancellation was observed and its semantics are known.
- **Timed out** — the declared time budget expired; this does not prove that an uncooperative external operation stopped.
- **Indeterminate** — execution crossed an effect boundary but cannot establish the external outcome.

A model may add domain-specific outcomes. It must not collapse indeterminate into failure or success merely to simplify a caller.

## 10. A disciplined path from the current implementation

The existing sequential workflow runtime is a useful first concrete model. Preserve its validated-before-side-effects behavior, explicit host handler registry, structured outcomes, cooperative cancellation, and retained lifecycle.

Extend by proving one model at a time:

1. Formalize a versioned model descriptor and execution request without breaking the current workflow contract.
2. Extract the common admission, capability, lifecycle, and evidence seams only where more than one model needs them.
3. Keep the sequential workflow engine as the reference implementation.
4. Add a second model with genuinely different semantics—preferably a dependency-graph model or an FSM_API-backed model—to prove the envelope is model-neutral.
5. Add bounded scheduling, persistence, and recovery only with explicit semantics and failure-injection tests.
6. Add remote execution only after transport, identity, authority, replay, and indeterminate-outcome contracts are proven.

Do not build a speculative universal interpreter. Build a small, explicit execution kernel with model-specific engines and verifiable boundaries.

---

## Design invariant

**The model defines what execution means. The host grants what execution may do. FSM_Execution governs how an admitted instance progresses and what can truthfully be said about its outcome.**
