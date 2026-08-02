using System.Security.Cryptography;
using System.Text;
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
        return Task.FromResult(new SemanticToolSelection(
            liveTools.ToArray(),
            ["Complete live registry"],
            SemanticToolBuckets.BuildDirectory(buckets),
            false,
            "The complete effective live tool registry was supplied for this planning pass."));
    }

    public Task<SemanticToolDiscoveryResult> DiscoverAsync(string need, CancellationToken cancellationToken) =>
        Task.FromResult(new SemanticToolDiscoveryResult(
            need,
            ["Complete live registry"],
            [],
            "The current planning pass already contains its complete effective live registry; no cross-turn tool cache is used."));
}

/// <summary>
/// Uses Ali's explicit local embedding endpoint and a separate Qdrant collection to propose a compact
/// set of tool drawers. Vector similarity generates candidates only; Ali's model remains the
/// sole interpreter and decides whether any loaded tool should run.
/// </summary>
internal sealed class QdrantSemanticToolCatalog : ISemanticToolCatalog
{
    internal const string CollectionName = "ali_semantic_tool_catalog_v1";
    internal const int MaximumCandidateBuckets = 1;
    private const int MaximumEmbeddingChunkCharacters = 700;

    private readonly OpenAiCompatibleEmbeddingClient _embeddingClient;
    private readonly QdrantServiceManager _qdrant;
    private readonly Func<LocalVectorLibrarySettings> _settings;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private IReadOnlyList<AIFunctionDeclaration> _latestTools = [];
    private string? _indexedFingerprint;

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
        _latestTools = liveTools.ToArray();
        var buckets = LiveSemanticToolDirectory.CreateBuckets(liveTools);
        var directory = SemanticToolBuckets.BuildDirectory(buckets);
        if (liveTools.Count == 0)
        {
            return new([], [], directory, false, "No live model-callable tools were registered.");
        }

        try
        {
            var settings = _settings();
            var runtime = await _qdrant.EnsureAvailableAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!runtime.IsReachable)
            {
                return FullRegistryFallback(liveTools, directory, runtime.Message);
            }

            var fingerprint = BuildFingerprint(liveTools, buckets, settings);
            await EnsureIndexedAsync(buckets, fingerprint, settings, cancellationToken).ConfigureAwait(false);
            var query = await CreateEmbeddingAsync(need, settings, cancellationToken).ConfigureAwait(false);
            if (query is null)
            {
                return FullRegistryFallback(liveTools, directory, "The embedding endpoint returned no query vector.");
            }

            using var client = _qdrant.CreateClient(settings);
            var matches = await client.QueryAsync(
                CollectionName,
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
                return FullRegistryFallback(liveTools, directory, "Semantic retrieval returned no live tool schemas.");
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
            return FullRegistryFallback(
                liveTools,
                directory,
                $"Semantic tool retrieval failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}");
        }
    }

    public async Task<SemanticToolDiscoveryResult> DiscoverAsync(
        string need,
        CancellationToken cancellationToken)
    {
        var snapshot = _latestTools;
        if (snapshot.Count == 0)
        {
            return new(need, [], [], "The live registry has not been observed yet.");
        }

        var selection = await SelectAsync(need, snapshot, [], cancellationToken).ConfigureAwait(false);
        return new(
            need,
            selection.Buckets,
            selection.Tools.Select(tool => tool.Name).ToArray(),
            selection.Status);
    }

    private async Task EnsureIndexedAsync(
        IReadOnlyList<ToolBucketDefinition> buckets,
        string fingerprint,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        if (string.Equals(_indexedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_indexedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var vectors = new List<float[]>(buckets.Count);
            foreach (var bucket in buckets)
            {
                var vector = await CreateEmbeddingAsync(BuildEmbeddingText(bucket), settings, cancellationToken)
                    .ConfigureAwait(false);
                if (vector is null)
                {
                    throw new InvalidOperationException($"No embedding was returned for the {bucket.Name} tool drawer.");
                }
                vectors.Add(vector);
            }

            using var client = _qdrant.CreateClient(settings);
            if (await client.CollectionExistsAsync(CollectionName, cancellationToken).ConfigureAwait(false))
            {
                await client.DeleteCollectionAsync(CollectionName, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            await client.CreateCollectionAsync(
                CollectionName,
                new VectorParams { Size = checked((ulong)vectors[0].Length), Distance = Distance.Cosine },
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
                    ["embedding_dimensions"] = settings.EmbeddingDimensions
                }
            }).ToArray();
            await client.UpsertAsync(CollectionName, points, wait: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _indexedFingerprint = fingerprint;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<float[]?> CreateEmbeddingAsync(
        string input,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        var chunks = SplitEmbeddingInput(input, MaximumEmbeddingChunkCharacters);
        if (chunks.Count > 1)
        {
            var vectors = new List<float[]>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var chunkVector = await CreateSingleEmbeddingAsync(chunk, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (chunkVector is null)
                {
                    return null;
                }
                vectors.Add(chunkVector);
            }
            return AverageVectors(vectors);
        }

        return await CreateSingleEmbeddingAsync(chunks[0], settings, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<float[]?> CreateSingleEmbeddingAsync(
        string input,
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        if (!LocalEmbeddingConfiguration.TryCreate(
                settings.EmbeddingProvider,
                settings.EmbeddingEndpoint,
                settings.EmbeddingModel,
                settings.EmbeddingDimensions,
                out var configuration,
                out var failure))
        {
            throw new InvalidOperationException(failure);
        }

        var result = await _embeddingClient
            .CreateEmbeddingAsync(configuration!, input, cancellationToken)
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

    private static SemanticToolSelection FullRegistryFallback(
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        string directory,
        string status) =>
        new(
            liveTools.ToArray(),
            ["Complete live registry fallback"],
            directory,
            false,
            status + " The complete live registry was supplied so no capability was hidden.",
            RequiresAttention: true);

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
                .Select(tool => $"{tool.Name}|{tool.Description}"))
            + "\n" + string.Join("\n", buckets.Select(BuildEmbeddingText))
            + $"\n{settings.EmbeddingProvider}|{settings.EmbeddingEndpoint}|{settings.EmbeddingModel}|{settings.EmbeddingDimensions}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

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
