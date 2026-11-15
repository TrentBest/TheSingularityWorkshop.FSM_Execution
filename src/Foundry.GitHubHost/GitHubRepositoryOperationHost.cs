using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Owns the production composition of the GitHub repository adapter, Actions dispatcher,
/// and exact-run candidate-checkout provenance verifier. The host supplies durable stores,
/// token provisioning, allowlists, and HTTP clients configured with automatic redirects disabled.
/// </summary>
public sealed class GitHubRepositoryOperationHost
{
    private GitHubRepositoryOperationHost(
        GitHubRepositoryOperationAdapter repositoryOperations,
        GitHubActionsWorkflowDispatcher actionsDispatcher,
        GitHubActionsArtifactProvenanceReader provenanceReader)
    {
        RepositoryOperations = repositoryOperations;
        ActionsDispatcher = actionsDispatcher;
        ProvenanceReader = provenanceReader;
    }

    /// <summary>Capability-gated repository-operation adapter to register with Foundry's host dispatcher.</summary>
    public GitHubRepositoryOperationAdapter RepositoryOperations { get; }

    /// <summary>Host-owned dispatcher used by repository CI operations.</summary>
    public GitHubActionsWorkflowDispatcher ActionsDispatcher { get; }

    /// <summary>Verifier for the provenance artifact of an already-correlated exact run attempt.</summary>
    public GitHubActionsArtifactProvenanceReader ProvenanceReader { get; }

    /// <summary>
    /// Creates the complete GitHub host composition. Both clients must have automatic redirects
    /// disabled. The API client must be rooted at https://api.github.com/ and have no default
    /// Authorization header; credentials are supplied per request by the host token provider.
    /// The artifact client must have no default Authorization header and must not follow redirects.
    /// </summary>
    public static GitHubRepositoryOperationHost Create(
        HttpClient apiClient,
        HttpClient artifactDownloadClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy repositoryPolicy,
        GitHubActionsWorkflowAllowlist workflows,
        IGitHubActionsDispatchJournal dispatchJournal,
        IGitHubRepositoryStagingStore stagingStore,
        string trustedWorkflowSha,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(artifactDownloadClient);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(repositoryPolicy);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(dispatchJournal);
        ArgumentNullException.ThrowIfNull(stagingStore);

        if (apiClient.DefaultRequestHeaders.Authorization is not null ||
            artifactDownloadClient.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("GitHub host clients must not carry ambient Authorization headers.");

        var dispatcher = new GitHubActionsWorkflowDispatcher(
            apiClient, tokens, repositoryPolicy, workflows, dispatchJournal, trustedWorkflowSha, clock);
        var provenanceReader = new GitHubActionsArtifactProvenanceReader(
            apiClient, artifactDownloadClient, tokens, repositoryPolicy, dispatchJournal, clock);
        var repositoryAdapter = new GitHubRepositoryOperationAdapter(
            apiClient, tokens, repositoryPolicy, stagingStore, clock, dispatcher);

        return new GitHubRepositoryOperationHost(repositoryAdapter, dispatcher, provenanceReader);
    }
}
