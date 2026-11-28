using System.Collections.Concurrent;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Couples coordinator mutations to the local journal. If a journal write fails after
/// an in-memory mutation, this adapter permanently fails closed for the process; callers
/// must restart and restore from the journal before accepting more work.
/// </summary>
public sealed class PersistentWorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskOperationGates = new(StringComparer.Ordinal);
    private readonly WorkTaskCoordinator _coordinator;
    private readonly JsonlWorkTaskStore _store;
    private readonly JsonlWorkTaskRepairAttemptJournal? _repairAttemptJournal;
    private Exception? _persistenceFailure;
    private bool _admissionReady = true;

    public PersistentWorkTaskCoordinator(JsonlWorkTaskStore store,
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null,
        ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _coordinator = new WorkTaskCoordinator(recoveryReceiptVerifier);
        var restored = _store.GetAll();
        foreach (var snapshot in restored)
            _coordinator.Restore(snapshot);
        _admissionReady = !restored.Any(snapshot =>
            AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository));
    }

    /// <summary>
    /// Revalidates all restored governed tasks at their immutable base SHA before the
    /// coordinator exposes lifecycle operations after process restart.
    /// </summary>
    public async ValueTask InitializeAsync(IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var snapshot in _store.GetAll())
            {
                if (!AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository))
                    continue;
                var stored = snapshot.Request.AdmissionEvidence ?? throw new UnauthorizedAccessException(
                    "A restored governed task is missing immutable onboarding admission provenance.");
                var current = await admissionVerifier.VerifyAsync(snapshot.Request.Repository,
                    snapshot.Request.BaseSha, cancellationToken).ConfigureAwait(false);
                current.ValidateFor(snapshot.Request.Repository, snapshot.Request.BaseSha);
                if (!current.IsEligible || current != stored)
                    throw new UnauthorizedAccessException(
                        $"Restored task '{snapshot.Request.TaskId}' no longer has matching exact-SHA onboarding admission.");
            }
            lock (_gate)
            {
                EnsurePersistenceHealthy();
                _admissionReady = true;
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>
    /// Serializes one repository operation against lifecycle mutations for the same task.
    /// The callback must not synchronously invoke a lifecycle mutation on this coordinator.
    /// </summary>
    public async ValueTask<T> ExecuteSerializedAsync<T>(string taskId,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(action);
        var taskGate = _taskOperationGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await taskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
                EnsureHealthy();
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            taskGate.Release();
        }
    }

    private IDisposable EnterTaskOperationGate(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var taskGate = _taskOperationGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        taskGate.Wait();
        return new TaskOperationGateLease(taskGate);
    }

    private sealed class TaskOperationGateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var taskGate = EnterTaskOperationGate(request.TaskId);
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Submit(request);
            return Persist(snapshot, create: true);
        }
    }

    public async ValueTask<WorkTaskSnapshot> SubmitAsync(WorkTaskRequest request,
        IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        var validated = request.Validate(requireAdmissionEvidence: false);
        if (!AgentOnboardingTargetPolicy.RequiresAdmission(validated.Repository))
            return Submit(validated with { AdmissionEvidence = null });

        var evidence = await admissionVerifier.VerifyAsync(validated.Repository, validated.BaseSha,
            cancellationToken).ConfigureAwait(false);
        evidence.ValidateFor(validated.Repository, validated.BaseSha);
        if (!evidence.IsEligible)
            throw new UnauthorizedAccessException("The repository's AgentOnboarding contract is not eligible.");

        using var taskGate = EnterTaskOperationGate(validated.TaskId);
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.SubmitAdmitted(validated, evidence);
            return Persist(snapshot, create: true);
        }
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now)
    {
        using var taskGate = EnterTaskOperationGate(taskId);
        lock (_gate)
        {
            EnsureHealthy();
            var existing = _coordinator.GetSnapshot(taskId);
            if (AgentOnboardingTargetPolicy.RequiresAdmission(existing.Request.Repository))
                throw new UnauthorizedAccessException("Governed tasks require ClaimAsync with fresh exact-SHA onboarding verification.");
            var snapshot = _coordinator.Claim(taskId, agentId, leaseDuration, now);
            return Persist(snapshot);
        }
    }

    /// <summary>
    /// Revalidates a governed task's exact base commit before the claim can mutate memory
    /// or append a durable lease event. The synchronous Claim path rejects governed tasks.
    /// </summary>
    public async ValueTask<WorkTaskSnapshot> ClaimAsync(string taskId, string agentId,
        TimeSpan leaseDuration, DateTimeOffset now,
        IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkTaskSnapshot before;
            lock (_gate)
            {
                EnsureHealthy();
                before = _coordinator.GetSnapshot(taskId);
            }

            if (!AgentOnboardingTargetPolicy.RequiresAdmission(before.Request.Repository))
                return Claim(taskId, agentId, leaseDuration, now);

            var stored = before.Request.AdmissionEvidence ?? throw new UnauthorizedAccessException(
                "The governed task has no persisted onboarding admission provenance.");
            var current = await admissionVerifier.VerifyAsync(before.Request.Repository,
                before.Request.BaseSha, cancellationToken).ConfigureAwait(false);
            current.ValidateFor(before.Request.Repository, before.Request.BaseSha);
            if (!current.IsEligible || current != stored)
                throw new UnauthorizedAccessException("Current exact-SHA onboarding evidence does not match the task's immutable admission provenance.");

            using var taskGate = EnterTaskOperationGate(taskId);
            lock (_gate)
            {
                EnsureHealthy();
                var latest = _coordinator.GetSnapshot(taskId);
                if (latest.Request != before.Request || latest.Events.Count != before.Events.Count ||
                    latest.Status != before.Status)
                    throw new InvalidOperationException("Task lifecycle changed while onboarding admission was being revalidated; retry the claim.");
                var claimed = _coordinator.ClaimAdmitted(taskId, agentId, leaseDuration, now, current);
                return Persist(claimed);
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason)
    {
        using var taskGate = EnterTaskOperationGate(taskId);
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Transition(taskId, leaseId, next, now, reason);
            return Persist(snapshot);
        }
    }

    /// <summary>Verifies and durably records a host-approved CODE RED recovery.</summary>
    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, CodeRedRecoveryApprovalReceipt receipt,
        DateTimeOffset now, string reason)
    {
        using var taskGate = EnterTaskOperationGate(taskId);
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.RecoverFromCodeRed(taskId, leaseId, incident, receipt, now, reason);
            return Persist(snapshot);
        }
    }

    /// <summary>
    /// Applies a repair decision using only the exact attempt history recovered from the
    /// durable append-only attempt journal. Caller-supplied history is deliberately not accepted.
    /// </summary>
    public WorkTaskSnapshot ApplyRepairDecision(string taskId, Guid leaseId,
        WorkTaskRepairAttempt currentAttempt, WorkTaskRepairEvidenceExpectation expectation,
        TimeSpan maximumDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
        using var taskGate = EnterTaskOperationGate(taskId);
        lock (_gate)
        {
            EnsureHealthy();
            var journal = _repairAttemptJournal ?? throw new InvalidOperationException(
                "A durable repair-attempt journal is required before applying a persistent repair decision.");
            return journal.ExecuteWithAttempts(taskId, history =>
            {
                if (history.Count == 0 || history[^1] != currentAttempt ||
                    currentAttempt.TaskId != taskId)
                    throw new InvalidOperationException(
                        "The current CI attempt must exactly match the latest durable attempt-journal record for this task.");
                var previousAttempts = history.Take(history.Count - 1).ToArray();
                var snapshot = _coordinator.ApplyRepairDecision(taskId, leaseId, previousAttempts,
                    currentAttempt, expectation, maximumDuration, now);
                return Persist(snapshot);
            });
        }
    }

    public WorkTaskSnapshot GetSnapshot(string taskId)
    {
        lock (_gate)
        {
            EnsureHealthy();
            return _coordinator.GetSnapshot(taskId);
        }
    }

    private WorkTaskSnapshot Persist(WorkTaskSnapshot snapshot, bool create = false)
    {
        try
        {
            return create ? _store.Create(snapshot) : _store.Append(snapshot);
        }
        catch (Exception exception)
        {
            _persistenceFailure = exception;
            throw new InvalidOperationException(
                "Task state changed in memory but could not be durably journaled. This coordinator is now fail-closed; restart and restore before dispatch.",
                exception);
        }
    }

    private void EnsureHealthy()
    {
        EnsurePersistenceHealthy();
        if (!_admissionReady)
            throw new InvalidOperationException("Restored governed tasks are unavailable until InitializeAsync revalidates exact-SHA onboarding admission.");
    }

    private void EnsurePersistenceHealthy()
    {
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "Task dispatch is disabled because a prior journal write failed. Restart and restore from durable state before continuing.",
                _persistenceFailure);
    }
}
