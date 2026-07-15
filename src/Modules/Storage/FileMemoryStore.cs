using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Memory;

namespace Ali.Modules.Storage;

public sealed class FileMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public FileMemoryStore(string localAliRoot)
    {
        RootDirectory = Path.Combine(localAliRoot, "Memory");
        _filePath = Path.Combine(RootDirectory, "memories.json");
    }

    public string RootDirectory { get; }

    public string FilePath => _filePath;

    public void EnsureCreated()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        WriteAll(Array.Empty<MemoryEntry>());
    }

    public MemoryListResult List()
    {
        if (!File.Exists(_filePath))
        {
            return new MemoryListResult(Array.Empty<MemoryEntry>(), Array.Empty<string>());
        }

        try
        {
            var memories = ReadAll()
                .Where(memory => memory.Active)
                .OrderByDescending(memory => memory.UpdatedAt)
                .ToList();
            return new MemoryListResult(memories, Array.Empty<string>());
        }
        catch (Exception ex) when (IsJsonOrIoException(ex))
        {
            return new MemoryListResult(Array.Empty<MemoryEntry>(), [$"Memory file was unreadable: {ex.Message}"]);
        }
    }

    public MemoryEntry Save(MemoryEntry memory)
    {
        Directory.CreateDirectory(RootDirectory);
        var normalized = memory with
        {
            Text = memory.Text.Trim(),
            Category = string.IsNullOrWhiteSpace(memory.Category) ? "general" : memory.Category.Trim()
        };
        var memories = File.Exists(_filePath) ? ReadAll().ToList() : [];
        var index = memories.FindIndex(existing => existing.MemoryId.Equals(normalized.MemoryId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            memories[index] = normalized;
        }
        else
        {
            memories.Add(normalized);
        }

        WriteAll(memories);
        return normalized;
    }

    public bool Delete(string memoryId)
    {
        var memories = File.Exists(_filePath) ? ReadAll().ToList() : [];
        var removed = memories.RemoveAll(memory => memory.MemoryId.Equals(memoryId, StringComparison.OrdinalIgnoreCase));
        WriteAll(memories);
        return removed > 0;
    }

    public int DeleteMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var memories = File.Exists(_filePath) ? ReadAll().ToList() : [];
        var removed = memories.RemoveAll(memory => memory.Text.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
        WriteAll(memories);
        return removed;
    }

    public int Clear()
    {
        var count = File.Exists(_filePath) ? ReadAll().Count : 0;
        WriteAll(Array.Empty<MemoryEntry>());
        return count;
    }

    private IReadOnlyList<MemoryEntry> ReadAll()
    {
        using var stream = File.OpenRead(_filePath);
        return JsonSerializer.Deserialize<List<MemoryEntry>>(stream, JsonOptions) ?? [];
    }

    private void WriteAll(IReadOnlyList<MemoryEntry> memories)
    {
        Directory.CreateDirectory(RootDirectory);
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, memories, JsonOptions);
    }

    private static bool IsJsonOrIoException(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException;
}
