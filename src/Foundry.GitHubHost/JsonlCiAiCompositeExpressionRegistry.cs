using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>One immutable task-to-expression registration recovered from durable storage.</summary>
public sealed record CiAiRegisteredCompositeExpression(
    CompositeDependencyAdmissionIdentity Identity,
    CiAiPipelineGateExpression Expression);

/// <summary>
/// Durable, append-only registration of the exact CiAi expression owned by a waiting task.
/// Provider events use this registry to discover dependents; an event never supplies an
/// expression. The canonical CiAi digest is recomputed on registration and replay.
/// </summary>
public sealed class JsonlCiAiCompositeExpressionRegistry
{
    private sealed record GateNode(
        string Kind,
        string? GateId,
        string? Repository,
        string? CandidateId,
        string? CommitSha,
        IReadOnlyList<GateNode>? Children);

    private sealed record Registration(
        CompositeDependencyAdmissionIdentity Identity,
        GateNode Root);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, CiAiRegisteredCompositeExpression> _byTask =
        new(StringComparer.Ordinal);

    public JsonlCiAiCompositeExpressionRegistry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            Replay();
    }

    /// <summary>Registers an immutable expression before its task is admitted as Waiting.</summary>
    public CiAiRegisteredCompositeExpression Register(
        CompositeDependencyAdmissionIdentity identity,
        CiAiPipelineGateExpression expression)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expression);
        identity.Validate();
        ValidateBinding(identity, expression);
        var registration = new CiAiRegisteredCompositeExpression(identity, expression);
        var persisted = new Registration(identity, Capture(expression.Root));

        lock (_gate)
        {
            if (_byTask.TryGetValue(identity.TaskId, out var existing))
            {
                if (existing.Identity == identity &&
                    string.Equals(existing.Expression.DefinitionSha256, expression.DefinitionSha256,
                        StringComparison.OrdinalIgnoreCase))
                    return existing;
                throw new InvalidDataException(
                    "A task's composite expression registration is immutable and cannot be replaced.");
            }

            Append(persisted);
            _byTask.Add(identity.TaskId, registration);
            return registration;
        }
    }

    public CiAiRegisteredCompositeExpression Get(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
            return _byTask.TryGetValue(taskId, out var registration)
                ? registration
                : throw new KeyNotFoundException(
                    $"No durable CiAi expression is registered for task '{taskId}'.");
    }

    /// <summary>Returns only registrations whose canonical expression declares this exact gate ID.</summary>
    public IReadOnlyList<CiAiRegisteredCompositeExpression> FindDependents(string gateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        lock (_gate)
            return Array.AsReadOnly(_byTask.Values
                .Where(item => item.Expression.DependsOnGate(gateId))
                .OrderBy(item => item.Identity.TaskId, StringComparer.Ordinal)
                .ToArray());
    }

    private static void ValidateBinding(
        CompositeDependencyAdmissionIdentity identity,
        CiAiPipelineGateExpression expression)
    {
        if (!string.Equals(identity.Pipeline, expression.Pipeline, StringComparison.Ordinal) ||
            !string.Equals(identity.ExpressionSha256, expression.DefinitionSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Registered expression must match the task's immutable pipeline and canonical CiAi digest.");
    }

    private static GateNode Capture(CiAiGateExpression expression) => expression switch
    {
        CiAiRepositoryGateReference leaf => new GateNode(
            "leaf", leaf.GateId, leaf.Repository, leaf.CandidateId, leaf.CommitSha, null),
        CiAiAllOf all => new GateNode(
            "all", null, null, null, null, all.Children.Select(Capture).ToArray()),
        CiAiAnyOf any => new GateNode(
            "any", null, null, null, null, any.Children.Select(Capture).ToArray()),
        _ => throw new InvalidDataException("Unsupported CiAi expression node.")
    };

    private static CiAiGateExpression Restore(GateNode node, int depth, ref int count)
    {
        if (depth > CiAiPipelineGateExpression.MaximumDepth ||
            ++count > CiAiPipelineGateExpression.MaximumNodes)
            throw new InvalidDataException("Persisted CiAi expression exceeds the contract's structural limits.");

        if (node.Kind == "leaf" && node.GateId is not null &&
            node.Repository is not null && node.CandidateId is not null &&
            node.CommitSha is not null && node.Children is null)
            return new CiAiRepositoryGateReference(
                node.GateId, node.Repository, node.CandidateId, node.CommitSha);

        if (node.Kind is "all" or "any" && node.GateId is null &&
            node.Repository is null && node.CandidateId is null &&
            node.CommitSha is null && node.Children is { Count: >= 2 })
        {
            var children = new List<CiAiGateExpression>(node.Children.Count);
            foreach (var child in node.Children)
                children.Add(Restore(child, depth + 1, ref count));
            return node.Kind == "all"
                ? new CiAiAllOf(children)
                : new CiAiAnyOf(children);
        }

        throw new InvalidDataException("Persisted CiAi expression node is malformed.");
    }

    private void Append(Registration registration)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(registration, JsonOptions) + "\n");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10)
                throw new InvalidDataException("CiAi expression registry has an incomplete final record.");
            stream.Position = 0;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            try
            {
                var persisted = JsonSerializer.Deserialize<Registration>(line, JsonOptions)
                    ?? throw new InvalidDataException("Null CiAi expression registration.");
                persisted.Identity.Validate();
                var count = 0;
                var root = Restore(persisted.Root, 1, ref count);
                var expression = CiAiPipelineGateExpression.ForPipeline(persisted.Identity.Pipeline, root);
                ValidateBinding(persisted.Identity, expression);
                if (!_byTask.TryAdd(persisted.Identity.TaskId,
                    new CiAiRegisteredCompositeExpression(persisted.Identity, expression)))
                    throw new InvalidDataException("CiAi expression task identity was registered more than once.");
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or
                                               InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException(
                    $"CiAi expression registry record {lineNumber} is invalid.", exception);
            }
        }
    }
}
