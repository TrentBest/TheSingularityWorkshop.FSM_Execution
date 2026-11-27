using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// The producer-owned CiAi V1 packet after Execution has rebound it to persisted dispatch intent
/// and re-evaluated protected workflow-definition trust from independently retrieved workflow bytes.
/// </summary>
public sealed record ExecutionAcceptedCiAiWorkflowEvidence(
    CiWorkflowEvidencePacket Packet,
    string PacketSha256,
    string Verdict)
{
    public string Repository => Packet.Provenance.Repository;
    public long RunId => Packet.Provenance.RunId;
    public int RunAttempt => Packet.Provenance.RunAttempt;
    public string WorkflowPath => Packet.Provenance.WorkflowPath;
    public string WorkflowRef => Packet.Provenance.WorkflowRef;
    public string WorkflowSha => Packet.Provenance.WorkflowSha;
    public string EventName => Packet.Provenance.EventName;
    public string EventRef => Packet.Provenance.EventRef;
    public string EventSha => Packet.Provenance.EventSha;
    public string CandidateRef => Packet.CandidateRef;
    public string CandidateSha => Packet.CandidateSha;
    public bool EvidenceComplete => Packet.EvidenceComplete;
    public bool WorkflowDefinitionTrusted => Packet.WorkflowDefinitionTrusted;
    public bool CheckoutVerified => Packet.CheckoutVerified;
    public bool IsSuccessful => Packet.IsSuccessful;
    public bool HasActionableDiagnostics => Packet.HasActionableDiagnostics;
    public IReadOnlyList<string> BlockingReasons => Packet.BlockingReasons;
}

/// <summary>
/// Execution's adapter over CiAi's canonical packet parser. This deliberately does not duplicate
/// the ciai.workflow-evidence.v1 JSON schema or derive trust from serialized packet booleans.
/// </summary>
public static class CiAiWorkflowEvidencePacketConsumer
{
    public const int MaximumPacketBytes = 1024 * 1024;
    public const int MaximumWorkflowDefinitionBytes = 256 * 1024;

    /// <summary>
    /// Parses the exact CiAi V1 packet, checks every identity against persisted dispatch intent,
    /// and re-evaluates workflow trust using bytes independently fetched by the authenticated host.
    /// The caller remains responsible for durable packet staging before any lifecycle transition.
    /// </summary>
    public static ExecutionAcceptedCiAiWorkflowEvidence Consume(
        ReadOnlyMemory<byte> packetBytes,
        string expectedRepository,
        long expectedRunId,
        int expectedRunAttempt,
        string expectedPacketSha256,
        IEnumerable<CiWorkflowRequiredCheckBinding> expectedRequiredChecks,
        string expectedWorkflowRef,
        string expectedWorkflowSha,
        string expectedCandidateRef,
        string expectedCandidateSha,
        string expectedEventName,
        string expectedEventRef,
        string expectedEventSha,
        CiWorkflowRunEvidenceObservation independentlyObservedRun,
        CiWorkflowDefinitionTrustPolicy protectedWorkflowPolicy,
        ReadOnlyMemory<byte> workflowBytesAtRunRevision,
        ReadOnlyMemory<byte> workflowBytesAtApprovedRevision,
        JsonlCiAiWorkflowEvidencePacketStore durableStore,
        string taskId,
        string correlationId,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(expectedRequiredChecks);
        ArgumentNullException.ThrowIfNull(independentlyObservedRun);
        ArgumentNullException.ThrowIfNull(protectedWorkflowPolicy);
        ArgumentNullException.ThrowIfNull(durableStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWorkflowRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWorkflowSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCandidateRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCandidateSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEventRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEventSha);

        if (packetBytes.IsEmpty || packetBytes.Length > MaximumPacketBytes)
            throw new InvalidDataException("CiAi workflow-evidence packet is empty or exceeds the four-megabyte limit.");
        if (workflowBytesAtRunRevision.IsEmpty ||
            workflowBytesAtRunRevision.Length > MaximumWorkflowDefinitionBytes ||
            workflowBytesAtApprovedRevision.IsEmpty ||
            workflowBytesAtApprovedRevision.Length > MaximumWorkflowDefinitionBytes)
            throw new InvalidDataException("Both independently retrieved workflow definitions must be non-empty and within the 256 KiB limit.");

        if (protectedWorkflowPolicy.Repository != expectedRepository ||
            protectedWorkflowPolicy.WorkflowPath.Length == 0 ||
            !expectedWorkflowRef.StartsWith(
                expectedRepository + "/" + protectedWorkflowPolicy.WorkflowPath + "@",
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Protected workflow policy does not identify the persisted repository and workflow path.");

        var packetSha256 = Convert.ToHexString(SHA256.HashData(packetBytes.Span)).ToLowerInvariant();
        if (expectedPacketSha256.Length != 64 ||
            !expectedPacketSha256.All(Uri.IsHexDigit) ||
            !string.Equals(expectedPacketSha256, expectedPacketSha256.ToLowerInvariant(), StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(packetSha256), Convert.FromHexString(expectedPacketSha256)))
            throw new InvalidDataException("CiAi workflow-evidence packet digest does not match the persisted provider artifact identity.");

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(packetBytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("CiAi workflow-evidence packet must contain strict UTF-8 JSON.", exception);
        }

        var requiredChecks = expectedRequiredChecks.ToArray();
        // The first parse validates the producer-owned structure and provenance without trusting
        // the packet's historical trust state. The host then evaluates exact workflow bytes.
        var untrustedPacket = CiWorkflowEvidencePacket.FromJson(
            json, expectedRepository, expectedRunId, expectedRunAttempt,
            packetSha256, requiredChecks, workflowDefinitionTrusted: false);

        var provenance = untrustedPacket.Provenance;
        RequireObservedRunMatches(untrustedPacket.ObservedRun, independentlyObservedRun);
        RequireEqual(provenance.WorkflowRef, expectedWorkflowRef, "workflow definition ref");
        RequireEqualSha(provenance.WorkflowSha, expectedWorkflowSha, "workflow definition SHA");
        RequireEqual(provenance.WorkflowPath, protectedWorkflowPolicy.WorkflowPath, "workflow path");
        RequireEqual(provenance.EventName, expectedEventName, "event name");
        RequireEqual(provenance.EventRef, expectedEventRef, "event ref");
        RequireEqualSha(provenance.EventSha, expectedEventSha, "event SHA");
        RequireEqual(untrustedPacket.CandidateRef, expectedCandidateRef, "candidate ref");
        RequireEqualSha(untrustedPacket.CandidateSha, expectedCandidateSha, "candidate SHA");

        var trustDecision = protectedWorkflowPolicy.Evaluate(
            provenance, workflowBytesAtRunRevision, workflowBytesAtApprovedRevision);
        if (!trustDecision.IsFor(provenance))
            throw new UnauthorizedAccessException("Protected workflow trust decision is not bound to the exact packet provenance.");

        // This is the authoritative reload: CiAi reconstructs its packet and required-check
        // observations using the newly evaluated host trust decision, not the serialized flag.
        var packet = CiWorkflowEvidencePacket.FromJson(
            json, expectedRepository, expectedRunId, expectedRunAttempt,
            packetSha256, requiredChecks, trustDecision);

        var verdict = !packet.EvidenceComplete || !packet.WorkflowDefinitionTrusted || !packet.CheckoutVerified
            ? "IncompleteOrUntrusted"
            : packet.IsSuccessful
                ? "CompletePass"
                : packet.HasActionableDiagnostics
                    ? "ActionableFailure"
                    : "NonActionableFailure";

        var staged = durableStore.Store(
            taskId, correlationId, expectedRepository, expectedRunId, expectedRunAttempt,
            packet.CandidateSha, packetSha256, packetBytes, receivedAt);
        if (staged == CiAiWorkflowEvidencePacketStoreOutcome.Conflict)
            throw new InvalidDataException("A different CiAi packet is already staged for this task/run/attempt identity.");

        return new ExecutionAcceptedCiAiWorkflowEvidence(packet, packetSha256, verdict);
    }

    private static void RequireObservedRunMatches(
        CiWorkflowRunEvidenceObservation packetObservation,
        CiWorkflowRunEvidenceObservation hostObservation)
    {
        // The packet is producer-supplied. Compare its complete provider-facing run/job facts
        // with a separate observation assembled by the authenticated Execution host.
        var sameRun =
            string.Equals(packetObservation.Repository, hostObservation.Repository, StringComparison.OrdinalIgnoreCase) &&
            packetObservation.WorkflowPath == hostObservation.WorkflowPath &&
            packetObservation.RunId == hostObservation.RunId &&
            packetObservation.RunAttempt == hostObservation.RunAttempt &&
            packetObservation.WorkflowRef == hostObservation.WorkflowRef &&
            string.Equals(packetObservation.WorkflowSha, hostObservation.WorkflowSha, StringComparison.OrdinalIgnoreCase) &&
            packetObservation.Event == hostObservation.Event &&
            packetObservation.WorkflowName == hostObservation.WorkflowName &&
            packetObservation.Status == hostObservation.Status &&
            packetObservation.Conclusion == hostObservation.Conclusion &&
            packetObservation.RunEvidenceUrl == hostObservation.RunEvidenceUrl &&
            packetObservation.DiscoveryComplete == hostObservation.DiscoveryComplete &&
            packetObservation.JobsComplete == hostObservation.JobsComplete &&
            packetObservation.CandidateCheckoutEvidence == hostObservation.CandidateCheckoutEvidence &&
            packetObservation.CandidateCheckoutVerified == hostObservation.CandidateCheckoutVerified &&
            packetObservation.ArtifactReferences.SequenceEqual(hostObservation.ArtifactReferences) &&
            packetObservation.Jobs.Count == hostObservation.Jobs.Count &&
            packetObservation.Jobs.Zip(hostObservation.Jobs).All(pair =>
                string.Equals(JsonSerializer.Serialize(pair.First), JsonSerializer.Serialize(pair.Second), StringComparison.Ordinal));
        if (!sameRun)
            throw new UnauthorizedAccessException("CiAi packet run/job observations do not match the independent provider observation.");
    }

    private static void RequireEqual(string actual, string expected, string identity)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi workflow-evidence " + identity + " does not match persisted dispatch intent.");
    }

    private static void RequireEqualSha(string actual, string expected, string identity)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi workflow-evidence " + identity + " does not match persisted dispatch intent.");
    }
}
