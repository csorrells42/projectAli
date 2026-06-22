using System.Text.Json;
using Ali.Core.Feedback;

namespace Ali.Infrastructure.Storage;

public sealed class FileCorrectionQueueStore(string dataDirectory) : ICorrectionQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath = Path.Combine(dataDirectory, "corrections.json");

    public async Task SaveAsync(CorrectionReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var reports = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        reports.Add(report);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, reports, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CorrectionReport>> ListAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<CorrectionReport>();
        }

        await using var stream = File.OpenRead(_filePath);
        var reports = await JsonSerializer.DeserializeAsync<List<CorrectionReport>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return reports is null ? Array.Empty<CorrectionReport>() : reports;
    }
}
