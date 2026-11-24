namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Canonical response contract for the first bounded ADP implementation turn.
/// The operation names mirror the closed WorkTaskAction vocabulary. The model can only
/// suggest operations; host policy and capability checks decide whether they may run.
/// </summary>
public static class ExecutionHeadlessAiResponseSchema
{
    public const string JsonSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "diagnosis": { "type": ["string", "null"] },
        "patchIntent": { "type": ["string", "null"] },
        "questions": { "type": "array", "maxItems": 64, "items": { "type": "string", "maxLength": 2048 } },
        "confidence": { "type": ["number", "null"], "minimum": 0, "maximum": 1 },
        "uncertainty": { "type": ["string", "null"] },
        "operationalStatus": { "type": ["string", "null"] },
        "currentAction": { "type": ["string", "null"] },
        "blocker": { "type": ["string", "null"] },
        "nextExpectedUpdate": { "type": ["string", "null"] },
        "stopOrEscalateReason": { "type": ["string", "null"] },
        "humanDecisionRequired": { "type": ["string", "null"] },
        "operations": {
          "type": "array",
          "maxItems": 16,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["operation", "summary", "rationale", "argumentsJson"],
            "properties": {
              "operation": {
                "type": "string",
                "enum": ["ReadRepository", "WriteFiles", "CreateBranch", "CreateCommit", "RequestCiRun", "CreateDraftPullRequest"]
              },
              "summary": { "type": "string", "maxLength": 1024 },
              "rationale": { "type": "string", "maxLength": 4096 },
              "argumentsJson": { "type": "string", "maxLength": 65536 }
            }
          }
        }
      },
      "required": ["diagnosis", "patchIntent", "questions", "confidence", "uncertainty", "operationalStatus", "currentAction", "blocker", "nextExpectedUpdate", "stopOrEscalateReason", "humanDecisionRequired", "operations"]
    }
    """;
}
