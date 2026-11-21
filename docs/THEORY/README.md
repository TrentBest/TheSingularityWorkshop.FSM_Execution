<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Execution Theory — Learning Path

The canonical theory is [Execution Theory](../EXECUTION_MODEL_THEORY.md). It defines the vocabulary and invariants for execution models, engines, schedulers, hosts, asynchronous work, concurrency, parallelism, time, effects, evidence, and recovery.

## Reader journey

Read the central theory in order. It is intentionally model-neutral: the sequential workflow runner is the first concrete model, not the limit of the domain.

| Study area | Core question | Theory sections |
|---|---|---|
| Foundations | What is a model, definition, engine, scheduler, host, and execution instance? | 1–3 |
| Model families | What different kinds of computation can be executed? | 4 |
| Time and progress | What do synchronous, asynchronous, periodic, and continuous mean? | 5 |
| Concurrency | What is concurrent, what is parallel, and what can safely overlap? | 6–7 |
| Effects and lifecycle | How are cancellation, timeout, retries, recovery, and outcomes represented truthfully? | 8–9 |
| Authority and evidence | Who grants capabilities, and what proves what happened? | 10–11 |
| Composition | How does execution relate to FSM_API, FSM_COS, MicroBundles, Physics, Learning, CiAi, and hosts? | 12 |
| Engineering discipline | What must a model specify, and how should new engines be proven? | 13–15 |

## Evidence discipline

The theory describes the complete domain and target contracts. It does not claim that every model or engine exists today. Current implementation status is recorded in [Domain and ownership](../DOMAIN.md), [Architecture](../ARCHITECTURE.md), and the [Roadmap](../ROADMAP.md).

A conceptual model becomes an implemented capability only when its contracts, source, tests, and exact-head CI evidence exist. A passing test proves only the behavior it exercises. A green build does not prove distributed correctness, live external integration, or authorization to publish or deploy.

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
