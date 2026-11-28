using TheSingularityWorkshop.Execution;

namespace TheSingularityWorkshop.Execution.GitHubHost;

/// <summary>
/// Composes the existing GitHub transport, exact-SHA CiAi onboarding verifier, and bounded
/// repository adapter. The same redirect-disabled API client and repository token provider
/// are shared; no parallel GitHub transport or credential source is introduced.
/// </summary>
public sealed class GitHubRepositoryOperationHost
{
    private GitHubRepositoryOperationHost(
        GitHubRepositoryOperationAdapter repositoryOperations,
        GitHubAgentOnboardingAdmissionVerifier admissionVerifier)
    {
        RepositoryOperations = repositoryOperations;
        AdmissionVerifier = admissionVerifier;
    }

    public GitHubRepositoryOperationAdapter RepositoryOperations { get; }
    public GitHubAgentOnboardingAdmissionVerifier AdmissionVerifier { get; }

    public AuthorizedRepositoryOperationDispatcher CreateAuthorizedDispatcher(
        PersistentWorkTaskCoordinator tasks,
        WorkTaskCapabilityRegistry capabilities,
        IRepositoryOperationAuditJournal auditJournal,
        Func<DateTimeOffset>? clock = null) =>
        new(tasks, capabilities, RepositoryOperations, auditJournal, clock, AdmissionVerifier);

    public static GitHubRepositoryOperationHost Create(
        HttpClient apiClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy repositoryPolicy,
        IGitHubRepositoryStagingStore stagingStore,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(repositoryPolicy);
        ArgumentNullException.ThrowIfNull(stagingStore);
        if (apiClient.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("The GitHub API client must not carry ambient Authorization headers.", nameof(apiClient));
        var verifier = new GitHubAgentOnboardingAdmissionVerifier(apiClient, tokens);
        var adapter = new GitHubRepositoryOperationAdapter(apiClient, tokens, repositoryPolicy, stagingStore, clock, verifier);
        return new GitHubRepositoryOperationHost(adapter, verifier);
    }
}
