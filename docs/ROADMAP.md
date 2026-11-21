<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Execution Roadmap and Readiness

**Readiness: early implementation; no current percentage is asserted.** The previous 35% estimate predates the governed-work lifecycle and is not comparable to the expanded scope. The version-1 workflow runtime passed its development CI gate; the governed-work feature branch passed [feature-branch CI run #276](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/actions/runs/41170698768) on exact head `230907aad4a7a75aed589d61f4c43aa9ea2a1eb4`, including workflow timeout/cancellation tests. This is not a release-readiness claim. The signed host-issued CODE RED recovery receipt boundary passed [feature-branch CI run #294](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/actions/runs/41172622514) on exact head 0ef31369daa115fb652e46b79d208ea66ab8fc83.

## Milestones

### M0 — Repository and documentation bootstrap
- [x] Establish repository identity and MIT license.
- [x] State intended boundaries and distinguish target from implementation.
- [x] Add documentation navigation, Gallery, and roadmap.
- [ ] Verify repository branch policy and protect intended development flow.

### M1 — Define the domain from real Workshop contracts
- [x] Inspect FSM_API public builder/context APIs and its CI workflow.
- [x] Inspect FSM_COS composition contract and runtime boundary.
- [x] Inspect the FSM_Serialization representation boundary.
- [x] Inspect ProtocolAi/GrammarAi responsibility boundaries and AnyApp's host path at README/project/source level.
- [x] Record an initial Execution domain and ownership proposal in [DOMAIN.md](DOMAIN.md).
- [x] Trace representative WebPage Hub contracts, callers, and tests; record the non-equivalence and safe host-adapter seam in [HUB_INTEGRATION_BOUNDARY.md](HUB_INTEGRATION_BOUNDARY.md).
- [ ] Record final source-backed contract decisions and source-derived UML.

### M2 — First executable workflow
- [x] Add a minimal buildable .NET 8 probe project.
- [x] Define a versioned in-memory workflow definition contract (persisted format remains open).
- [x] Document version-1 execution semantics in [WORKFLOW_SEMANTICS.md](WORKFLOW_SEMANTICS.md).
- [x] Execute and assert a deterministic state transition through the actual published FSM_API API in a probe.
- [x] Demonstrate a deterministic terminal state in the API probe.
- [x] Add and verify a deterministic multi-state fox/sheepdog scenario with explicit state assertions.
- [x] Add invalid-definition, handled-failure, ordering, cancellation, and handler-registration tests.
- [x] Produce structured step and workflow outcomes distinct from console output and host exit codes.
- [x] Add retained in-process run lifecycle: submit, start once, observe snapshots, cancel, and async disposal.
- [x] Verify coordinator lifecycle tests in development CI run #84.

### M3 — CI proof
- [x] Run the FSM_API probe in GitHub Actions; the manifest-driven workflow itself remains pending.
- [x] Verify CI exit-code mapping and upload a probe-output artifact.
- [x] Compare deterministic output from two fresh basic-probe processes.
- [x] Run the fox/sheepdog scenario in two fresh processes and compare TSV and SVG evidence byte-for-byte.
- [x] Prove cooperative workflow timeout outcomes and distinguish timeout from caller-requested cancellation in feature-branch CI run #276.
- [ ] Harden permissions for untrusted pull requests.
- [ ] Publish workflow logs and a concise evidence summary.

### M4 — Living documentation and optional article production
- [ ] Align README and existing docs with the FSM_COS reader journey, moniker, badge, visual, and footer standards.
- [ ] Generate diagrams from real source/runtime evidence.
- [ ] Include stable type identity, source spans, and provenance.
- [ ] Add stale-output verification and a focused change test.
- [ ] Add Gallery recipes linked to successful runs.
- [ ] Add an opt-in article build that selects real source excerpts and records repository revision/path/line range.
- [ ] Generate high-contrast, readable code-excerpt images with equivalent selectable text and accessible descriptions.
- [ ] Produce a local/CI preview artifact without requiring publishing credentials.
- [ ] Define a publisher adapter only after selecting a destination; keep actual publication disabled unless explicitly approved.

### M5 — Host and language expansion
- [ ] Evaluate AnyApp/WebApp as hosts only after the CI proof works.
- [ ] Evaluate ProtocolAi/GrammarAi adapters with explicit contracts.
- [ ] Add additional languages only when the first cross-boundary proof is testable.

### M6 — Governed repository-work lifecycle
The completed items below are implemented on PR #10's feature branch and are not yet part of `development` until the owner reviews and merges that PR.
- [x] Add a versioned, repository/issue/SHA-bound work-task envelope with bounded identifiers, path scope, and attempt budget.
- [x] Add validated lifecycle transitions, expiring agent leases, fencing tokens, and sequenced audit history.
- [x] Add append-only local task, CODE RED incident, and CI repair-attempt journals with restart replay and fail-closed corruption handling.
- [x] Add host-issued, short-lived capabilities scoped to task, lease, branch, candidate SHA, paths, and explicit actions.
- [x] Add credential-free CODE RED incident records and validated active/cleared notice projections.
- [x] Require a short-lived, signed host approval receipt for CODE RED recovery, bound to the active incident sequence and exact task-event sequence; persist the complete signed receipt in the task audit.
- [x] Add exact-CI-attempt provenance checks and bounded deterministic repair decisions.
- [x] Add a concrete `Execution.HeadlessAiHost` adapter that maps Execution turns to HeadlessAi's immutable request/executor without moving lifecycle or authorization into HeadlessAi; build it against pinned HeadlessAi source in CI run #337.
- [x] Add `Execution.HeadlessAiHost.Console` as a manual host composition root and compile it in CI run #352.
- [ ] Provision host-managed provider credentials and execute one real provider turn through the manual smoke runner.
- [ ] Connect CiAi's exact-run evidence packet/outbox and bounded rerun request/ledger through trusted host adapters.
- [ ] Bridge the task lifecycle into the existing WorkflowExecutionCoordinator/WorkflowRunner; do not create a competing runner.
- [ ] Prove the red → evidence → bounded repair → green → draft-PR path with deterministic fake-provider/fake-GitHub tests.
- [ ] Complete the first integrated MyVR pilot and preserve human review/merge authority.

## Execution theory foundation

- [x] Define model, definition, engine, scheduler, executor, host, instance, and trace/evidence as distinct concepts.
- [x] Cover sequential, graph/dataflow, FSM, event/reactive, request/response, simulation, stream, batch, interactive, and distributed model families.
- [x] Distinguish synchronous, asynchronous, concurrent, parallel, periodic, and continuous/model-time execution.
- [x] Define clock domains, dependencies, resource claims, bounded scheduling, cancellation/join, idempotency, retries, recovery, and indeterminate effects.
- [x] Establish authority, evidence, replay, composition, and outcome invariants.
- [x] Define the canonical [Execution Theory](EXECUTION_MODEL_THEORY.md) and build the twelve-chapter [Execution Theory Learning Path](THEORY/README.md), including dedicated async and parallel scheduling chapters.

**Theory status is not implementation status.** The current concrete workflow engine remains sequential. General graph scheduling, parallel workflow steps, general asynchronous child orchestration, real-time scheduling, distributed leases, cross-host scheduling, and universal checkpoint/replay remain future capabilities until each has a source-backed contract, tests, and exact-head CI evidence.

## Active integration queue

The following draft PRs are the current implementation queue. Grouping is by domain responsibility, not an assertion that every branch is ready to merge.

### Runtime and task lifecycle
- [PR #29 — FSM_API workflow lifecycle](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/29) — foundational workflow-run FSM integration.
- [PR #32 — FSM_API task lifecycle + expired-lease fence](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/32) — explicitly stacked on PR #29's branch and already contains PR #30's stricter CODE RED/ReviewReady lease-reclaim guard and regression test. PR #30 is closed as superseded.
- [PR #34 — capability lifecycle fence + ReviewReady handoff](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/34) — invalidates grants after task events and permits only a draft-PR capability after evidence-derived ReviewReady. The unique PR #25 implementation/tests were ported here; PR #25 is closed as superseded.
- [PR #18 — host-owned CODE RED approval](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/18) — selected Option B ownership boundary.

### Host, repository, and Hub boundaries
- [PR #27 — GitHub host adapter semantics](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/27) — adapter contract and operation meaning.
- [PR #26 — capability-gated repository operations](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/26) — host-side repository operation boundary.
- [PR #40 — exact-SHA AgentOnboarding admission](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/40) — integrates the admission donor and PR #26 host operation work; current exact-head CI passes, but live trusted dispatch remains unverified.
- [PR #19 — host-facing Hub execution contract](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/19) — Hub command/query boundary.
- [PR #31 — Autonomous Development status projection](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/31) — read-only operational projection.

### CiAi evidence and artifact transport
- [PR #15 — CiAi ingress, pinned transport, and provenance-bound report consumer](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/15) — contains the four notice-ingress/retrieval components and tests from PR #14, the adapter-boundary document, report consumer, host composition, and repair feedback.
- [PR #16 — DNS-pinned HTTPS transport](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/16) — constrained artifact transport. PR #14 is closed as superseded by PR #15.

### File-overlap and integration gates

The following PRs cannot be treated as independent, clean merges because they change overlapping source/test surfaces:

- **Workflow FSM + identity rename:** PR #29 changes the workflow coordinator and adds the FSM_API lifecycle adapter; PR #43 renames the same workflow projects and coordinator surface. Reconcile the FSM_API lifecycle adapter against the Execution paths and timeout implementation before integration.
- **Task lifecycle stack:** PR #32 and PR #18 change `WorkTaskCoordinator` and/or `PersistentWorkTaskCoordinator` / `JsonlWorkTaskStore`. PR #32 already carries PR #30's expired-lease reclaim guard. Integrate the FSM_API-backed task state, host-owned CODE RED approval, and lease recovery as one coherent lifecycle rather than merging these independently.
  - **Authority constraint:** PR #32 still accepts and re-authenticates signed recovery receipts; PR #18 routes recovery through a host-owned `ICodeRedRecoveryApprovalGate` and records the approved incident/task sequence. The selected Option B makes the host gate authoritative. The combined implementation must retain old receipt fields only for legacy journal deserialization, not as a new authorization mechanism, while preserving FSM_API transition validation and the incident/task sequence recheck.
- **Capability policy:** PR #34 now combines the former PR #25 ReviewReady gate with lifecycle event-sequence invalidation. Preserve both invariants: a `ReviewReady` task can receive only a draft-PR handoff capability, and any later lifecycle event invalidates an earlier grant. PR #25 is closed and its donor branch is safe for the cleanup batch.
- **Repository host integration:** PR #40 declares that it integrates the PR #26 host operation boundary while preserving PR #26's review history. Compare the exact source/test trees before choosing one canonical implementation; PR #40's passing ordinary CI does not exercise live trusted dispatch.
- **CiAi evidence pipeline:** PR #14 defines notice/ingress/retrieval contracts, PR #16 supplies constrained artifact transport, and PR #15 consumes provenance-bound reports while selectively porting PR #14 work. Review these as one end-to-end evidence chain, not as three unrelated features.

These overlap notes are a merge-planning aid, not permission to merge. Preserve each PR's exact-head evidence and re-run CI after resolving conflicts or porting changes to the Execution identity.

### Identity and domain theory
- [PR #43 — FSM_Execution identity and execution theory](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Execution/pull/43) — current canonical rename, 12-chapter theory, workflow timeout semantics, and identity regression guard.

All 12 remaining open PR heads passed their recorded repository workflow during the 2026-11-21 audit; PR #25's capability gate was ported into PR #34, PR #14's ingress work/docs into PR #15, and PR #30's lease-reclaim guard into PR #32. All three donor PRs are closed. A green run is bounded evidence for that exact SHA; it does not establish that live privileged paths ran, that the PR is integrated, or that the branch is ready to merge.

**Integration caution:** the 11 functional/design PR branches still use historical Foundry-era paths and namespaces. Do not merge them blindly across the identity migration. Reconcile dependencies and decide whether to integrate functional work before the rename or port/rebase each candidate onto the canonical Execution identity. Preserve exact-head CI evidence after each reconciliation. No PR is auto-merged by this roadmap.

## Readiness rubric

| Gate | Weight | Current evidence |
|---|---:|---|
| Purpose and dependency boundaries | 15% | Source-level contract inspection plus an initial domain proposal |
| Contracts and implementation | 20% | Version-1 workflow runtime on development; PR #10 adds the governed task/lease/journal/capability/repair-policy slice |
| Automated tests | 20% | Workflow runtime, task lifecycle, strict journal replay, repair policy, signed CODE RED recovery receipt validation, and persistence tests passed on PR #10 exact head in CI run #294 (112 tests) |
| CI execution and security | 20% | Pinned-action workflow has read-only contents permission and a 15-minute timeout; broader threat cases pending |
| Demonstration and reproducibility | 15% | Successful basic probe plus fox/dog state assertions, cross-FSM transition, repeatable trace/SVG, and uploaded artifact |
| Documentation/UML completeness | 10% | Bootstrap docs, lifecycle UML, and Hub integration boundary; source-derived UML and complete reader journey remain |

**No release recommendation yet.** Readiness rises when gates are supported by verifiable implementation and test evidence, not merely by adding documents. CI run #294 verifies PR #10 head commit `0ef31369daa115fb652e46b79d208ea66ab8fc83`. PR #11's source-pinned HeadlessAi adapter build passed run #337 on exact head `d410afd4e23aaf459bdf2ce526faf964d3d36274`; this does not prove a live provider call, GitHub/CiAi integration, or the MyVR pilot.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
