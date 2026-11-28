namespace TheSingularityWorkshop.Execution;

/// <summary>Immutable, bounded result of CiAi's exact-commit onboarding evaluation.</summary>
public sealed record AgentOnboardingAdmissionEvidence(
    string Repository,
    string TargetSha,
    string Path,
    string ContractVersion,
    bool IsEligible,
    string Outcome,
    string? FailureCode,
    string Reason,
    string? ContentSha256)
{
    public const string RequiredPath = "AgentOnboarding.md";
    public const string RequiredContractVersion = "ciai.agent-onboarding.v1";

    public AgentOnboardingAdmissionEvidence ValidateFor(string repository, string targetSha)
    {
        if (!string.Equals(Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Onboarding evidence is not bound to the requested repository.");
        if (!IsFullSha(TargetSha) || !string.Equals(TargetSha, targetSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Onboarding evidence is not bound to the exact requested commit SHA.");
        if (!string.Equals(Path, RequiredPath, StringComparison.Ordinal) ||
            !string.Equals(ContractVersion, RequiredContractVersion, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Onboarding evidence does not identify the required root-level CiAi contract.");
        if (string.IsNullOrWhiteSpace(Outcome) || Outcome.Length > 64 || Outcome.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Reason) || Reason.Length > 2048 || Reason.Any(char.IsControl) ||
            (FailureCode is not null && (FailureCode.Length > 128 || FailureCode.Any(char.IsControl))))
            throw new ArgumentException("Onboarding evidence contains invalid bounded result metadata.");
        if (ContentSha256 is not null && !IsDigest(ContentSha256))
            throw new ArgumentException("Onboarding content digest must be a full SHA-256 value when present.");
        if (IsEligible)
        {
            if (Outcome != "ELIGIBLE" || FailureCode is not null || !IsDigest(ContentSha256))
                throw new UnauthorizedAccessException("Eligible onboarding evidence must contain the successful CiAi result and content digest.");
        }
        else if (Outcome != "NOT_ELIGIBLE" || string.IsNullOrWhiteSpace(FailureCode))
        {
            throw new ArgumentException("Rejected onboarding evidence must retain its failure outcome and code.");
        }
        return this;
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Host-owned exact-commit reader and CiAi validator boundary.</summary>
public interface IAgentOnboardingAdmissionVerifier
{
    ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
        string repository, string targetSha, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repositories whose autonomous work is governed by mandatory CiAi onboarding admission.
/// Keep this list aligned with the owner-approved ADP participant set.
/// </summary>
public static class AgentOnboardingTargetPolicy
{
    private static readonly HashSet<string> Repositories = new(StringComparer.OrdinalIgnoreCase)
    {
        "TrentBest/TheSingularityWorkshop.CiAi",
        "TrentBest/TheSingularityWorkshop.CiAi.CogA",
        "TrentBest/TheSingularityWorkshop.CiAi.CogB",
        "TrentBest/TheSingularityWorkshop.CiAi.CogC",
        "TrentBest/TheSingularityWorkshop.CiAi.CogD",
        "TrentBest/TheSingularityWorkshop.CiAi.CogE",
        "TrentBest/TheSingularityWorkshop.Experiences",
        "TrentBest/TheSingularityWorkshop.FSM_COS",
        "TrentBest/TheSingularityWorkshop.FSM_Execution",
        "TrentBest/TheSingularityWorkshop.HeadlessAi",
        "TrentBest/TheSingularityWorkshop.MicroBundleRepository",
        "TrentBest/TheSingularityWorkshop.ServerSC2",
        "TrentBest/TheSingularityWorkshop.SingularityBot",
        "TrentBest/raWWar",
        "TrentBest/WebPage"
    };

    public static bool RequiresAdmission(string repository) => Repositories.Contains(repository);
}
