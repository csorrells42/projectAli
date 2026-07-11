using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Core.Sources;

namespace Ali.Infrastructure.Sources;

public sealed record SourceCatalogRepairResult(
    int ExistingSourceCount,
    int AddedStarterSourceCount,
    bool CatalogCreated,
    string? BackupPath);

public sealed class FileSourceRetriever
{
    private const string BundledDefaultCatalogResourceName = "Ali.Infrastructure.Sources.curated_sources.seed.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileSourceRetriever(string dataRoot, HttpClient? httpClient = null)
    {
        RootDirectory = Path.Combine(dataRoot, "Sources");
        CatalogPath = Path.Combine(RootDirectory, "curated_sources.json");
        ExamplePath = Path.Combine(RootDirectory, "curated_sources.example.json");
    }

    public string RootDirectory { get; }

    public string CatalogPath { get; }

    public string ExamplePath { get; }

    public void WriteExample()
    {
        RepairStarterCatalog();
        var defaultCatalog = BuildDefaultCatalog();
        if (File.Exists(ExamplePath))
        {
            return;
        }

        using var stream = File.Create(ExamplePath);
        JsonSerializer.Serialize(stream, defaultCatalog, JsonOptions);
    }

    public SourceCatalogRepairResult RepairStarterCatalog()
    {
        Directory.CreateDirectory(RootDirectory);
        var defaultCatalog = BuildDefaultCatalog();
        if (!File.Exists(CatalogPath))
        {
            SaveCatalog(defaultCatalog);
            return new SourceCatalogRepairResult(0, defaultCatalog.Length, CatalogCreated: true, BackupPath: null);
        }

        SourceCatalogEntry[] currentCatalog;
        try
        {
            currentCatalog = LoadCatalog().ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backupPath = Path.Combine(
                RootDirectory,
                $"curated_sources.invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(CatalogPath, backupPath, overwrite: false);
            SaveCatalog(defaultCatalog);
            return new SourceCatalogRepairResult(0, defaultCatalog.Length, CatalogCreated: true, backupPath);
        }

        var existingIds = currentCatalog
            .Select(source => source.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDefaults = defaultCatalog
            .Where(source => !existingIds.Contains(source.Id))
            .ToArray();
        if (missingDefaults.Length > 0)
        {
            SaveCatalog(currentCatalog.Concat(missingDefaults));
        }

        return new SourceCatalogRepairResult(
            currentCatalog.Length,
            missingDefaults.Length,
            CatalogCreated: false,
            BackupPath: null);
    }

    public IReadOnlyList<SourceCatalogEntry> LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return Array.Empty<SourceCatalogEntry>();
        }

        using var stream = File.OpenRead(CatalogPath);
        return JsonSerializer.Deserialize<List<SourceCatalogEntry>>(stream, JsonOptions) ?? [];
    }

    public void SaveCatalog(IEnumerable<SourceCatalogEntry> sources)
    {
        Directory.CreateDirectory(RootDirectory);
        using var stream = File.Create(CatalogPath);
        JsonSerializer.Serialize(stream, sources.ToList(), JsonOptions);
    }

    [Obsolete("The old curated URL fetcher has been replaced by TavilyFirecrawlSourceRetriever. This compatibility method returns no results.")]
    public ISourceRetriever CreateRetriever() => new NoOpSourceRetriever();

    private static SourceCatalogEntry[] BuildDefaultCatalog() =>
        LoadBundledDefaultCatalog() ?? Array.Empty<SourceCatalogEntry>();

    private static SourceCatalogEntry[]? LoadBundledDefaultCatalog()
    {
        try
        {
            var assembly = typeof(FileSourceRetriever).Assembly;
            using var stream = assembly.GetManifestResourceStream(BundledDefaultCatalogResourceName);
            if (stream is null)
            {
                return null;
            }

            var catalog = JsonSerializer.Deserialize<SourceCatalogEntry[]>(stream, JsonOptions);
            var enabledCatalog = catalog?
                .Where(source => source.Enabled)
                .GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            return enabledCatalog is { Length: > 0 } ? enabledCatalog : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
