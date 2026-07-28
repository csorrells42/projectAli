using System.Diagnostics;
using System.Text.Json;
using Ali.Modules.RAG;

namespace Ali.Modules.UserMemory;

internal sealed class Mem0ProcessClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataRoot;
    private readonly QdrantServiceManager _qdrant;
    private readonly Func<LocalVectorLibrarySettings> _qdrantSettings;
    private readonly Func<UserMemorySettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;

    public Mem0ProcessClient(
        string dataRoot,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> qdrantSettings,
        Func<UserMemorySettings> settings)
    {
        _dataRoot = Path.Combine(dataRoot, "Memory", "Mem0");
        _qdrant = qdrant;
        _qdrantSettings = qdrantSettings;
        _settings = settings;
    }

    public string LastDiagnostic
    {
        get { lock (_stderr) return string.Join(" | ", _stderr); }
    }

    public async Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var requestProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = JsonSerializer.SerializeToElement(id, JsonOptions)
            };
            foreach (var property in JsonSerializer.SerializeToElement(request, JsonOptions).EnumerateObject())
            {
                requestProperties[property.Name] = property.Value.Clone();
            }
            var requestJson = JsonSerializer.Serialize(requestProperties, JsonOptions);
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException(
                    process.HasExited
                        ? $"Mem0 worker exited with code {process.ExitCode}. {LastDiagnostic}"
                        : $"Mem0 worker returned no response. {LastDiagnostic}");
            }
            var response = JsonSerializer.Deserialize<Mem0Response>(line, JsonOptions)
                ?? throw new InvalidOperationException("Mem0 worker returned invalid JSON.");
            if (!string.Equals(response.Id, id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Mem0 worker response did not match the current request.");
            }
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } running)
        {
            return running;
        }

        var qdrantSettings = _qdrantSettings();
        await _qdrant.EnsureAvailableAsync(qdrantSettings, cancellationToken).ConfigureAwait(false);
        if (!_qdrant.Status.IsReachable)
        {
            throw new InvalidOperationException(_qdrant.Status.Message);
        }

        var settings = _settings().Normalize();
        var python = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "lib", "memory", "mem0_service.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            throw new FileNotFoundException("The portable Mem0 runtime is not installed. Restore Ali runtime assets and republish.");
        }
        Directory.CreateDirectory(_dataRoot);
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _dataRoot
        };
        start.ArgumentList.Add(script);
        Add("--data-root", _dataRoot);
        Add("--collection", settings.CollectionName);
        Add("--llm-endpoint", settings.LemonadeEndpoint);
        Add("--llm-model", settings.LlmModel);
        Add("--embedding-endpoint", settings.EmbeddingEndpoint);
        Add("--embedding-model", settings.EmbeddingModel);
        Add("--embedding-dimensions", settings.EmbeddingDimensions.ToString());
        Add("--qdrant-host", qdrantSettings.QdrantHost);
        Add("--qdrant-port", qdrantSettings.QdrantHttpPort.ToString());
        start.Environment["MEM0_TELEMETRY"] = "false";
        start.Environment["POSTHOG_DISABLED"] = "true";
        start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";

        var process = Process.Start(start) ?? throw new InvalidOperationException("Mem0 worker did not start.");
        process.ErrorDataReceived += OnErrorDataReceived;
        process.BeginErrorReadLine();
        _process = process;
        return process;

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_stderr)
        {
            _stderr.Enqueue(e.Data.ReplaceLineEndings(" ").Trim());
            while (_stderr.Count > 8) _stderr.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not { } process) return;
            try { process.StandardInput.Close(); } catch { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            process.Dispose();
            _process = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

}

internal sealed record Mem0Response(
    string Id,
    bool Success,
    string Message,
    IReadOnlyList<UserMemory>? Memories,
    int Count,
    string? ErrorCode);
