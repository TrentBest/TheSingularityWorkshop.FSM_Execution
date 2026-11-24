using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Execution;

public enum ExecutionAgentOperationPolicyDecision
{
    Execute,
    RequiresHumanApproval,
    Reject
}

public sealed record ExecutionAgentOperationPolicyResult(
    ExecutionAgentOperationPolicyDecision Decision,
    string Reason);

public sealed record ExecutionAgentOperationContext(
    WorkTaskSnapshot Task,
    string TurnId,
    Guid CapabilityId,
    string Branch,
    string CandidateSha,
    WorkTaskAction Action,
    ExecutionHeadlessAiOperationProposal Proposal);

public sealed record ExecutionAgentOperationOutcome(
    string Operation,
    ExecutionAgentOperationDisposition Disposition,
    string Reason,
    string? ApprovalId = null,
    string? ExecutionResult = null);

public enum ExecutionAgentOperationDisposition
{
    Executed,
    AwaitingHumanApproval,
    Rejected,
    Deferred
}

public sealed record ExecutionAgentOperationPlanResult(
    string TaskId,
    string TurnId,
    IReadOnlyList<ExecutionAgentOperationOutcome> Outcomes);

/// <summary>Host-owned policy; model output cannot select its own authority disposition.</summary>
public interface IExecutionAgentOperationPolicy
{
    ExecutionAgentOperationPolicyResult Classify(
        WorkTaskSnapshot task, ExecutionHeadlessAiTurnResult turn,
        WorkTaskAction action, ExecutionHeadlessAiOperationProposal proposal);
}

/// <summary>
/// Host-owned last-mile adapter for one already-authorized operation. Implementations must
/// re-read task state and re-demand the capability immediately before performing the side effect.
/// </summary>
public interface IExecutionAgentOperationExecutor
{
    Task<string?> ExecuteAsync(ExecutionAgentOperationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Classifies structured HeadlessAi proposals and either executes a host-permitted operation
/// through a narrow capability, places it in the durable human-approval queue, or rejects it.
/// Operations are processed in order and the plan stops at the first approval/rejection barrier.
/// </summary>
public sealed class ExecutionAgentOperationRouter
{
    private readonly WorkTaskCapabilityRegistry _capabilities;
    private readonly IExecutionAgentOperationPolicy _policy;
    private readonly IExecutionAgentOperationExecutor _executor;
    private readonly JsonlHumanApprovalRequestStore _approvals;
    private readonly TimeProvider _timeProvider;

    public ExecutionAgentOperationRouter(WorkTaskCapabilityRegistry capabilities,
        IExecutionAgentOperationPolicy policy, IExecutionAgentOperationExecutor executor,
        JsonlHumanApprovalRequestStore approvals, TimeProvider? timeProvider = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExecutionAgentOperationPlanResult> ProcessAsync(
        WorkTaskSnapshot task, ExecutionHeadlessAiTurnResult turn, Guid capabilityId,
        string branch, TimeSpan approvalLifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(task.Request);
        ArgumentNullException.ThrowIfNull(turn.Operations);
        var now = _timeProvider.GetUtcNow();
        if (task.Status != WorkTaskStatus.Investigating ||
            task.Lease is not { } lease || lease.ExpiresAt <= now)
            throw new UnauthorizedAccessException("A live Investigating task lease is required to route model operations.");
        if (turn.TaskId != task.Request.TaskId || turn.CorrelationId != task.Request.CorrelationId ||
            !string.Equals(turn.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(turn.BaseCommitSha, task.Request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            turn.CandidateCommitSha.Length != 40 || !turn.CandidateCommitSha.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(turn.TurnId) || string.IsNullOrWhiteSpace(turn.ProviderId) ||
            string.IsNullOrWhiteSpace(turn.ModelId))
            throw new UnauthorizedAccessException("HeadlessAi result is not bound to the current task and candidate.");
        if (turn.Operations.Count > 16)
            throw new ArgumentException("At most 16 model operations can be routed in one turn.", nameof(turn));
        if (approvalLifetime <= TimeSpan.Zero || approvalLifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(approvalLifetime));

        var outcomes = new List<ExecutionAgentOperationOutcome>();
        for (var index = 0; index < turn.Operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = turn.Operations[index] ?? throw new ArgumentException("Model operation proposal cannot be null.");
            proposal.Validate();
            if (!Enum.TryParse<WorkTaskAction>(proposal.Operation, ignoreCase: false, out var action) ||
                !Enum.IsDefined(action) || !string.Equals(Enum.GetName(action), proposal.Operation, StringComparison.Ordinal))
            {
                outcomes.Add(new(proposal.Operation, ExecutionAgentOperationDisposition.Rejected,
                    "The operation is not part of Execution's closed action vocabulary."));
                break;
            }

            string? path;
            try { path = ReadPathArgument(action, proposal.ArgumentsJson); }
            catch (ArgumentException exception)
            {
                outcomes.Add(new(proposal.Operation, ExecutionAgentOperationDisposition.Rejected, exception.Message));
                break;
            }
            var policy = _policy.Classify(task, turn, action, proposal);
            if (!Enum.IsDefined(policy.Decision) || string.IsNullOrWhiteSpace(policy.Reason) ||
                policy.Reason.Length > 2048 || policy.Reason.Any(char.IsControl))
                throw new InvalidOperationException("The host operation policy returned an invalid decision.");

            if (policy.Decision == ExecutionAgentOperationPolicyDecision.Reject)
            {
                outcomes.Add(new(proposal.Operation, ExecutionAgentOperationDisposition.Rejected, policy.Reason));
                break;
            }

            if (policy.Decision == ExecutionAgentOperationPolicyDecision.RequiresHumanApproval)
            {
                var approvalId = CreateApprovalId(turn, index, proposal);
                var approvalRationale = proposal.Rationale + " Host policy: " + policy.Reason;
                if (approvalRationale.Length > 4096) approvalRationale = approvalRationale[..4096];
                var request = HumanApprovalRequest.Create(approvalId, task.Request.TaskId,
                    task.Request.CorrelationId, turn.TurnId, task.Request.Repository, turn.CandidateCommitSha,
                    turn.ProviderId + "/" + turn.ModelId, proposal.Operation, proposal.Summary,
                    approvalRationale, proposal.ArgumentsJson, now, now.Add(approvalLifetime));
                var stored = _approvals.CreateOrGet(request);
                var disposition = stored.Status switch
                {
                    HumanApprovalStatus.Pending => ExecutionAgentOperationDisposition.AwaitingHumanApproval,
                    HumanApprovalStatus.Rejected or HumanApprovalStatus.Expired or HumanApprovalStatus.Cancelled =>
                        ExecutionAgentOperationDisposition.Rejected,
                    _ => ExecutionAgentOperationDisposition.Deferred
                };
                var reason = stored.Status switch
                {
                    HumanApprovalStatus.Pending => "Waiting for an explicit human decision.",
                    HumanApprovalStatus.Approved => "Human approved; the host must resume through the approved-operation path.",
                    HumanApprovalStatus.Rejected => "A human rejected this proposal.",
                    HumanApprovalStatus.Expired => "The approval request expired.",
                    HumanApprovalStatus.Cancelled => "The approval request was cancelled.",
                    _ => "The approval request is not executable in its current state."
                };
                outcomes.Add(new(proposal.Operation, disposition, reason, stored.ApprovalId));
                break;
            }

            try
            {
                _capabilities.Demand(capabilityId, task, branch, turn.CandidateCommitSha,
                    action, path, now);
            }
            catch (UnauthorizedAccessException exception)
            {
                outcomes.Add(new(proposal.Operation, ExecutionAgentOperationDisposition.Rejected,
                    "Host policy permitted execution, but the current capability did not: " + exception.Message));
                break;
            }

            var executionResult = await _executor.ExecuteAsync(new ExecutionAgentOperationContext(
                task, turn.TurnId, capabilityId, branch, turn.CandidateCommitSha, action, proposal),
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(proposal.Operation, ExecutionAgentOperationDisposition.Executed,
                policy.Reason, ExecutionResult: executionResult));
        }

        if (outcomes.Count < turn.Operations.Count)
        {
            for (var index = outcomes.Count; index < turn.Operations.Count; index++)
                outcomes.Add(new(turn.Operations[index].Operation, ExecutionAgentOperationDisposition.Deferred,
                    "A prior operation reached an approval or rejection barrier."));
        }

        return new ExecutionAgentOperationPlanResult(task.Request.TaskId, turn.TurnId,
            Array.AsReadOnly(outcomes.ToArray()));
    }

    /// <summary>
    /// Resumes one explicitly approved proposal from its original durable turn. The method
    /// revalidates the task lease, exact candidate, approval identity, current host policy,
    /// and a fresh capability. Execution intent is journaled before the side effect; an
    /// interrupted Executing record is indeterminate and is never replayed automatically.
    /// </summary>
    public async Task<ExecutionAgentOperationOutcome> ResumeApprovedAsync(
        WorkTaskSnapshot task, ExecutionHeadlessAiTurnResult turn, string approvalId,
        Guid capabilityId, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(task.Request);
        ArgumentNullException.ThrowIfNull(turn.Operations);

        var now = _timeProvider.GetUtcNow();
        if (task.Status != WorkTaskStatus.Investigating ||
            task.Lease is not { } lease || lease.ExpiresAt <= now)
            throw new UnauthorizedAccessException("A live Investigating task lease is required to resume an approved operation.");
        if (turn.TaskId != task.Request.TaskId || turn.CorrelationId != task.Request.CorrelationId ||
            !string.Equals(turn.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(turn.BaseCommitSha, task.Request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            turn.CandidateCommitSha.Length != 40 || !turn.CandidateCommitSha.All(Uri.IsHexDigit) ||
            !string.Equals(turn.AuthorizedBranch, branch, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The completed HeadlessAi turn is not bound to the current task, branch, and candidate.");
        if (turn.Operations.Count > 16)
            throw new ArgumentException("At most 16 model operations can be resumed from one turn.", nameof(turn));

        var approval = _approvals.Get(approvalId);
        if (approval.TaskId != task.Request.TaskId ||
            approval.CorrelationId != task.Request.CorrelationId ||
            !string.Equals(approval.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approval.TurnId, turn.TurnId, StringComparison.Ordinal) ||
            !string.Equals(approval.CandidateSha, turn.CandidateCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The approval request does not match this task and exact HeadlessAi turn.");

        var proposalIndex = -1;
        ExecutionHeadlessAiOperationProposal? proposal = null;
        for (var index = 0; index < turn.Operations.Count; index++)
        {
            var candidate = turn.Operations[index] ??
                throw new ArgumentException("Model operation proposal cannot be null.", nameof(turn));
            if (CreateApprovalId(turn, index, candidate) != approvalId)
                continue;
            if (proposal is not null)
                throw new InvalidDataException("The approval identity unexpectedly matched multiple model proposals.");
            proposalIndex = index;
            proposal = candidate;
        }

        if (proposal is null || proposalIndex < 0)
            throw new UnauthorizedAccessException("The approval identity does not resolve to one proposal in the durable HeadlessAi turn.");
        proposal.Validate();
        if (approval.Operation != proposal.Operation || approval.Summary != proposal.Summary ||
            approval.ArgumentsJson != proposal.ArgumentsJson ||
            approval.RequestedBy != turn.ProviderId + "/" + turn.ModelId ||
            !approval.Rationale.StartsWith(proposal.Rationale + " Host policy: ", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The approved proposal content differs from the durable HeadlessAi response.");

        if (approval.Status == HumanApprovalStatus.Executed)
            return new(approval.Operation, ExecutionAgentOperationDisposition.Executed,
                "This approved operation already has a durable execution receipt.",
                approval.ApprovalId, approval.ExecutionResult);
        if (approval.Status == HumanApprovalStatus.Executing)
            throw new InvalidOperationException(
                "The approved operation has a durable Executing record without a completion receipt. Reconcile the host side effect; automatic replay is blocked.");
        if (approval.Status != HumanApprovalStatus.Approved)
            throw new UnauthorizedAccessException("The operation does not have an active human approval.");
        if (approval.ExpiresAt <= now)
            throw new UnauthorizedAccessException("The human approval expired before execution.");

        if (!Enum.TryParse<WorkTaskAction>(proposal.Operation, ignoreCase: false, out var action) ||
            !Enum.IsDefined(action) || !string.Equals(Enum.GetName(action), proposal.Operation, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The approved operation is not part of Execution's closed action vocabulary.");

        var policy = _policy.Classify(task, turn, action, proposal);
        if (!Enum.IsDefined(policy.Decision) || string.IsNullOrWhiteSpace(policy.Reason) ||
            policy.Reason.Length > 2048 || policy.Reason.Any(char.IsControl) ||
            policy.Decision == ExecutionAgentOperationPolicyDecision.Reject)
            throw new UnauthorizedAccessException("Current host policy no longer permits the approved operation.");

        var path = ReadPathArgument(action, proposal.ArgumentsJson);
        _capabilities.Demand(capabilityId, task, branch, turn.CandidateCommitSha,
            action, path, now);

        // Persist the one-way execution fence before any external side effect. If the process
        // stops after this append, a human must reconcile the outcome rather than replay it.
        _approvals.BeginExecution(approvalId, now);
        var executionResult = await _executor.ExecuteAsync(new ExecutionAgentOperationContext(
            task, turn.TurnId, capabilityId, branch, turn.CandidateCommitSha, action, proposal),
            cancellationToken).ConfigureAwait(false);
        var completedAt = _timeProvider.GetUtcNow();
        _approvals.CompleteExecution(approvalId, executionResult, completedAt);
        return new(approval.Operation, ExecutionAgentOperationDisposition.Executed,
            "The approved operation completed and its receipt was durably recorded.",
            approval.ApprovalId, executionResult);
    }

    private static string? ReadPathArgument(WorkTaskAction action, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (action is WorkTaskAction.ReadRepository or WorkTaskAction.WriteFiles)
        {
            if (!root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(path.GetString()))
                throw new ArgumentException("Repository read/write operations require a non-empty 'path' argument.");
            var value = path.GetString();
            if (!IsSafeRepositoryPath(value))
                throw new ArgumentException("Repository path must be safe and repository-relative.");
            return value;
        }
        if (root.TryGetProperty("path", out _))
            throw new ArgumentException("A repository path is not valid for this operation.");
        return null;
    }

    private static bool IsSafeRepositoryPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path[0] != '/' && path.IndexOf((char)92) < 0 &&
        path.IndexOf(':') < 0 && path.Split('/').All(segment => segment.Length > 0 &&
            segment is not "." and not ".." && !segment.Any(char.IsControl));

    private static string CreateApprovalId(ExecutionHeadlessAiTurnResult turn, int index,
        ExecutionHeadlessAiOperationProposal proposal)
    {
        var material = Encoding.UTF8.GetBytes(string.Join("\n", turn.TaskId, turn.TurnId,
            index.ToString(System.Globalization.CultureInfo.InvariantCulture), proposal.Operation,
            proposal.ArgumentsJson));
        var digest = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        return "approval-" + digest[..48];
    }
}
