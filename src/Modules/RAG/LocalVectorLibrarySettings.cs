using System.Text.Json;
using Ali;

namespace Ali.Modules.RAG;

public sealed record LocalVectorLibrarySettings
{
    public const string DefaultEmbeddingEndpoint = "http://127.0.0.1:13305/api/v1/embeddings";

    public const string DefaultEmbeddingModel = "nomic-embed-text-v1-GGUF";

    public bool Enabled { get; init; } = true;

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

    public string EmbeddingEndpoint { get; init; } = DefaultEmbeddingEndpoint;

    public string EmbeddingModel { get; init; } = DefaultEmbeddingModel;

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

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "Sources", "local_vector_library_settings.json");

    public static string GetQdrantDataPath(string dataRoot) =>
        Path.Combine(ResolveUserDataRoot(dataRoot), "RAG", "Qdrant");

    public static string GetScanStatePath(string dataRoot) =>
        Path.Combine(ResolveUserDataRoot(dataRoot), "RAG", "local_library_scan_state.json");

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
            return JsonSerializer.Deserialize<LocalVectorLibrarySettings>(stream, JsonOptions)
                   ?? new LocalVectorLibrarySettings();
        }
        catch (JsonException)
        {
            return new LocalVectorLibrarySettings();
        }
        catch (IOException)
        {
            return new LocalVectorLibrarySettings();
        }
    }

    public static void Save(string dataRoot, LocalVectorLibrarySettings settings)
    {
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
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
            LocalVectorLibrarySettings? settings;
            using (var stream = File.OpenRead(path))
            {
                settings = JsonSerializer.Deserialize<LocalVectorLibrarySettings>(stream, JsonOptions);
            }

            if (settings is null)
            {
                return;
            }

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
}
