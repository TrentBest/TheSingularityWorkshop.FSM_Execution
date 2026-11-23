namespace TheSingularityWorkshop.Foundry;

public sealed record GitHubWorkItemTaskProvenance(
    string ItemKind,
    string ItemNodeId,
    DateTimeOffset UpdatedAt,
    string PayloadSha256,
    string SnapshotSha,
    string? HeadSha,
    string? EventBaseSha)
{
    public GitHubWorkItemTaskProvenance Validate(string repository, int number, string itemUrl, string taskBaseSha)
    {
        var expectedPath = ItemKind switch
        {
            "issue" => $"/{repository}/issues/{number}",
            "pull_request" => $"/{repository}/pull/{number}",
            _ => throw new ArgumentException("GitHub task item kind must be issue or pull_request.")
        };
        if (string.IsNullOrWhiteSpace(ItemNodeId) || ItemNodeId.Length > 128 || ItemNodeId.Any(char.IsControl) ||
            UpdatedAt == default || PayloadSha256 is null || PayloadSha256.Length != 64 ||
            !PayloadSha256.All(Uri.IsHexDigit) || !IsSha(SnapshotSha) ||
            !string.Equals(SnapshotSha, taskBaseSha, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(itemUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.AbsolutePath != expectedPath ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("GitHub work-item provenance does not bind the exact item URL, revision, payload, and task snapshot.");
        if (ItemKind == "pull_request")
        {
            if (!IsSha(HeadSha) || !IsSha(EventBaseSha) ||
                !string.Equals(HeadSha, SnapshotSha, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pull-request task provenance must pin its exact head SHA and event base SHA.");
        }
        else if (HeadSha is not null || EventBaseSha is not null)
            throw new ArgumentException("Issue task provenance cannot contain pull-request head/base SHAs.");
        return this;
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
