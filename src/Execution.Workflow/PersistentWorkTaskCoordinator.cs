namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Couples coordinator mutations to the local journal. If a journal write fails after
/// an in-memory mutation, this adapter permanently fails closed for the process; callers
/// must restart and restore from the journal before accepting more work.
/// </summary>
public sealed class PersistentWorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly WorkTaskCoordinator _coordinator;
    private readonly JsonlWorkTaskStore _store;
    private readonly JsonlWorkTaskRepairAttemptJournal? _repairAttemptJournal;
    private Exception? _persistenceFailure;

    public PersistentWorkTaskCoordinator(JsonlWorkTaskStore store,
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null,
        ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _coordinator = new WorkTaskCoordinator(recoveryReceiptVerifier);
        foreach (var snapshot in _store.GetAll())
            _coordinator.Restore(snapshot);
    }

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Submit(request);
            return Persist(snapshot, create: true);
        }
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Claim(taskId, agentId, leaseDuration, now);
            return Persist(snapshot);
        }
    }

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason)
    {
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

    /// <summary>
    /// Consumes a verified CiAi report, appends its exact attempt idempotently, and applies
    /// the existing Execution lifecycle policy using only CiAi's versioned verdict.
    /// </summary>
    public CiAiDiagnosticReportContext ConsumeCiAiDiagnosticReport(
        string taskId,
        Guid leaseId,
        ExecutionVerifiedCiAiReportArtifact artifact,
        CiAiHostObservedSourceArtifactBinding? sourceArtifactBinding,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate)
        {
            EnsureHealthy();
            var journal = _repairAttemptJournal ?? throw new InvalidOperationException(
                "A durable authoritative workflow-evidence attempt journal is required before consuming diagnostic reports.");
            var task = _coordinator.GetSnapshot(taskId);
            if (now == default || task.Lease is not { } lease ||
                lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new UnauthorizedAccessException("A current, unexpired task lease is required to consume CiAi diagnostic evidence.");
            if (task.Status != WorkTaskStatus.RepairRequired)
                throw new UnauthorizedAccessException("Diagnostic reports cannot create RepairRequired state; authoritative workflow evidence must do that first.");

            var mapping = artifact.Request.Mapping.Validate();
            var run = artifact.Request.RunBinding
                ?? throw new InvalidOperationException("CiAi report is not bound to an exact GitHub run.");
            if (!string.Equals(mapping.TaskId, taskId, StringComparison.Ordinal) ||
                !string.Equals(mapping.CorrelationId, task.Request.CorrelationId, StringComparison.Ordinal) ||
                !string.Equals(mapping.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("CiAi diagnostic report mapping identifies a different Execution task.");

            var authoritativeEvent = task.Events.LastOrDefault(item => item.Current == WorkTaskStatus.RepairRequired)
                ?? throw new UnauthorizedAccessException("Task has no authoritative RepairRequired event.");
            if (authoritativeEvent.EvidenceSchema != WorkTaskRepairAttempt.RequiredEvidenceSchema ||
                !string.Equals(authoritativeEvent.CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                authoritativeEvent.CiRunId != run.RunId ||
                authoritativeEvent.CiRunAttempt != run.RunAttempt ||
                string.IsNullOrWhiteSpace(authoritativeEvent.EvidencePayloadSha256))
                throw new UnauthorizedAccessException("The diagnostic report does not match the exact ciai.workflow-evidence.v1 task event.");

            var authoritativeAttempt = journal.GetAttempts(taskId).SingleOrDefault(item =>
                item.CiRunId == run.RunId && item.CiRunAttempt == run.RunAttempt);
            if (authoritativeAttempt is null ||
                authoritativeAttempt.EvidenceSchema != WorkTaskRepairAttempt.RequiredEvidenceSchema ||
                !string.Equals(authoritativeAttempt.CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(authoritativeAttempt.EvidencePayloadSha256, authoritativeEvent.EvidencePayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                !authoritativeAttempt.EvidenceComplete || authoritativeAttempt.IsSuccessful ||
                !authoritativeAttempt.HasActionableDiagnostics)
                throw new UnauthorizedAccessException("The matching authoritative workflow-evidence v1 attempt is missing, incomplete, successful, or non-actionable.");

            return CiAiTestResultReportConsumer.Consume(artifact, task, sourceArtifactBinding, now);
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
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "Task dispatch is disabled because a prior journal write failed. Restart and restore from durable state before continuing.",
                _persistenceFailure);
    }
}
