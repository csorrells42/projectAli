using System.Diagnostics;
using Ali.Modules.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ali.Modules.Serena;

public enum SerenaRuntimeState
{
    Stopped,
    Starting,
    Ready,
    Restarting,
    Unavailable
}

public sealed record SerenaRuntimeStatus(
    SerenaRuntimeState State,
    string Detail,
    int ToolCount,
    DateTimeOffset ChangedAtUtc);

/// <summary>
/// Owns one Serena MCP child process for Ali's complete application lifetime.
/// A crashed transport is restarted, but in-flight or completed tool calls are
/// never retained, queued, retried, or replayed.
/// </summary>
public sealed class SerenaCodingService : IAsyncDisposable
{
    private readonly SerenaRuntimeSettings _settings;
    private readonly string _workspaceRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _startSync = new();
    private IReadOnlyList<AITool> _tools = Array.Empty<AITool>();
    private string? _serverInstructions;
    private Task? _supervisor;
    private SerenaRuntimeStatus _status = new(
        SerenaRuntimeState.Stopped,
        "Serena has not started.",
        0,
        DateTimeOffset.UtcNow);
    private int _disposed;

    public SerenaCodingService(
        SerenaRuntimeSettings settings,
        string workspaceRoot)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public event Action<SerenaRuntimeStatus>? StatusChanged;

    public IReadOnlyList<AITool> Tools => Volatile.Read(ref _tools);

    public string? ServerInstructions => Volatile.Read(ref _serverInstructions);

    public SerenaRuntimeStatus Status
    {
        get
        {
            lock (_startSync)
            {
                return _status;
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_startSync)
        {
            if (_supervisor is not null)
            {
                return;
            }

            _supervisor = Task.Run(() => SuperviseAsync(_lifetime.Token));
        }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            SetStatus(SerenaRuntimeState.Stopped, "Serena is disabled in configuration.", 0);
            return;
        }

        var firstStart = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            StdioClientTransport? transport = null;
            McpClient? client = null;
            try
            {
                SetStatus(
                    firstStart ? SerenaRuntimeState.Starting : SerenaRuntimeState.Restarting,
                    firstStart
                        ? "Starting Serena for the configured Workspace."
                        : "Restarting Serena after its previous process ended.",
                    0);

                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "Project Ali Serena",
                    Command = _settings.Command,
                    Arguments = BuildArguments(),
                    WorkingDirectory = _workspaceRoot,
                    InheritEnvironmentVariables = true,
                    ShutdownTimeout = TimeSpan.FromSeconds(3)
                });

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds));
                client = await McpClient.CreateAsync(
                        transport,
                        cancellationToken: startup.Token)
                    .WaitAsync(startup.Token)
                    .ConfigureAwait(false);
                var discovered = await client.ListToolsAsync(cancellationToken: startup.Token)
                    .AsTask()
                    .WaitAsync(startup.Token)
                    .ConfigureAwait(false);
                var tools = discovered.Cast<AITool>().ToArray();
                if (tools.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Serena connected but advertised no tools.");
                }

                Volatile.Write(ref _serverInstructions, client.ServerInstructions);
                Volatile.Write(ref _tools, Array.AsReadOnly(tools));
                SetStatus(
                    SerenaRuntimeState.Ready,
                    $"Serena is ready with {tools.Length} native tool(s).",
                    tools.Length);
                firstStart = false;

                var completion = await client.Completion
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _tools, Array.Empty<AITool>());
                    Volatile.Write(ref _serverInstructions, null);
                    var detail = completion.Exception is null
                        ? "The Serena process ended unexpectedly."
                        : $"The Serena process ended ({completion.Exception.GetType().Name}).";
                    SetStatus(SerenaRuntimeState.Restarting, detail, 0);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _tools, Array.Empty<AITool>());
                Volatile.Write(ref _serverInstructions, null);
                SetStatus(
                    SerenaRuntimeState.Unavailable,
                    $"Serena is unavailable ({ex.GetType().Name}: {ex.Message}).",
                    0);
                Trace.TraceError("Serena startup/runtime failure: {0}", ex);
            }
            finally
            {
                if (client is not null)
                {
                    await McpToolSession.DisposeResourceAsync(client).ConfigureAwait(false);
                }
                if (transport is not null)
                {
                    await McpToolSession.DisposeResourceAsync(transport).ConfigureAwait(false);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_settings.RestartDelayMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        Volatile.Write(ref _tools, Array.Empty<AITool>());
        Volatile.Write(ref _serverInstructions, null);
        SetStatus(SerenaRuntimeState.Stopped, "Serena stopped with Ali.", 0);
    }

    private IList<string> BuildArguments() =>
    [
        "start-mcp-server",
        "--project",
        _workspaceRoot,
        "--context",
        _settings.Context,
        "--transport",
        _settings.Transport,
        "--enable-web-dashboard",
        _settings.EnableWebDashboard ? "true" : "false",
        "--open-web-dashboard",
        _settings.OpenWebDashboard ? "true" : "false"
    ];

    private void SetStatus(
        SerenaRuntimeState state,
        string detail,
        int toolCount)
    {
        var status = new SerenaRuntimeStatus(
            state,
            detail,
            toolCount,
            DateTimeOffset.UtcNow);
        lock (_startSync)
        {
            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // Status presentation cannot alter Serena's lifecycle.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        Task? supervisor;
        lock (_startSync)
        {
            supervisor = _supervisor;
        }
        if (supervisor is not null)
        {
            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when Ali closes.
            }
        }
        _lifetime.Dispose();
    }
}
