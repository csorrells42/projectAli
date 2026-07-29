using System.Text.Json;

namespace Ali.Modules.UserMemory;

public sealed record UserMemorySettings
{
    public bool Enabled { get; init; } = true;

    // Explicit "remember this" requests remain available. Broad extraction after
    // every reply is opt-in because tool output and transient task state are not
    // reliable personal facts and can also contend with foreground recall.
    public bool AutomaticBackgroundLearning { get; init; } = false;

    public int RecallMaximumResults { get; init; } = 5;

    public int RecallTimeoutMilliseconds { get; init; } = 2500;

    // Mem0 v1.1 returns a normalized hybrid score. When BM25 is available it
    // averages semantic and keyword evidence, so a strong 0.60 dense match plus
    // a useful keyword match commonly lands around 0.35. A 0.30 second-stage
    // floor rejects dense-only noise while preserving those supported matches;
    // Mem0's own semantic threshold has already gated the candidate set.
    public double RecallMinimumScore { get; init; } = 0.30;

    public double RecallScoreWindow { get; init; } = 0.05;

    // Semantic-only results need a stronger absolute score and a clear lead
    // over the next candidate. This rejects the tight 0.40-0.47 noise clusters
    // observed for facts that are not actually stored, while preserving a
    // distinct paraphrase match such as 0.57 versus 0.44.
    public double RecallSemanticOnlyMinimumScore { get; init; } = 0.50;

    public double RecallSemanticOnlyStrongScore { get; init; } = 0.65;

    public double RecallSemanticOnlyMinimumLead { get; init; } = 0.08;

    // A small nonzero BM25 signal is meaningful for exact names, codes, and
    // phrases whose normalized hybrid score can be lower than a dense match.
    public double RecallMinimumKeywordScore { get; init; } = 0.08;

    public string CollectionName { get; init; } = "ali_user_memories";

    public string LemonadeEndpoint { get; init; } = "http://127.0.0.1:13305/api/v1";

    public string LlmModel { get; init; } = "gpt-oss-20b-mxfp4-GGUF";

    public string EmbeddingEndpoint { get; init; } = "http://127.0.0.1:13305/api/v1";

    public string EmbeddingModel { get; init; } = "nomic-embed-text-v1-GGUF";

    public int EmbeddingDimensions { get; init; } = 768;

    public string QdrantHost { get; init; } = "127.0.0.1";

    public int QdrantHttpPort { get; init; } = 6333;

    public UserMemorySettings Normalize() => this with
    {
        RecallMaximumResults = Math.Clamp(RecallMaximumResults, 1, 8),
        RecallTimeoutMilliseconds = Math.Clamp(RecallTimeoutMilliseconds, 250, 5000),
        RecallMinimumScore = Math.Clamp(RecallMinimumScore, 0, 1),
        RecallScoreWindow = Math.Clamp(RecallScoreWindow, 0, 0.25),
        RecallSemanticOnlyMinimumScore = Math.Clamp(RecallSemanticOnlyMinimumScore, 0, 1),
        RecallSemanticOnlyStrongScore = Math.Clamp(RecallSemanticOnlyStrongScore, 0, 1),
        RecallSemanticOnlyMinimumLead = Math.Clamp(RecallSemanticOnlyMinimumLead, 0, 1),
        RecallMinimumKeywordScore = Math.Clamp(RecallMinimumKeywordScore, 0, 1),
        CollectionName = string.IsNullOrWhiteSpace(CollectionName) ? "ali_user_memories" : CollectionName.Trim(),
        LemonadeEndpoint = RequireLoopback(LemonadeEndpoint, nameof(LemonadeEndpoint)),
        EmbeddingEndpoint = RequireLoopback(EmbeddingEndpoint, nameof(EmbeddingEndpoint)),
        LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? "gpt-oss-20b-mxfp4-GGUF" : LlmModel.Trim(),
        EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModel) ? "nomic-embed-text-v1-GGUF" : EmbeddingModel.Trim(),
        EmbeddingDimensions = Math.Clamp(EmbeddingDimensions, 1, 8192),
        QdrantHost = QdrantHost.Trim() is "localhost" or "::1" ? QdrantHost.Trim() : "127.0.0.1",
        QdrantHttpPort = Math.Clamp(QdrantHttpPort, 1, 65535)
    };

    private static string RequireLoopback(string value, string name)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !uri.IsLoopback)
        {
            throw new InvalidOperationException($"{name} must be a loopback HTTP endpoint.");
        }

        return uri.ToString().TrimEnd('/');
    }
}
public static class UserMemorySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string GetPath(string dataRoot) => Path.Combine(dataRoot, "user-memory-settings.json");

    public static UserMemorySettings LoadOrDefault(string dataRoot)
    {
        try
        {
            var path = GetPath(dataRoot);
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<UserMemorySettings>(File.ReadAllText(path), JsonOptions) ?? new()).Normalize()
                : new UserMemorySettings().Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new UserMemorySettings().Normalize();
        }
    }

    public static void Save(string dataRoot, UserMemorySettings settings)
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(GetPath(dataRoot), JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
    }
}
