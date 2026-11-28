# Candidate-SHA onboarding at the repository mutation boundary

## Purpose

A task's onboarding receipt and a later repository operation's onboarding receipt answer different questions.

- **Task admission** proves that the root `AgentOnboarding.md` at the task's immutable `BaseSha` satisfied CiAi's `ciai.agent-onboarding.v1` contract when the task was admitted.
- **Operation admission** proves that the same contract is eligible at the exact `CandidateSha` the repository operation will read or mutate.

A base-SHA receipt must never be reused as proof for a candidate write. The candidate may have advanced through an explicit, authorized commit.

## Enforcement sequence

1. The workflow proposes a bounded `RepositoryOperation` with repository, isolated `agents/` branch, and full candidate SHA.
2. `AuthorizedRepositoryOperationDispatcher` reloads the durable task and verifies repository identity.
3. For a governed repository, it calls the injected `IAgentOnboardingAdmissionVerifier` with `operation.CandidateSha`. The verifier reads only root `AgentOnboarding.md` at that immutable SHA and delegates interpretation to CiAi's pinned validator.
4. The dispatcher demands the existing lease-bound capability and writes a schema-v3 Pending audit record. That record includes the candidate-bound onboarding evidence; it contains no file content or credentials.
5. Under the per-task lifecycle reservation, the dispatcher re-reads the task, repeats candidate-SHA validation, compares the complete evidence value, and re-demands the capability.
6. The concrete `GitHubRepositoryOperationAdapter` independently requires its own trusted verifier for governed repositories. It revalidates before GitHub writes, including staged blob/tree/commit creation, branch creation, non-force ref advancement, and an ambiguous-update retry.
7. The adapter verifies exact branch-tip and commit-parent relationships. Only an explicit commit operation may advance the candidate SHA.

## Durable audit and replay

The JSONL operation journal uses schema v3 for candidate onboarding evidence. The Pending identity binds repository, candidate SHA, required root path, CiAi contract version, eligibility outcome, reason/failure code, and content digest. Terminal records must preserve the same evidence. Replay rejects governed v1/v2 records that have no candidate-specific admission evidence; it does not infer or backfill eligibility from task provenance.

A Pending operation after an ambiguous remote outcome remains pending for explicit reconciliation. It is never automatically replayed as a write.

## Trust boundaries

- CiAi owns the onboarding contract and parser.
- Execution owns task lifecycle, capability checks, serialization, and operation audit.
- The GitHub host owns credentials, repository allowlists, exact commit reads, and transport.
- Onboarding is untrusted repository data. It grants no capability, task scope, credential, CI authority, or human approval.
- CiAi workflow dispatch remains a separate coordinator boundary; this repository adapter does not dispatch workflows.

## Limits

This is a deterministic host implementation and test surface, not proof of production credentials, a deployed authenticated host, or a live GitHub mutation. The host must supply a redirect-disabled HTTPS GitHub API client and a token provider scoped to its repository policy. The JSONL journals are single-host durability primitives, not distributed coordination.
