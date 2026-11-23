namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Couples coordinator mutations to the local journal. If a journal write fails after
/// an in-memory mutation, this adapter permanently fails closed for the process; callers
/// must restart and restore from the journal before accepting more work.
/// </summary>
public sealed class PersistentWorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _taskGates = new(StringComparer.Ordinal);
    private readonly WorkTaskCoordinator _coordinator;
    private readonly JsonlWorkTaskStore _store;
    private readonly JsonlWorkTaskRepairAttemptJournal? _repairAttemptJournal;
    private readonly JsonlDependencyAdmissionStore? _dependencyAdmissionStore;
    private readonly JsonlCompositeDependencyAdmissionStore? _compositeDependencyAdmissionStore;
    private readonly IWorkTaskClaimAdmissionPolicy? _claimAdmissionPolicy;
    private Exception? _persistenceFailure;
    private bool _admissionReady = true;

    public PersistentWorkTaskCoordinator(JsonlWorkTaskStore store,
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null,
        ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null,
        JsonlDependencyAdmissionStore? dependencyAdmissionStore = null,
        JsonlCompositeDependencyAdmissionStore? compositeDependencyAdmissionStore = null,
        IWorkTaskClaimAdmissionPolicy? claimAdmissionPolicy = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _dependencyAdmissionStore = dependencyAdmissionStore;
        _compositeDependencyAdmissionStore = compositeDependencyAdmissionStore;
        _claimAdmissionPolicy = claimAdmissionPolicy;
        _coordinator = new WorkTaskCoordinator(recoveryReceiptVerifier);
        var restored = _store.GetAll();
        foreach (var snapshot in restored)
            _coordinator.Restore(snapshot);
        _admissionReady = !restored.Any(snapshot =>
            AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository));
    }

    /// <summary>The exact durable repair-attempt journal used by evidence decisions, if configured.</summary>
    public JsonlWorkTaskRepairAttemptJournal? RepairAttemptJournal => _repairAttemptJournal;

    /// <summary>
    /// The durable dependency admission journal. Hosts must create a task's Waiting gate
    /// before making that task eligible for dispatch.
    /// </summary>
    public JsonlDependencyAdmissionStore? DependencyAdmissionStore => _dependencyAdmissionStore;

    /// <summary>Durable CiAi composite admission journal, if configured.</summary>
    public JsonlCompositeDependencyAdmissionStore? CompositeDependencyAdmissionStore => _compositeDependencyAdmissionStore;

    /// <summary>
    /// Revalidates every restored governed task against the trusted host before the coordinator
    /// exposes any task lifecycle operation after process restart.
    /// </summary>
    public async ValueTask InitializeAsync(IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        var restored = _store.GetAll();
        foreach (var snapshot in restored)
        {
            if (!AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository))
                continue;
            var stored = snapshot.Request.AdmissionEvidence ?? throw new UnauthorizedAccessException(
                "A restored governed task is missing immutable AgentOnboarding admission provenance.");
            var current = await admissionVerifier.VerifyAsync(snapshot.Request.Repository,
                snapshot.Request.BaseSha, cancellationToken).ConfigureAwait(false);
            current.ValidateFor(snapshot.Request.Repository, snapshot.Request.BaseSha);
            if (!current.IsEligible || current != stored)
                throw new UnauthorizedAccessException(
                    $"Restored task '{snapshot.Request.TaskId}' no longer has matching exact-SHA AgentOnboarding admission.");
        }

        lock (_gate)
        {
            EnsurePersistenceHealthy();
            _admissionReady = true;
        }
    }

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithTaskLock(request.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.Submit(request);
                return Persist(snapshot, create: true);
            }
        });
    }

    /// <summary>Verifies governed onboarding before either in-memory mutation or durable creation.</summary>
    public async ValueTask<WorkTaskSnapshot> SubmitAsync(WorkTaskRequest request,
        IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        var validated = request.Validate(requireAdmissionEvidence: false);
        AgentOnboardingAdmissionEvidence? evidence = null;
        if (AgentOnboardingTargetPolicy.RequiresAdmission(validated.Repository))
        {
            evidence = await admissionVerifier.VerifyAsync(validated.Repository, validated.BaseSha,
                cancellationToken).ConfigureAwait(false);
            evidence.ValidateFor(validated.Repository, validated.BaseSha);
            if (!evidence.IsEligible)
                throw new UnauthorizedAccessException("The repository's AgentOnboarding contract is not eligible for autonomous task creation.");
        }

        return WithTaskLock(validated.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = evidence is null
                    ? _coordinator.Submit(validated with { AdmissionEvidence = null })
                    : _coordinator.SubmitAdmitted(validated, evidence);
                return Persist(snapshot, create: true);
            }
        });
    }

    /// <summary>
    /// Creates a durable Waiting gate before the task becomes claimable. The per-task
    /// reservation prevents a concurrent Claim through this coordinator instance from
    /// racing task creation and admission registration.
    /// </summary>
    public WorkTaskSnapshot SubmitWaiting(WorkTaskRequest request,
        DependencyGateIdentity requiredGate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requiredGate);
        var validated = request.Validate();
        if (AgentOnboardingTargetPolicy.RequiresAdmission(validated.Repository))
            throw new UnauthorizedAccessException(
                "Governed repositories must complete AgentOnboarding admission before dependency-gated task creation.");
        var admissionStore = _dependencyAdmissionStore ?? throw new InvalidOperationException(
            "A durable dependency admission store is required for SubmitWaiting.");

        return WithTaskLock(validated.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                if (_store.GetAll().Any(snapshot => snapshot.Request.TaskId == validated.TaskId))
                    throw new InvalidOperationException($"Task '{validated.TaskId}' already exists.");
                admissionStore.CreateWaiting(validated.TaskId, requiredGate, now);
                var snapshot = _coordinator.Submit(validated);
                return Persist(snapshot, create: true);
            }
        });
    }

    /// <summary>
    /// Creates a task behind a durable CiAi composite Waiting decision. The supplied
    /// identity must bind this exact task, repository, and source SHA. CiAi expression
    /// evaluation and evidence verification remain outside this coordinator.
    /// </summary>
    public WorkTaskSnapshot SubmitCompositeWaiting(WorkTaskRequest request,
        CompositeDependencyAdmissionIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        var validated = request.Validate();
        identity.Validate();
        if (identity.TaskId != validated.TaskId ||
            !string.Equals(identity.ConsumerRepository, validated.Repository, StringComparison.Ordinal) ||
            !string.Equals(identity.ConsumerSourceSha, validated.BaseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Composite admission identity must bind the exact work-task ID, repository, and source SHA.");
        if (AgentOnboardingTargetPolicy.RequiresAdmission(validated.Repository))
            throw new UnauthorizedAccessException(
                "Governed repositories must complete AgentOnboarding admission before composite dependency admission.");
        var admissionStore = _compositeDependencyAdmissionStore ?? throw new InvalidOperationException(
            "A durable composite dependency admission store is required for SubmitCompositeWaiting.");

        return WithTaskLock(validated.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                if (_store.GetAll().Any(snapshot => snapshot.Request.TaskId == validated.TaskId))
                    throw new InvalidOperationException($"Task '{validated.TaskId}' already exists.");
                admissionStore.CreateWaiting(identity, now);
                var snapshot = _coordinator.Submit(validated);
                return Persist(snapshot, create: true);
            }
        });
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                _claimAdmissionPolicy?.EnsureClaimAllowed(_coordinator.GetSnapshot(taskId).Request);
                var dependency = _dependencyAdmissionStore?.Get(taskId);
                if (dependency is { Status: DependencyAdmissionStatus.Waiting })
                    throw new InvalidOperationException(
                        "Task is dormant: its declared provider capability is still Waiting. No execution lease may be claimed.");
                if (dependency is { Status: DependencyAdmissionStatus.Blocked })
                    throw new InvalidOperationException(
                        "Task is blocked: its declared provider capability was rejected or failed terminally.");
                var composite = _compositeDependencyAdmissionStore?.Get(taskId);
                if (composite is { State: CompositeDependencyAdmissionState.Waiting })
                    throw new InvalidOperationException(
                        "Task is dormant: its CiAi composite prerequisite expression is still Waiting. No execution lease may be claimed.");
                if (composite is { State: CompositeDependencyAdmissionState.Blocked })
                    throw new InvalidOperationException(
                        "Task is blocked: CiAi's authoritative composite prerequisite evaluation returned Blocked.");
                var snapshot = _coordinator.Claim(taskId, agentId, leaseDuration, now);
                return Persist(snapshot);
            }
        });

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.Transition(taskId, leaseId, next, now, reason);
                return Persist(snapshot);
            }
        });

    /// <summary>Verifies and durably records a host-approved CODE RED recovery.</summary>
    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, CodeRedRecoveryApprovalReceipt receipt,
        DateTimeOffset now, string reason) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.RecoverFromCodeRed(taskId, leaseId, incident, receipt, now, reason);
                return Persist(snapshot);
            }
        });

    /// <summary>
    /// Applies a repair decision using only the exact attempt history recovered from the
    /// durable append-only attempt journal. Caller-supplied history is deliberately not accepted.
    /// </summary>
    public WorkTaskSnapshot ApplyRepairDecision(string taskId, Guid leaseId,
        WorkTaskRepairAttempt currentAttempt, WorkTaskRepairEvidenceExpectation expectation,
        TimeSpan maximumDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
        return WithTaskLock(taskId, () =>
        {
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
        });
    }

    /// <summary>
    /// Serializes a repository dispatch against lifecycle mutations for this task only.
    /// The reservation remains held until the asynchronous host operation and its audit
    /// completion have finished. Cancellation while waiting does not acquire the reservation.
    /// </summary>
    public async ValueTask<T> ExecuteSerializedAsync<T>(string taskId,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var taskGate = GetTaskGate(taskId);
        await taskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            taskGate.Release();
        }
    }

    private T WithTaskLock<T>(string taskId, Func<T> operation)
    {
        var taskGate = GetTaskGate(taskId);
        taskGate.Wait();
        try
        {
            return operation();
        }
        finally
        {
            taskGate.Release();
        }
    }

    private SemaphoreSlim GetTaskGate(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw new ArgumentException("Task identity must be non-empty, bounded, and control-free.", nameof(taskId));
        return _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
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
            throw new InvalidOperationException(
                "Restored governed tasks are unavailable until InitializeAsync revalidates their exact-SHA AgentOnboarding evidence.");
    }

    private void EnsurePersistenceHealthy()
    {
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "Task dispatch is disabled because a prior journal write failed. Restart and restore from durable state before continuing.",
                _persistenceFailure);
    }
}
