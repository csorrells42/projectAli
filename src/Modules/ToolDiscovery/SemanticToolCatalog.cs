using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Capabilities;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Microsoft.Extensions.AI;
using Qdrant.Client.Grpc;

namespace Ali.Modules.ToolDiscovery;

public interface ISemanticToolCatalog
{
    Task<SemanticToolSelection> SelectAsync(
        string need,
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyCollection<string> retainedToolNames,
        CancellationToken cancellationToken);

    Task<SemanticToolDiscoveryResult> DiscoverAsync(string need, CancellationToken cancellationToken);

    string CaptureBindingFingerprint(IReadOnlyList<AIFunctionDeclaration> liveTools) =>
        SemanticToolBindingFingerprint.Calculate(liveTools);
}

internal static class SemanticToolBindingFingerprint
{
    internal static string Calculate(IReadOnlyList<AIFunctionDeclaration> liveTools)
    {
        ArgumentNullException.ThrowIfNull(liveTools);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var tool in liveTools.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            Append(tool.Name);
            Append(tool.Description ?? string.Empty);
            Append(tool.JsonSchema.GetRawText());
            Append(tool.ReturnJsonSchema?.GetRawText() ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                hash.AppendData(bytes);
                hash.AppendData([0]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}

public sealed record SemanticToolSelection(
    IReadOnlyList<AIFunctionDeclaration> Tools,
    IReadOnlyList<string> Buckets,
    string Directory,
    bool UsedSemanticIndex,
    string Status,
    bool RequiresAttention = false);

public sealed record SemanticToolDiscoveryResult(
    string Need,
    IReadOnlyList<string> Buckets,
    IReadOnlyList<string> ToolNames,
    string Status);

internal sealed record ToolBucketDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string>? Requires = null,
    bool AlwaysVisible = false);

internal static class LiveSemanticToolDirectory
{
    internal const int MaximumDirectoryBuckets = 64;
    internal const int MaximumDirectoryCharacters = 12_000;

    private static readonly IReadOnlySet<string> RetiredBucketIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "external-coding-agents",
        "specialists-workflows"
    };

    public static IReadOnlyList<ToolBucketDefinition> CreateBuckets(
        IReadOnlyList<AIFunctionDeclaration> liveTools) =>
        SemanticToolBuckets.Create(liveTools)
            .Where(bucket => !RetiredBucketIds.Contains(bucket.Id))
            .ToArray();

    public static IReadOnlyList<ToolBucketDefinition> CreateDirectoryBuckets(
        IReadOnlyList<AIFunctionDeclaration> liveTools) =>
        SemanticToolBuckets.Create(liveTools, includeDisabled: true)
            .Where(bucket => !RetiredBucketIds.Contains(bucket.Id))
            .ToArray();

    public static string BuildBoundedDirectoryFor(
        IReadOnlyList<AIFunctionDeclaration> liveTools) =>
        BuildBoundedDirectory(CreateDirectoryBuckets(liveTools));

    public static string BuildBoundedDirectory(IReadOnlyList<ToolBucketDefinition> buckets)
    {
        var included = buckets.Take(MaximumDirectoryBuckets).ToArray();
        var lines = included
            .Select(bucket => SemanticToolBuckets.BuildDirectory([bucket]))
            .ToArray();
        var accepted = new List<string>(lines.Length);
        var characters = 0;
        foreach (var line in lines)
        {
            var separatorLength = accepted.Count == 0 ? 0 : Environment.NewLine.Length;
            if (characters + separatorLength + line.Length > MaximumDirectoryCharacters - 128)
            {
                break;
            }

            accepted.Add(line);
            characters += separatorLength + line.Length;
        }

        var omitted = buckets.Count - accepted.Count;
        if (omitted > 0)
        {
            accepted.Add($"- {omitted} additional drawer(s) omitted from this bounded group manifest.");
        }

        return string.Join(Environment.NewLine, accepted);
    }
}

internal static class SafeSemanticToolFallback
{
    internal const int MaximumToolSchemas = 8;
    private const int MaximumStatusCharacters = 512;

    public static SemanticToolSelection Create(
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyList<ToolBucketDefinition> buckets,
        IReadOnlyCollection<string> retainedToolNames,
        string requestedGroupId,
        string directory,
        string status,
        bool requiresAttention = true)
    {
        var liveByName = liveTools
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var requested = buckets.SingleOrDefault(
            bucket => string.Equals(bucket.Id, requestedGroupId, StringComparison.Ordinal));
        var selectedGroupIds = ExpandDependencies(
            buckets,
            buckets.Where(static bucket => bucket.AlwaysVisible).Select(static bucket => bucket.Id)
                .Concat(requested is null ? [] : [requested.Id]));
        var candidateNames = buckets
            .Where(static bucket => bucket.AlwaysVisible)
            .SelectMany(static bucket => bucket.ToolNames)
            .Concat(buckets
                .Where(bucket => selectedGroupIds.Contains(bucket.Id) && !bucket.AlwaysVisible)
                .OrderByDescending(bucket => requested is not null && bucket.Id == requested.Id)
                .ThenBy(static bucket => bucket.Id, StringComparer.Ordinal)
                .SelectMany(static bucket => bucket.ToolNames))
            .Concat(retainedToolNames.OrderBy(name => name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumToolSchemas);
        var selectedTools = candidateNames
            .Where(liveByName.ContainsKey)
            .Select(name => liveByName[name])
            .ToArray();
        var selectedNames = selectedTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var selectedBuckets = buckets
            .Where(bucket => bucket.ToolNames.Any(selectedNames.Contains))
            .Select(bucket => bucket.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var compactStatus = status.ReplaceLineEndings(" ").Trim();
        if (compactStatus.Length > MaximumStatusCharacters)
        {
            compactStatus = compactStatus[..MaximumStatusCharacters] + "...";
        }

        return new(
            selectedTools,
            selectedBuckets,
            directory,
            false,
            compactStatus
                + $" Full-registry schemas were withheld; {selectedTools.Length} retained or discovery schema(s) "
                + "and the bounded group manifest remain available for a fresh planning pass. "
                + (requested is null
                    ? "No exact enabled groupId was requested."
                    : $"Opened exact groupId '{requested.Id}' mechanically."),
            RequiresAttention: requiresAttention);
    }

    private static HashSet<string> ExpandDependencies(
        IReadOnlyList<ToolBucketDefinition> buckets,
        IEnumerable<string> roots)
    {
        var byId = buckets.ToDictionary(static bucket => bucket.Id, StringComparer.Ordinal);
        var selected = new HashSet<string>(roots, StringComparer.Ordinal);
        var pending = new Queue<string>(selected);
        while (pending.TryDequeue(out var id))
        {
            if (!byId.TryGetValue(id, out var bucket) || bucket.Requires is null)
            {
                continue;
            }

            foreach (var requirement in bucket.Requires.Where(selected.Add))
            {
                pending.Enqueue(requirement);
            }
        }

        return selected;
    }
}

internal sealed class RegistryOnlySemanticToolCatalog : ISemanticToolCatalog
{
    public Task<SemanticToolSelection> SelectAsync(
        string need,
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyCollection<string> retainedToolNames,
        CancellationToken cancellationToken)
    {
        var buckets = LiveSemanticToolDirectory.CreateBuckets(liveTools);
        return Task.FromResult(SafeSemanticToolFallback.Create(
            liveTools,
            buckets,
            retainedToolNames,
            need,
            LiveSemanticToolDirectory.BuildBoundedDirectoryFor(liveTools),
            "Semantic indexing is unavailable."));
    }

    public Task<SemanticToolDiscoveryResult> DiscoverAsync(string need, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SemanticToolDiscoveryResult(
            need,
            [],
            [],
            "Discovery requires the current planning pass to provide its live registry; no cross-turn tool snapshot is retained."));
    }
}

internal sealed class SettingsAwareSemanticToolCatalog : ISemanticToolCatalog
{
    private readonly QdrantSemanticToolCatalog _semanticCatalog;
    private readonly Func<LocalVectorLibrarySettings> _settings;

    internal SettingsAwareSemanticToolCatalog(
        HttpClient httpClient,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _semanticCatalog = new QdrantSemanticToolCatalog(httpClient, qdrant, settings);
    }

    public Task<SemanticToolSelection> SelectAsync(
        string need,
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyCollection<string> retainedToolNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings();
        if (settings.SemanticToolRetrievalEnabled)
        {
            return _semanticCatalog.SelectAsync(
                need,
                liveTools,
                retainedToolNames,
                cancellationToken);
        }

        var buckets = LiveSemanticToolDirectory.CreateBuckets(liveTools);
        return Task.FromResult(SafeSemanticToolFallback.Create(
            liveTools,
            buckets,
            retainedToolNames,
            need,
            LiveSemanticToolDirectory.BuildBoundedDirectoryFor(liveTools),
            "Semantic tool retrieval is disabled in settings.",
            requiresAttention: false));
    }

    public Task<SemanticToolDiscoveryResult> DiscoverAsync(
        string need,
        CancellationToken cancellationToken) =>
        _settings().SemanticToolRetrievalEnabled
            ? _semanticCatalog.DiscoverAsync(need, cancellationToken)
            : Task.FromResult(new SemanticToolDiscoveryResult(
                need,
                [],
                [],
                "Semantic tool retrieval is disabled in settings."));
}

/// <summary>
/// Uses Ali's explicit local embedding endpoint and a separate Qdrant collection to propose a compact
/// set of tool drawers. Vector similarity generates candidates only; Ali's model remains the
/// sole interpreter and decides whether any loaded tool should run.
/// </summary>
internal sealed class QdrantSemanticToolCatalog : ISemanticToolCatalog
{
    internal const string CollectionNamePrefix = "ali_semantic_tool_catalog_v2";
    internal const int MaximumCandidateBuckets = 1;
    internal const int CollectionFingerprintCharacters = 64;
    private const int MaximumEmbeddingChunkCharacters = 700;

    private readonly OpenAiCompatibleEmbeddingClient _embeddingClient;
    private readonly QdrantServiceManager _qdrant;
    private readonly Func<LocalVectorLibrarySettings> _settings;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private string? _publishedIndexKey;

    public QdrantSemanticToolCatalog(
        HttpClient httpClient,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> settings)
    {
        _embeddingClient = new OpenAiCompatibleEmbeddingClient(httpClient);
        _qdrant = qdrant;
        _settings = settings;
    }

    public async Task<SemanticToolSelection> SelectAsync(
        string need,
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyCollection<string> retainedToolNames,
        CancellationToken cancellationToken)
    {
        var buckets = LiveSemanticToolDirectory.CreateBuckets(liveTools);
        var directory = LiveSemanticToolDirectory.BuildBoundedDirectoryFor(liveTools);
        if (liveTools.Count == 0)
        {
            return new([], [], directory, false, "No live model-callable tools were registered.");
        }
        if (buckets.Count == 0)
        {
            return SafeSemanticToolFallback.Create(
                liveTools,
                buckets,
                retainedToolNames,
                need,
                directory,
                "No live semantic drawers were available.");
        }

        // ExpandTools uses an exact enabled groupId when the planner followed the compact
        // manifest contract. Resolve that mechanical request before touching embeddings or
        // Qdrant; semantic retrieval is reserved for compatibility with older/prose callers.
        if (buckets.Any(bucket => string.Equals(bucket.Id, need, StringComparison.Ordinal)))
        {
            return SafeSemanticToolFallback.Create(
                liveTools,
                buckets,
                retainedToolNames,
                need,
                directory,
                "The planner requested an exact enabled groupId.",
                requiresAttention: false);
        }

        try
        {
            var settings = _settings();
            var runtime = await _qdrant.EnsureAvailableAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!runtime.IsReachable)
            {
                return SafeSemanticToolFallback.Create(
                    liveTools,
                    buckets,
                    retainedToolNames,
                    need,
                    directory,
                    runtime.Message);
            }

            var fingerprint = BuildFingerprint(liveTools, buckets, settings);
            var collectionName = await EnsureIndexedAsync(
                buckets,
                fingerprint,
                settings,
                cancellationToken).ConfigureAwait(false);
            var query = await CreateEmbeddingAsync(
                need,
                EmbeddingInputRole.RetrievalQuery,
                settings,
                cancellationToken).ConfigureAwait(false);
            if (query is null)
            {
                return SafeSemanticToolFallback.Create(
                    liveTools,
                    buckets,
                    retainedToolNames,
                    need,
                    directory,
                    "The embedding endpoint returned no query vector.");
            }

            using var client = _qdrant.CreateClient(settings);
            var matches = await client.QueryAsync(
                collectionName,
                query: query,
                limit: MaximumCandidateBuckets,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var matchedIds = matches
                .Select(point => ReadPayloadString(point.Payload, "bucket_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var selectedBucketIds = ExpandBucketDependencies(
                buckets,
                buckets.Where(bucket => bucket.AlwaysVisible).Select(bucket => bucket.Id).Concat(matchedIds));
            var selectedNames = new HashSet<string>(retainedToolNames, StringComparer.Ordinal);
            foreach (var bucket in buckets.Where(bucket => selectedBucketIds.Contains(bucket.Id)))
            {
                selectedNames.UnionWith(bucket.ToolNames);
            }

            var selectedTools = liveTools.Where(tool => selectedNames.Contains(tool.Name)).ToArray();
            if (selectedTools.Length == 0)
            {
                return SafeSemanticToolFallback.Create(
                    liveTools,
                    buckets,
                    retainedToolNames,
                    need,
                    directory,
                    "Semantic retrieval returned no live tool schemas.");
            }

            var selectedBuckets = buckets
                .Where(bucket => selectedBucketIds.Contains(bucket.Id))
                .Select(bucket => bucket.Name)
                .ToArray();
            return new(
                selectedTools,
                selectedBuckets,
                directory,
                true,
                $"Loaded {selectedTools.Length} of {liveTools.Count} live tools from {selectedBuckets.Length} semantic drawer(s).");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                   or Grpc.Core.RpcException or TimeoutException)
        {
            return SafeSemanticToolFallback.Create(
                liveTools,
                buckets,
                retainedToolNames,
                need,
                directory,
                $"Semantic tool retrieval failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}");
        }
    }

    public string CaptureBindingFingerprint(
        IReadOnlyList<AIFunctionDeclaration> liveTools)
    {
        ArgumentNullException.ThrowIfNull(liveTools);
        var buckets = LiveSemanticToolDirectory.CreateBuckets(liveTools);
        return BuildFingerprint(liveTools, buckets, _settings());
    }

    public Task<SemanticToolDiscoveryResult> DiscoverAsync(
        string need,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SemanticToolDiscoveryResult(
            need,
            [],
            [],
            "Discovery requires the current planning pass to provide its live registry; no cross-turn tool snapshot is retained."));
    }

    private async Task<string> EnsureIndexedAsync(
        IReadOnlyList<ToolBucketDefinition> buckets,
        string fingerprint,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        var collectionName = BuildCollectionName(fingerprint);
        var publicationKey = BuildPublicationKey(collectionName, settings);

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_publishedIndexKey, publicationKey, StringComparison.Ordinal))
            {
                return collectionName;
            }

            var vectors = new List<float[]>(buckets.Count);
            foreach (var bucket in buckets)
            {
                var vector = await CreateEmbeddingAsync(
                        BuildEmbeddingText(bucket),
                        EmbeddingInputRole.StoredDocument,
                        settings,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (vector is null)
                {
                    throw new InvalidOperationException($"No embedding was returned for the {bucket.Name} tool drawer.");
                }
                vectors.Add(vector);
            }

            using var client = _qdrant.CreateClient(settings);
            if (!await client.CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await client.CreateCollectionAsync(
                        collectionName,
                        new VectorParams { Size = checked((ulong)vectors[0].Length), Distance = Distance.Cosine },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
                {
                    // Another catalog instance safely published the same immutable fingerprint first.
                }
            }
            var points = buckets.Select((bucket, index) => new PointStruct
            {
                Id = CreatePointId(bucket.Id),
                Vectors = vectors[index],
                Payload =
                {
                    ["bucket_id"] = bucket.Id,
                    ["bucket_name"] = bucket.Name,
                    ["description"] = bucket.Description,
                    ["registry_fingerprint"] = fingerprint,
                    ["embedding_provider"] = settings.EmbeddingProvider,
                     ["embedding_endpoint"] = settings.EmbeddingEndpoint,
                     ["embedding_model"] = settings.EmbeddingModel,
                     ["embedding_dimensions"] = settings.EmbeddingDimensions,
                     ["embedding_protocol"] = settings.EmbeddingProtocolIdentity,
                     ["embedding_context_tokens"] = settings.EmbeddingContextTokens,
                     ["embedding_prompt_mode"] = settings.EmbeddingDocumentPromptMode.ToString()
                }
            }).ToArray();
            await client.UpsertAsync(collectionName, points, wait: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _publishedIndexKey = publicationKey;
            return collectionName;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<float[]?> CreateEmbeddingAsync(
        string input,
        EmbeddingInputRole role,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        var chunks = SplitEmbeddingInput(input, MaximumEmbeddingChunkCharacters);
        if (chunks.Count > 1)
        {
            var vectors = new List<float[]>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var chunkVector = await CreateSingleEmbeddingAsync(chunk, role, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (chunkVector is null)
                {
                    return null;
                }
                vectors.Add(chunkVector);
            }
            return AverageVectors(vectors);
        }

        return await CreateSingleEmbeddingAsync(chunks[0], role, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<float[]?> CreateSingleEmbeddingAsync(
        string input,
        EmbeddingInputRole role,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        if (!LocalEmbeddingConfiguration.TryCreate(
                settings.EmbeddingProvider,
                settings.EmbeddingEndpoint,
                settings.EmbeddingModel,
                settings.EmbeddingDimensions,
                settings.EmbeddingProtocolIdentity,
                settings.EmbeddingContextTokens,
                settings.EmbeddingDocumentPromptMode,
                settings.EmbeddingQueryPromptMode,
                out var configuration,
                out var failure))
        {
            throw new InvalidOperationException(failure);
        }

        var result = await _embeddingClient
            .CreateEmbeddingAsync(configuration!, input, role, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success || result.Vector is null)
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Vector;
    }

    private static IReadOnlyList<string> SplitEmbeddingInput(string input, int maximumCharacters)
    {
        var normalized = input.ReplaceLineEndings(" ").Trim();
        if (normalized.Length == 0)
        {
            return [string.Empty];
        }
        if (normalized.Length <= maximumCharacters)
        {
            return [normalized];
        }

        var chunks = new List<string>();
        var offset = 0;
        while (offset < normalized.Length)
        {
            var length = Math.Min(maximumCharacters, normalized.Length - offset);
            if (offset + length < normalized.Length)
            {
                var boundary = normalized.LastIndexOf(' ', offset + length - 1, length);
                if (boundary > offset)
                {
                    length = boundary - offset;
                }
            }

            var chunk = normalized.Substring(offset, length).Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }
            offset += length;
            while (offset < normalized.Length && char.IsWhiteSpace(normalized[offset]))
            {
                offset++;
            }
        }
        return chunks.Count == 0 ? [string.Empty] : chunks;
    }

    private static float[]? AverageVectors(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0 || vectors[0].Length == 0
            || vectors.Any(vector => vector.Length != vectors[0].Length))
        {
            return null;
        }

        var average = new float[vectors[0].Length];
        foreach (var vector in vectors)
        {
            for (var index = 0; index < average.Length; index++)
            {
                average[index] += vector[index];
            }
        }
        for (var index = 0; index < average.Length; index++)
        {
            average[index] /= vectors.Count;
        }
        return average;
    }

    private static HashSet<string> ExpandBucketDependencies(
        IReadOnlyList<ToolBucketDefinition> buckets,
        IEnumerable<string> initialIds)
    {
        var byId = buckets.ToDictionary(bucket => bucket.Id, StringComparer.Ordinal);
        var selected = new HashSet<string>(initialIds, StringComparer.Ordinal);
        var pending = new Queue<string>(selected);
        while (pending.TryDequeue(out var id))
        {
            if (!byId.TryGetValue(id, out var bucket) || bucket.Requires is null)
            {
                continue;
            }
            foreach (var dependency in bucket.Requires.Where(selected.Add))
            {
                pending.Enqueue(dependency);
            }
        }
        return selected;
    }

    private static string BuildEmbeddingText(ToolBucketDefinition bucket) =>
        $"Tool drawer: {bucket.Name}. Purpose: {bucket.Description}. Available operations: "
        + string.Join(", ", bucket.ToolNames);

    internal static string BuildFingerprint(
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        IReadOnlyList<ToolBucketDefinition> buckets,
        LocalVectorLibrarySettings settings)
    {
        var source = string.Join("\n", liveTools.OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(CapabilitySchemaIdentity.Calculate))
            + "\n" + string.Join("\n", buckets.Select(BuildEmbeddingText))
            + $"\n{settings.EmbeddingProvider}|{settings.EmbeddingEndpoint}|{settings.EmbeddingModel}|{settings.EmbeddingDimensions}"
            + $"|{settings.EmbeddingProtocolIdentity}|{settings.EmbeddingContextTokens}"
            + $"|{settings.EmbeddingDocumentPromptMode}|{settings.EmbeddingQueryPromptMode}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    internal static string BuildCollectionName(string fingerprint)
    {
        if (fingerprint.Length != CollectionFingerprintCharacters
            || fingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A full hexadecimal registry fingerprint is required.", nameof(fingerprint));
        }

        return CollectionNamePrefix + "_" + fingerprint[..CollectionFingerprintCharacters].ToLowerInvariant();
    }

    private static string BuildPublicationKey(string collectionName, LocalVectorLibrarySettings settings) =>
        string.Join(
            '\u001f',
            settings.QdrantHost,
            settings.QdrantGrpcPort,
            settings.QdrantUseTls,
            settings.QdrantApiKeyEnvironmentVariable,
            collectionName);

    private static Guid CreatePointId(string bucketId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(bucketId));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string ReadPayloadString(
        IDictionary<string, Qdrant.Client.Grpc.Value> payload,
        string key) =>
        payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;
}
