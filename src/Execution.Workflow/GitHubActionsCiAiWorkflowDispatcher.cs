using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Dispatches only the registered workflow on its protected ref. The supplied HttpClient must
/// have automatic redirects and proxies disabled; the credential provider must grant only the
/// repository-scoped Actions workflow-dispatch permission needed by this operation.
/// The candidate is supplied as
/// immutable SHA plus its human-readable branch ref; request identity is carried into the trusted
/// workflow's run-scoped marker artifact for later independent reconciliation.
/// </summary>
public interface IGitHubActionsWorkflowDispatchCredentialProvider
{
    ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken = default);
}

public sealed class GitHubActionsCiAiWorkflowDispatcher : ICiAiWorkflowDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly IGitHubActionsWorkflowDispatchCredentialProvider _credentials;
    private readonly CiWorkflowObserverProfile _profile;
    private readonly string _protectedWorkflowRef;

    public GitHubActionsCiAiWorkflowDispatcher(
        HttpClient httpClient,
        IGitHubActionsWorkflowDispatchCredentialProvider credentials,
        CiWorkflowObserverProfile profile,
        string protectedWorkflowRef = "refs/heads/development")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (!IsBranchRef(protectedWorkflowRef))
            throw new ArgumentException("Protected workflow ref must be a fully qualified branch ref.", nameof(protectedWorkflowRef));
        _protectedWorkflowRef = protectedWorkflowRef;
    }

    public async Task DispatchAsync(
        CiAiRequestTaskMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate();

        var expectedArtifactName = _profile.TestArtifactName(1);
        if (!string.Equals(mapping.WorkflowPath, _profile.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(mapping.JobKey, _profile.RequiredJobKey, StringComparison.Ordinal) ||
            !string.Equals(mapping.JobName, _profile.RequiredJobName, StringComparison.Ordinal) ||
            !string.Equals(mapping.ResolutionStepName, _profile.ResolutionStepName, StringComparison.Ordinal) ||
            !string.Equals(mapping.VerificationStepName, _profile.VerificationStepName, StringComparison.Ordinal) ||
            !string.Equals(mapping.SourceArtifactName, expectedArtifactName, StringComparison.Ordinal) ||
            !string.Equals(mapping.TrustedWorkflowRef, _protectedWorkflowRef, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(mapping.TrustedEventSha) ||
            !IsFullSha(mapping.TrustedEventSha) ||
            !IsFullSha(mapping.TrustedWorkflowSha) ||
            !IsBranchRef(mapping.CandidateRef) ||
            !IsFullSha(mapping.CandidateSha))
            throw new UnauthorizedAccessException("CiAi dispatch mapping does not match the registered protected workflow, candidate, and artifact contract.");

        var token = await _credentials.GetTokenAsync(mapping.Repository, cancellationToken).ConfigureAwait(false);
        ValidateToken(token);
        if (token is null)
            throw new UnauthorizedAccessException("GitHub Actions workflow dispatch requires an authenticated read/write workflow credential.");

        var parts = mapping.Repository.Split('/');
        var workflowFile = Uri.EscapeDataString(Path.GetFileName(mapping.WorkflowPath));
        var endpoint = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/actions/workflows/{workflowFile}/dispatches");

        var protectedBranch = _protectedWorkflowRef["refs/heads/".Length..];
        var payload = new
        {
            @ref = protectedBranch,
            inputs = new
            {
                candidate_ref = mapping.CandidateRef,
                candidate_sha = mapping.CandidateSha,
                request_id = mapping.RequestId,
                operation_id = mapping.OperationId,
                idempotency_key = mapping.IdempotencyKey
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Execution/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new HttpRequestException(
                $"GitHub workflow dispatch did not return the required 204 acceptance status (received {(int)response.StatusCode}).");
    }

    private static bool IsBranchRef(string? value)
        => value is { Length: > 11 and <= 300 } &&
            value.StartsWith("refs/heads/", StringComparison.Ordinal) &&
            !value.Contains("..", StringComparison.Ordinal) &&
            !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static bool IsFullSha(string? value)
        => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static void ValidateToken(string? token)
    {
        if (token is { Length: > 8192 } ||
            token is not null &&
            (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace)))
            throw new UnauthorizedAccessException("GitHub read credential provider returned an invalid token.");
    }
}
