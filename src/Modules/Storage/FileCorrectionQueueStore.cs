using System.Text.Json;
using Ali.Modules.Feedback;

namespace Ali.Modules.Storage;

public sealed class FileCorrectionQueueStore(string dataDirectory) : ICorrectionQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath = Path.Combine(dataDirectory, "corrections.json");

    public void EnsureCreated()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, Array.Empty<CorrectionReport>(), JsonOptions);
    }

    public async Task SaveAsync(CorrectionReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var reports = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        reports.Add(report);

        await WriteReportsAsync(reports, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(CorrectionReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var reports = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var index = reports.FindIndex(existing => existing.Id.Equals(report.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            reports[index] = report;
        }
        else
        {
            reports.Add(report);
        }

        await WriteReportsAsync(reports, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteReportsAsync(IReadOnlyList<CorrectionReport> reports, CancellationToken cancellationToken)
    {
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
