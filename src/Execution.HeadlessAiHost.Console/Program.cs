using System.Text.Json;
using TheSingularityWorkshop.Execution;
using TheSingularityWorkshop.HeadlessAi;

if (args.Length != 8)
{
    Console.Error.WriteLine(
        "Usage: <task-id> <correlation-id> <owner/repo> <issue-number> <base-sha> <candidate-sha> <feature-branch> <allowed-path>");
    return 2;
}

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
var prompt = GetEnvironmentValue("EXECUTION_AGENT_PROMPT", "FOUNDRY_AGENT_PROMPT");
if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model) ||
    string.IsNullOrWhiteSpace(prompt))
{
    Console.Error.WriteLine(
        "Set OPENAI_API_KEY, OPENAI_MODEL, and EXECUTION_AGENT_PROMPT (or legacy FOUNDRY_AGENT_PROMPT) in the host environment. Secrets are read only at request time and are never printed.");
    return 2;
}

if (!int.TryParse(args[3], out var issueNumber) || issueNumber <= 0)
{
    Console.Error.WriteLine("Issue number must be a positive integer.");
    return 2;
}

var now = DateTimeOffset.UtcNow;
var repository = args[2];
var taskId = args[0];
var correlationId = args[1];
var issueUrl = $"https://github.com/{repository}/issues/{issueNumber}";
var branch = args[6];
var allowedPath = args[7];
var legacyDataDirectory = Path.Combine(Environment.CurrentDirectory, ".foundry-host-data");
var defaultDataDirectory = Directory.Exists(legacyDataDirectory)
    ? legacyDataDirectory
    : Path.Combine(Environment.CurrentDirectory, ".execution-host-data");
var dataDirectory = Path.GetFullPath(
    GetEnvironmentValue("EXECUTION_DATA_DIR", "FOUNDRY_DATA_DIR") ?? defaultDataDirectory);
Directory.CreateDirectory(dataDirectory);

var taskStore = new JsonlWorkTaskStore(Path.Combine(dataDirectory, "tasks.jsonl"));
var persistentTasks = new PersistentWorkTaskCoordinator(taskStore);
var task = persistentTasks.Submit(new WorkTaskRequest(
    WorkTaskRequest.CurrentSchemaVersion,
    taskId,
    correlationId,
    repository,
    issueNumber,
    issueUrl,
    args[4],
    new[] { allowedPath },
    MaximumAttempts: 3));

var lease = persistentTasks.Claim(task.Request.TaskId, "execution-headlessai-host",
    TimeSpan.FromMinutes(15), now);
persistentTasks.Transition(task.Request.TaskId, lease.Lease!.LeaseId,
    WorkTaskStatus.Investigating, now, "Host began one bounded HeadlessAi investigation turn.");

var profile = new HeadlessAiProfile(
    "execution-openai-host",
    new Uri("https://api.openai.com/v1/responses"),
    timeout: TimeSpan.FromMinutes(2),
    settings: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = model
    });

using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var template = new HeadlessAiAgentTemplate(
    profile,
    new OpenAiResponsesAdapter(),
    new EnvironmentApiKeyHeaderProvider());
var executor = new HeadlessAiAgentTurnExecutor(template, httpClient);
var client = new HeadlessAiAgentTurnClient(executor);
var journal = new JsonlHeadlessAiTurnJournal(Path.Combine(dataDirectory, "headlessai-turns.jsonl"));
var coordinator = new ExecutionHeadlessAiTurnCoordinator(persistentTasks, client, journal);

var instructions = GetEnvironmentValue("EXECUTION_AGENT_INSTRUCTIONS", "FOUNDRY_AGENT_INSTRUCTIONS") ??
    "Analyze the supplied task context. Return a diagnosis and a bounded, non-executable patch intent. " +
    "Do not claim that files were inspected or changed unless that evidence is explicitly present.";
var result = await coordinator.InvokeAsync(
    task.Request.TaskId,
    lease.Lease!.LeaseId,
    branch,
    args[5],
    "implementation-agent",
    instructions,
    prompt,
    """{"diagnosis":"string","patchIntent":"string","operationalStatus":"string","currentAction":"string","blocker":"string","nextExpectedUpdate":"string","stopOrEscalateReason":"string","humanDecisionRequired":"string"}""");

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
}));
return 0;

static string? GetEnvironmentValue(string primaryName, string legacyName) =>
    Environment.GetEnvironmentVariable(primaryName) ?? Environment.GetEnvironmentVariable(legacyName);

internal sealed class EnvironmentApiKeyHeaderProvider : IHeadlessAiRequestHeaderProvider
{
    public ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(
        HeadlessAiProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {key}"
            };
        return ValueTask.FromResult(headers);
    }
}
