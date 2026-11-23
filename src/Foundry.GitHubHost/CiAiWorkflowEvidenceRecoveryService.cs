using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Revalidates durable evidence stages during host startup. A journal record is not
/// trusted merely because it survived restart: its dispatch mapping, exact run binding,
/// accepted notice receipt, packet digest, and CiAi v2 provenance are checked again.
/// This service deliberately does not acknowledge providers or wake tasks.
/// </summary>
public sealed class CiAiWorkflowEvidenceRecoveryService
{
    private readonly JsonlCiAiWorkflowEvidenceStageStore _stages;
    private readonly JsonlCiAiRequestTaskMappingJournal _mappings;
    private readonly JsonlCiAiArtifactNoticeReceiptLedger _receipts;
    private readonly CiAiWorkflowEvidencePacketValidator _validator;

    public CiAiWorkflowEvidenceRecoveryService(
        JsonlCiAiWorkflowEvidenceStageStore stages,
        JsonlCiAiRequestTaskMappingJournal mappings,
        JsonlCiAiArtifactNoticeReceiptLedger receipts,
        CiAiWorkflowEvidencePacketValidator validator)
    {
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Rebuilds the host's validated evidence view from durable state. Any missing,
    /// cancelled, expired, or identity-mismatched dependency fails closed and prevents
    /// the host from proceeding with acknowledgement or admission reevaluation.
    /// </summary>
    public IReadOnlyList<CiAiVerifiedWorkflowEvidence> RestoreValidatedEvidence(DateTimeOffset now)
    {
        if (now == default)
            throw new ArgumentException("A recovery timestamp is required.", nameof(now));

        var receiptsById = _receipts.GetAll().ToDictionary(item => item.ReceiptId, StringComparer.Ordinal);
        var restored = new List<CiAiVerifiedWorkflowEvidence>();

        foreach (var stage in _stages.GetAll())
        {
            if (!receiptsById.ContainsKey(stage.ReceiptId))
                throw new InvalidDataException($"Staged evidence '{stage.RequestId}' references a missing durable notice receipt.");

            var mapping = _mappings.Get(stage.RequestId);
            var identity = mapping.Mapping;
            if (mapping.Cancelled || mapping.RunBinding is null ||
                !string.Equals(identity.RequestId, stage.RequestId, StringComparison.Ordinal) ||
                !string.Equals(identity.TaskId, stage.TaskId, StringComparison.Ordinal) ||
                !string.Equals(identity.OperationId, stage.OperationId, StringComparison.Ordinal) ||
                !string.Equals(identity.IdempotencyKey, stage.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(identity.Repository, stage.Repository, StringComparison.Ordinal) ||
                mapping.RunBinding.RunId != stage.RunId ||
                mapping.RunBinding.RunAttempt != stage.RunAttempt)
                throw new InvalidDataException($"Staged evidence '{stage.RequestId}' no longer matches its immutable dispatch mapping and exact run binding.");

            var verified = _validator.Validate(stage.GetPacketBytes(), mapping, now);
            if (!string.Equals(verified.PacketSha256, stage.PacketSha256, StringComparison.Ordinal) ||
                !string.Equals(verified.RequestId, stage.RequestId, StringComparison.Ordinal) ||
                !string.Equals(verified.TaskId, stage.TaskId, StringComparison.Ordinal) ||
                !string.Equals(verified.OperationId, stage.OperationId, StringComparison.Ordinal) ||
                !string.Equals(verified.IdempotencyKey, stage.IdempotencyKey, StringComparison.Ordinal) ||
                verified.RunBinding.RunId != stage.RunId ||
                verified.RunBinding.RunAttempt != stage.RunAttempt)
                throw new InvalidDataException($"Staged evidence '{stage.RequestId}' failed exact packet provenance verification during recovery.");

            restored.Add(verified);
        }

        return Array.AsReadOnly(restored.ToArray());
    }
}
