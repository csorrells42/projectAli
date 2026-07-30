using System.Diagnostics;
using System.Net.Http;
using Qdrant.Client;

namespace Ali.Modules.RAG;

public sealed record QdrantRuntimeStatus(
    bool IsReachable,
    bool IsOwnedProcess,
    string State,
    string Message,
    string Endpoint);

public sealed class QdrantServiceManager : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;

    public QdrantServiceManager(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    public QdrantRuntimeStatus Status { get; private set; } = new(false, false, "Stopped", "Qdrant has not been started.", string.Empty);

    public event EventHandler<QdrantRuntimeStatus>? StatusChanged;

    public QdrantClient CreateClient(LocalVectorLibrarySettings settings)
    {
        var apiKey = string.IsNullOrWhiteSpace(settings.QdrantApiKeyEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(settings.QdrantApiKeyEnvironmentVariable.Trim());
        return new QdrantClient(
            settings.QdrantHost.Trim(),
            settings.QdrantGrpcPort,
            settings.QdrantUseTls,
            apiKey);
    }

    public async Task<QdrantRuntimeStatus> EnsureAvailableAsync(
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(settings, cancellationToken).ConfigureAwait(false);
        if (probe.IsReachable || !settings.UseManagedLocalQdrant || !settings.AutoStartQdrant)
        {
            return probe;
        }

        return await StartAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QdrantRuntimeStatus> StartAsync(
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ProbeCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            if (existing.IsReachable)
            {
                Publish(existing with { Message = "Connected to the existing Qdrant service. Ali does not own this process." });
                return Status;
            }

            if (!settings.UseManagedLocalQdrant)
            {
                Publish(existing with { Message = "The configured external Qdrant service is unavailable." });
                return Status;
            }

            if (!IsLoopback(settings.QdrantHost))
            {
                throw new InvalidOperationException("Managed Qdrant may only bind to localhost. Use external mode for a remote server.");
            }

            var executable = ResolveExecutablePath();
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The managed Qdrant runtime is missing. Restore Ali's runtime assets before starting local knowledge.", executable);
            }

            if (_ownedProcess is { HasExited: false })
            {
                return await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            var root = LocalVectorLibrarySettingsStore.GetQdrantDataPath(_dataRoot);
            Directory.CreateDirectory(root);
            var storagePath = Path.Combine(root, "storage");
            var snapshotsPath = Path.Combine(root, "snapshots");
            Directory.CreateDirectory(storagePath);
            Directory.CreateDirectory(snapshotsPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.Environment["QDRANT__SERVICE__HOST"] = "127.0.0.1";
            startInfo.Environment["QDRANT__SERVICE__HTTP_PORT"] = settings.QdrantHttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["QDRANT__SERVICE__GRPC_PORT"] = settings.QdrantGrpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["QDRANT__STORAGE__STORAGE_PATH"] = storagePath;
            startInfo.Environment["QDRANT__STORAGE__SNAPSHOTS_PATH"] = snapshotsPath;
            startInfo.Environment["QDRANT__STORAGE__ON_DISK_PAYLOAD"] = "false";
            startInfo.Environment["QDRANT__STORAGE__OPTIMIZERS__FLUSH_INTERVAL_SEC"] = "1";
            startInfo.Environment["QDRANT__TELEMETRY_DISABLED"] = "true";
            startInfo.Environment["QDRANT__LOG_LEVEL"] = "WARN";

            _ownedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the managed Qdrant process.");
            Publish(new(false, true, "Starting", "Ali is starting the managed Qdrant service.", BuildEndpoint(settings)));
            return await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QdrantRuntimeStatus> ProbeAsync(
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken = default)
    {
        var status = await ProbeCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        Publish(status);
        return status;
    }

    public async Task<QdrantRuntimeStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ownedProcess is not { HasExited: false } process)
            {
                Publish(Status with { IsOwnedProcess = false, State = "External or stopped", Message = "Ali did not stop Qdrant because she does not own a running process." });
                return Status;
            }

            // Qdrant's Windows binary has no private shutdown endpoint. All Ali
            // workers are stopped before this service; allow the shortened flush
            // interval to settle WAL and payload-index work before termination.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.Dispose();
            _ownedProcess = null;
            Publish(new(false, false, "Stopped", "Ali's managed Qdrant process stopped. Stored knowledge was preserved.", Status.Endpoint));
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ResolveExecutablePath() => Path.Combine(AppContext.BaseDirectory, "dependencies", "qdrant", "qdrant.exe");

    public string DataPath => LocalVectorLibrarySettingsStore.GetQdrantDataPath(_dataRoot);

    private async Task<QdrantRuntimeStatus> WaitForReadyAsync(LocalVectorLibrarySettings settings, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ownedProcess?.HasExited == true)
            {
                throw new InvalidOperationException($"Managed Qdrant exited during startup with code {_ownedProcess.ExitCode}.");
            }

            var result = await ProbeCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            if (result.IsReachable)
            {
                Publish(result with { IsOwnedProcess = true, State = "Running", Message = "Managed Qdrant is healthy and ready." });
                return Status;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task<QdrantRuntimeStatus> ProbeCoreAsync(LocalVectorLibrarySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(settings);
            var health = await client.HealthAsync(cancellationToken).ConfigureAwait(false);
            var version = health.Version;
            return new(true, _ownedProcess is { HasExited: false }, "Healthy", $"Qdrant {version} is healthy.", BuildEndpoint(settings));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Grpc.Core.RpcException or InvalidOperationException)
        {
            return new(false, _ownedProcess is { HasExited: false }, "Unavailable", $"Qdrant is unavailable: {ex.Message.ReplaceLineEndings(" ").Trim()}", BuildEndpoint(settings));
        }
    }

    private void Publish(QdrantRuntimeStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string BuildEndpoint(LocalVectorLibrarySettings settings) =>
        $"{(settings.QdrantUseTls ? "https" : "http")}://{settings.QdrantHost}:{settings.QdrantHttpPort}";

    private static bool IsLoopback(string host) => host.Trim() is "127.0.0.1" or "localhost" or "::1";

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
