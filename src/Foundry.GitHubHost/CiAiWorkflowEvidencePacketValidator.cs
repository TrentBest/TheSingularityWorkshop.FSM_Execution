using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Validates the authoritative CiAi v2 workflow evidence packet against the immutable
/// dispatch intent and independently bound GitHub run/attempt. A valid packet is evidence,
/// not authorization; callers must still apply the configured dependency policy.
/// </summary>
public sealed class CiAiWorkflowEvidencePacketValidator
{
    public const int MaximumPacketBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public CiAiVerifiedWorkflowEvidence Validate(
        ReadOnlyMemory<byte> packetBytes,
        CiAiRequestTaskMappingSnapshot mappingSnapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mappingSnapshot);
        if (packetBytes.IsEmpty || packetBytes.Length > MaximumPacketBytes)
            throw new InvalidDataException("Workflow evidence packet must be non-empty and no larger than 16 MiB.");
        if (now == default)
            throw new ArgumentException("A validation timestamp is required.", nameof(now));

        var mapping = mappingSnapshot.Mapping.Validate();
        var binding = mappingSnapshot.RunBinding
            ?? throw new UnauthorizedAccessException("Workflow evidence cannot be accepted before an exact GitHub run is independently bound.");
        if (mappingSnapshot.Cancelled || now < mapping.CreatedAt || now > mapping.ExpiresAt)
            throw new UnauthorizedAccessException("Workflow evidence mapping is cancelled, expired, or not yet active.");
        binding.ValidateAgainst(mapping);

        string json;
        try
        {
            json = StrictUtf8.GetString(packetBytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Workflow evidence packet must be strict UTF-8.", exception);
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            RejectDuplicateProperties(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Workflow evidence packet is not bounded, valid JSON.", exception);
        }

        CiWorkflowEvidencePacket packet;
        try
        {
            packet = CiWorkflowEvidencePacket.FromJson(json);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException("CiAi rejected the workflow evidence packet contract or internal provenance.", exception);
        }

        if (packet.ContractVersion != CiWorkflowEvidencePacket.CurrentContractVersion ||
            !string.Equals(packet.Schema, CiWorkflowEvidencePacket.CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException("Workflow evidence packet contract version is unsupported.");

        RequireEqual(packet.Repository, mapping.Repository, "repository");
        RequireEqual(packet.WorkflowPath, mapping.WorkflowPath, "workflow path");
        RequireEqual(packet.WorkflowRef, binding.WorkflowRef, "workflow-definition ref");
        RequireEqual(packet.WorkflowRef, mapping.TrustedWorkflowRef, "registered trusted workflow ref");
        RequireEqual(packet.WorkflowSha, binding.WorkflowSha, "workflow-definition SHA");
        RequireEqual(packet.WorkflowSha, mapping.TrustedWorkflowSha, "registered trusted workflow SHA");
        RequireEqual(packet.CandidateRef, binding.CandidateRef, "candidate ref");
        RequireEqual(packet.CandidateRef, mapping.CandidateRef, "registered candidate ref");
        RequireEqual(packet.CandidateSha, binding.CandidateSha, "candidate SHA");
        RequireEqual(packet.CandidateSha, mapping.CandidateSha, "registered candidate SHA");
        RequireEqual(packet.Event, binding.Event, "workflow event");
        RequireEqual(packet.Event, "workflow_dispatch", "trusted dispatch event");

        if (packet.RunId != binding.RunId || packet.RunAttempt != binding.RunAttempt)
            throw new UnauthorizedAccessException("Workflow evidence packet does not identify the exact bound GitHub run and attempt.");

        var checkout = packet.CandidateCheckoutEvidence
            ?? throw new UnauthorizedAccessException("Trusted dispatch evidence must contain explicit candidate checkout provenance.");
        RequireEqual(checkout.CandidateRef, mapping.CandidateRef, "checkout candidate ref");
        RequireEqual(checkout.CandidateSha, mapping.CandidateSha, "checkout candidate SHA");
        RequireEqual(checkout.JobName, mapping.JobName, "registered checkout job");
        RequireEqual(checkout.ResolutionStepName, mapping.ResolutionStepName, "registered candidate-resolution step");
        RequireEqual(checkout.VerificationStepName, mapping.VerificationStepName, "registered candidate-checkout verification step");

        if (!packet.CandidateCheckoutVerified)
            throw new UnauthorizedAccessException("CiAi did not verify the candidate checkout steps for this exact run attempt.");
        if (!packet.EvidenceComplete)
            throw new InvalidDataException("Workflow evidence packet is incomplete: " + string.Join(",", packet.BlockingReasons));
        if (packet.Jobs.Any(job => job.RunId != binding.RunId || job.RunAttempt != binding.RunAttempt))
            throw new UnauthorizedAccessException("Workflow evidence contains job observations from a different run attempt.");

        var digest = Convert.ToHexString(SHA256.HashData(packetBytes.Span)).ToLowerInvariant();
        return new CiAiVerifiedWorkflowEvidence(packet, mapping.RequestId, mapping.TaskId,
            mapping.OperationId, mapping.IdempotencyKey, binding, digest, now);
    }

    private static void RequireEqual(string actual, string expected, string identity)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Workflow evidence {identity} does not match the immutable dispatch mapping.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Workflow evidence packet cannot contain duplicate JSON property names.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}

/// <summary>Immutable result of packet validation, retaining exact packet and mapping identity.</summary>
public sealed record CiAiVerifiedWorkflowEvidence(
    CiWorkflowEvidencePacket Packet,
    string RequestId,
    string TaskId,
    string OperationId,
    string IdempotencyKey,
    CiAiDispatchRunBinding RunBinding,
    string PacketSha256,
    DateTimeOffset VerifiedAt)
{
    /// <summary>Only a successful, complete workflow packet can satisfy a successful-build dependency.</summary>
    public bool IsSuccessful => Packet.IsSuccessful;
}