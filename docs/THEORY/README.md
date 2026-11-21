<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Execution Theory — Learning Path

The canonical cross-cutting model is [Execution Theory](../EXECUTION_MODEL_THEORY.md). It defines the vocabulary and invariants for execution models, engines, schedulers, hosts, synchronous and asynchronous progress, concurrency, parallelism, time, effects, evidence, and recovery.

The twelve chapters below develop the domain progressively. They are designed to teach the why and how, map theory to implementation, and clearly label the limits of current evidence.

## Chapter map

| Chapter | Domain question |
|---|---|
| [01 — Work as an explicitly coordinated process](01-work-as-coordinated-process.md) | Why must work have a definition, owner, lifecycle, and observable outcome? |
| [02 — Workflow definitions and execution semantics](02-workflow-definitions-and-execution.md) | How can declared work be validated and executed without ambient authority? |
| [03 — Tasks, leases, and lifecycle ownership](03-tasks-leases-and-lifecycle.md) | How do we assign bounded work, fence stale agents, and preserve history? |
| [04 — Capabilities and authority](04-capabilities-and-authority.md) | How do hosts grant narrowly scoped power and preserve human approval? |
| [05 — Evidence, verdicts, and repair decisions](05-evidence-and-repair.md) | What makes a result trustworthy enough to drive a bounded next action? |
| [06 — Persistence, replay, and recovery](06-persistence-and-recovery.md) | What can survive restart, and where do local journals stop providing guarantees? |
| [07 — Hosts, adapters, and external systems](07-hosts-and-adapters.md) | How do different hosts participate without owning execution semantics? |
| [08 — Scheduling, dependencies, and concurrency](08-scheduling-and-concurrency.md) | When may work overlap, what must remain ordered, and what does a scheduler owe? |
| [09 — Observability, audit, and reproducibility](09-observability-and-reproducibility.md) | How can an operator reconstruct what happened and distinguish evidence from presentation? |
| [10 — Security and failure containment](10-security-and-failure-containment.md) | How does execution fail safely when identity, authority, evidence, or external systems are uncertain? |
| [11 — Asynchronous execution and work ownership](11-asynchronous-execution.md) | How does work outlive its caller while retaining identity, cancellation, and ownership? |
| [12 — Parallel execution, graphs, and resource scheduling](12-parallel-execution-and-scheduling.md) | When may independent work run simultaneously, and how are resources and partial failures governed? |

## The execution dimensions

The chapter sequence is supported by the central theory's explicit distinctions:

- **Synchronous vs asynchronous:** whether the caller waits for completion is independent of whether the work runs in parallel.
- **Concurrency vs parallelism:** overlapping lifetimes do not prove simultaneous progress on separate execution resources. Chapter 12 develops dependency graphs, resource scheduling, partial failure, and distributed work.
- **Async work ownership:** asynchronous completion does not make child work disappear; Chapter 11 defines structured ownership, cancellation propagation, joining, and backpressure.
- **Sequential vs graph scheduling:** a declared order is not a dependency graph; parallel eligibility requires explicit dependency and resource semantics.
- **Periodic vs continuous:** a model's time semantics differ from the wall-clock duration used to run its engine.
- **Local vs distributed:** a local lease or journal does not imply cross-process exclusion, distributed consensus, or exactly-once external effects.
- **Observation vs evidence:** a rendered status or trace is not automatically proof of the underlying external event.

## Evidence discipline

Theory coverage is not implementation status. The current concrete workflow engine remains sequential. General dependency-graph scheduling, parallel workflow steps, general asynchronous child orchestration, real-time scheduling, distributed leases, cross-host scheduling, and universal checkpoint/replay remain future capabilities until each has a source-backed contract, tests, and exact-head CI evidence.

See [Domain and ownership](../DOMAIN.md), [Architecture](../ARCHITECTURE.md), and the [Roadmap](../ROADMAP.md) for current implementation boundaries. A passing test proves only the behavior and conditions it exercises. A fake adapter does not prove a live provider; a local journal does not prove distributed consistency; and a green build does not authorize merge, publication, or deployment.

## Neighboring domains

- [FSM_API](https://github.com/TrentBest/FSM_API) — finite-state behavior primitives.
- [FSM_COS](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS) — MicroBundle composition and RuntimeAssembly.
- [Physics](https://github.com/TrentBest/TheSingularityWorkshop.Physics) — physical models, assumptions, units, and experiments.
- [Learning](https://github.com/TrentBest/TheSingularityWorkshop.Learning) — problem-to-proof learning progression.
- [CiAi](https://github.com/TrentBest/TheSingularityWorkshop.CiAi) — CI evidence and deterministic verdicts.
- [HeadlessAi](https://github.com/TrentBest/TheSingularityWorkshop.HeadlessAi) — provider-neutral model invocation.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
