using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Coding.Changesets;

internal sealed record AliSourceFileChange(
    string FilePath,
    string ExpectedSha256,
    string NewSha256,
    string NewContent,
    string EncodingName);

internal sealed record AliSourceChangeSet(
    string Id,
    string RootPath,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AliSourceFileChange> Files);

internal sealed class AliSourceChangeSetStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public AliSourceChangeSetStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task<AliSourceChangeSet> CreateAsync(
        string sourceRoot,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            throw new ArgumentException("A source changeset requires at least one replacement.", nameof(replacements));
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var changes = new List<AliSourceFileChange>(replacements.Count);
        foreach (var (candidatePath, newContent) in replacements.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = ValidateContainedFile(canonicalRoot, candidatePath);
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var encoding = DetectEncoding(bytes);
            var newBytes = encoding.GetBytes(newContent ?? string.Empty);
            changes.Add(new AliSourceFileChange(
                filePath,
                Hash(bytes),
                Hash(newBytes),
                newContent ?? string.Empty,
                encoding.WebName));
        }

        var changeSet = new AliSourceChangeSet(
            Guid.NewGuid().ToString("N"),
            canonicalRoot,
            DateTimeOffset.UtcNow,
            changes);
        await SaveAsync(changeSet, cancellationToken).ConfigureAwait(false);
        return changeSet;
    }

    public async Task SaveAsync(AliSourceChangeSet changeSet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var directory = Path.Combine(_root, changeSet.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "changeset.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(changeSet, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<AliSourceChangeSet> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The changeset ID is invalid.", nameof(id));
        }

        var path = Path.Combine(_root, id, "changeset.json");
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AliSourceChangeSet>(json, JsonOptions)
            ?? throw new InvalidDataException("The source changeset could not be read.");
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal static string ValidateContainedFile(string canonicalRoot, string candidatePath)
    {
        var fullPath = Path.GetFullPath(candidatePath);
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new InvalidOperationException("A source changeset may contain only existing files beneath its approved project root.");
        }

        return fullPath;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return new UTF8Encoding(true);
        }

        return new UTF8Encoding(false);
    }
}
