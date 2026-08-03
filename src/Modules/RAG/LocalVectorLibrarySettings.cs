using System.Text.Json;
using System.Text.Json.Serialization;
using Ali;
using Ali.Modules.Embeddings;

namespace Ali.Modules.RAG;

public sealed record LocalVectorLibrarySettings
{
    public const string DefaultEmbeddingProvider = LocalEmbeddingProviders.Custom;

    public const string DefaultEmbeddingEndpoint = "";

    public const string DefaultEmbeddingModel = "";

    public const int DefaultEmbeddingDimensions = 768;

    public const int DefaultEmbeddingContextTokens = 8192;

    public bool Enabled { get; init; } = true;

    public bool SemanticToolRetrievalEnabled { get; init; }

    public bool UseManagedLocalQdrant { get; init; } = true;

    public bool AutoStartQdrant { get; init; } = true;

    public bool EnableRipgrep { get; init; } = true;

    public int RipgrepTimeoutSeconds { get; init; } = 5;

    public string QdrantHost { get; init; } = "127.0.0.1";

    public int QdrantHttpPort { get; init; } = 6333;

    public int QdrantGrpcPort { get; init; } = 6334;

    public bool QdrantUseTls { get; init; }

    public string QdrantApiKeyEnvironmentVariable { get; init; } = "ALI_QDRANT_API_KEY";

    public string QdrantCollectionName { get; init; } = "ali_local_library";

    public int QdrantRequestTimeoutSeconds { get; init; } = 15;

    public string RootDirectory { get; init; } = DefaultRootDirectory();

    public string EmbeddingProvider { get; init; } = DefaultEmbeddingProvider;

    public string EmbeddingEndpoint { get; init; } = DefaultEmbeddingEndpoint;

    public string EmbeddingModel { get; init; } = DefaultEmbeddingModel;

    public int EmbeddingDimensions { get; init; } = DefaultEmbeddingDimensions;

    public string EmbeddingProtocolIdentity { get; init; } =
        LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1;

    public int EmbeddingContextTokens { get; init; } = DefaultEmbeddingContextTokens;

    public EmbeddingPromptMode EmbeddingDocumentPromptMode { get; init; } =
        EmbeddingPromptMode.SearchDocument;

    public EmbeddingPromptMode EmbeddingQueryPromptMode { get; init; } =
        EmbeddingPromptMode.SearchQuery;

    public int ScanIntervalMinutes { get; init; } = 10;

    public int MaxFiles { get; init; } = 200;

    public long MaxFileBytes { get; init; } = 1_000_000;

    public int MaxChunksPerFile { get; init; } = 40;

    public int MaxRetrievedChunks { get; init; } = 4;

    public int ChunkCharacters { get; init; } = 1_400;

    public int ChunkOverlapCharacters { get; init; } = 200;

    public IReadOnlyList<string> AllowedExtensions { get; init; } =
    [
        ".txt",
        ".md",
        ".csv",
        ".json",
        ".xml",
        ".xaml",
        ".config",
        ".props",
        ".targets",
        ".sln",
        ".csproj",
        ".cs",
        ".log"
    ];

    public static string DefaultRootDirectory()
    {
        return Path.Combine(AliServices.DesktopUserDataRoot, "RAG", "Library");
    }

    internal static string LegacyDefaultRootDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents");
        }

        return Path.Combine(documents, "AliRag");
    }
}

public static class LocalVectorLibrarySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static LocalVectorLibrarySettingsStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "Sources", "local_vector_library_settings.json");

    public static string GetQdrantDataPath(string dataRoot) =>
        Path.Combine(ResolveUserDataRoot(dataRoot), "RAG", "Qdrant");

    public static string GetScanStatePath(string dataRoot) =>
        Path.Combine(ResolveUserDataRoot(dataRoot), "RAG", "local_library_scan_state.json");

    public static string GetEmbeddingSpaceMarkerPath(string dataRoot) =>
        Path.Combine(ResolveUserDataRoot(dataRoot), "RAG", "local_library_embedding_space.sha256");

    public static LocalVectorLibrarySettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return new LocalVectorLibrarySettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasProperty(root, "embeddingEndpoint")
                || !HasProperty(root, "embeddingModel"))
            {
                return InvalidEmbeddingFallback();
            }

            var settings = document.RootElement.Deserialize<LocalVectorLibrarySettings>(JsonOptions)
                           ?? InvalidEmbeddingFallback();
            return settings with
            {
                EmbeddingProvider = HasProperty(root, "embeddingProvider")
                    ? settings.EmbeddingProvider
                    : LocalEmbeddingProviders.Custom,
                EmbeddingDimensions = HasProperty(root, "embeddingDimensions")
                    ? settings.EmbeddingDimensions
                    : LocalVectorLibrarySettings.DefaultEmbeddingDimensions,
                EmbeddingProtocolIdentity = HasProperty(root, "embeddingProtocolIdentity")
                    ? settings.EmbeddingProtocolIdentity
                    : LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
                EmbeddingContextTokens = HasProperty(root, "embeddingContextTokens")
                    ? settings.EmbeddingContextTokens
                    : LocalVectorLibrarySettings.DefaultEmbeddingContextTokens,
                EmbeddingDocumentPromptMode = HasProperty(root, "embeddingDocumentPromptMode")
                    ? settings.EmbeddingDocumentPromptMode
                    : EmbeddingPromptMode.SearchDocument,
                EmbeddingQueryPromptMode = HasProperty(root, "embeddingQueryPromptMode")
                    ? settings.EmbeddingQueryPromptMode
                    : EmbeddingPromptMode.SearchQuery
            };
        }
        catch (JsonException)
        {
            return InvalidEmbeddingFallback();
        }
        catch (IOException)
        {
            return InvalidEmbeddingFallback();
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidEmbeddingFallback();
        }
    }

    public static void Save(string dataRoot, LocalVectorLibrarySettings settings)
    {
        var path = GetSettingsPath(dataRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void MoveLegacyDefaultRootIfNeeded(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using (var stream = File.OpenRead(path))
            using (var document = JsonDocument.Parse(stream))
            {
                var root = document.RootElement;
                if (!HasProperty(root, "embeddingProvider")
                    || !HasProperty(root, "embeddingDimensions"))
                {
                    // Loading an older compatible file may synthesize these two
                    // in memory, but startup must not publish those values back
                    // until the user explicitly saves the current settings.
                    return;
                }
            }

            var settings = LoadOrDefault(dataRoot);

            var legacyRoot = Path.GetFullPath(LocalVectorLibrarySettings.LegacyDefaultRootDirectory());
            var currentRoot = Path.GetFullPath(settings.RootDirectory);
            if (!currentRoot.Equals(legacyRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Save(dataRoot, settings with { RootDirectory = LocalVectorLibrarySettings.DefaultRootDirectory() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    public static void WriteExample(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        Save(dataRoot, new LocalVectorLibrarySettings());
    }

    private static string ResolveUserDataRoot(string dataRoot)
    {
        var fullPath = Path.GetFullPath(dataRoot);
        if (string.Equals(Path.GetFileName(fullPath), "Settings", StringComparison.OrdinalIgnoreCase)
            && Directory.GetParent(fullPath) is { } parent)
        {
            return Path.Combine(parent.FullName, "Data");
        }

        return fullPath;
    }

    private static bool HasProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(property =>
            string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));

    private static LocalVectorLibrarySettings InvalidEmbeddingFallback() =>
        new()
        {
            EmbeddingProvider = string.Empty,
            EmbeddingEndpoint = string.Empty,
            EmbeddingModel = string.Empty,
            EmbeddingDimensions = LocalVectorLibrarySettings.DefaultEmbeddingDimensions,
            EmbeddingProtocolIdentity = LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            EmbeddingContextTokens = LocalVectorLibrarySettings.DefaultEmbeddingContextTokens,
            EmbeddingDocumentPromptMode = EmbeddingPromptMode.SearchDocument,
            EmbeddingQueryPromptMode = EmbeddingPromptMode.SearchQuery
        };
}
