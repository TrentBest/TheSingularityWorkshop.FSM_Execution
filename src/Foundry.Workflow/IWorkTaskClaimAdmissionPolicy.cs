namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host-owned final claim fence for tasks whose source revision can be superseded
/// independently of the task lifecycle journal.
/// </summary>
public interface IWorkTaskClaimAdmissionPolicy
{
    void EnsureClaimAllowed(WorkTaskRequest request);
}
