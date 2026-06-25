using System.Text.Json;

namespace Ali.Infrastructure.Sources;

public sealed record LocalVectorLibrarySettings
{
    public bool Enabled { get; init; } = true;

    public string RootDirectory { get; init; } = DefaultRootDirectory();

    public string EmbeddingEndpoint { get; init; } = "http://127.0.0.1:11434/api/embed";

    public string EmbeddingModel { get; init; } = "nomic-embed-text";

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

    public static string GetIndexPath(string dataRoot) =>
        Path.Combine(dataRoot, "Sources", "local_vector_library_index.json");

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
}
