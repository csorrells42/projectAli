using System.Security.Cryptography;
using System.Text.Json;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynActionHandle(
    string Id,
    string ActionId,
    string TargetPath,
    string DocumentPath,
    int Line,
    int Column,
    string RequestedValue,
    IReadOnlyDictionary<string, string> SourceHashes,
    DateTimeOffset CreatedAt);

internal sealed class AliRoslynActionHandleStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public AliRoslynActionHandleStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task SaveAsync(AliRoslynActionHandle handle, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, handle.Id + ".json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(handle, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<AliRoslynActionHandle> LoadAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The Roslyn action handle is invalid.", nameof(id));
        }

        var json = await File.ReadAllTextAsync(Path.Combine(_root, id + ".json"), cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<AliRoslynActionHandle>(json, JsonOptions)
            ?? throw new InvalidDataException("The Roslyn action handle could not be read.");
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
