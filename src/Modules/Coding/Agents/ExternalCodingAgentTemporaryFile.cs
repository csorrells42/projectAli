namespace Ali.Modules.Coding.Agents;

internal sealed class ExternalCodingAgentTemporaryFile : IDisposable
{
    private ExternalCodingAgentTemporaryFile(string path) => Path = path;

    public string Path { get; }

    public static async Task<ExternalCodingAgentTemporaryFile> CreateAsync(
        string contents,
        string extension,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Ali",
            "CodingAgents",
            "Transport");
        Directory.CreateDirectory(directory);
        var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
        var path = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}{normalizedExtension}");
        await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
        return new ExternalCodingAgentTemporaryFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A stale private transport file is safer than masking the coding-agent result.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale private transport file is safer than masking the coding-agent result.
        }
    }
}
