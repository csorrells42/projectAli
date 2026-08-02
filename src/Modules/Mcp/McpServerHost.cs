using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ali.Modules.Mcp;

public sealed record McpServerRuntimeStatus(
    bool IsRunning,
    string State,
    string Message,
    string Endpoint,
    int ExposedToolCount);

public sealed class McpServerHost : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly AliMcpServerToolFactory _toolFactory;
    private CapabilitySettingsSnapshotOwner? _capabilitySettings;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _statusLock = new();
    private WebApplication? _application;
    private McpServerRuntimeStatus _status;

    internal McpServerHost(
        string dataRoot,
        AliMcpServerToolFactory toolFactory,
        CapabilitySettingsSnapshotOwner? capabilitySettings = null)
    {
        _dataRoot = dataRoot;
        _toolFactory = toolFactory;
        _capabilitySettings = capabilitySettings;
        var loaded = LoadSettingsResult();
        var settings = loaded.Settings;
        _status = new McpServerRuntimeStatus(
            false,
            loaded.Status == McpSettingsLoadStatus.FailedClosed
                ? "Failed closed"
                : settings.Enabled ? "Stopped" : "Disabled",
            loaded.Status == McpSettingsLoadStatus.FailedClosed
                ? loaded.Error ?? "mcp-server.json failed safely."
                : settings.Enabled
                ? "The MCP server is enabled but not currently running."
                : "The MCP server is off. Ali's current behavior is unchanged.",
            settings.Endpoint,
            0);
    }

    public event EventHandler<McpServerRuntimeStatus>? StatusChanged;

    public string SettingsPath => McpServerSettingsStore.GetSettingsPath(_dataRoot);

    public bool IsRunning
    {
        get
        {
            lock (_statusLock)
            {
                return _status.IsRunning;
            }
        }
    }

    public McpServerRuntimeStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public McpServerSettingsLoadResult LoadSettingsResult() => McpServerSettingsStore.Load(_dataRoot);

    public McpServerSettings LoadSettings() => LoadSettingsResult().Settings;

    public McpServerSettings SaveSettings(
        McpServerSettings settings,
        string? expectedBoundaryRevision = null) =>
        McpServerSettingsStore.Save(_dataRoot, settings, expectedBoundaryRevision);

    public async Task StartIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        var loaded = LoadSettingsResult();
        if (loaded.Status == McpSettingsLoadStatus.FailedClosed)
        {
            PublishLoadFailure(loaded);
            return;
        }
        var settings = loaded.Settings;
        if (!settings.Enabled)
        {
            PublishStatus(new McpServerRuntimeStatus(
                false,
                "Disabled",
                "The MCP server is off. Ali's current behavior is unchanged.",
                settings.Endpoint,
                0));
            return;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
            if (_application is not null)
            {
                return true;
            }

            var loaded = LoadSettingsResult();
            if (loaded.Status == McpSettingsLoadStatus.FailedClosed)
            {
                PublishLoadFailure(loaded);
                return false;
            }
            var settings = loaded.Settings;
            Validate(settings);
            var token = ResolveAuthenticationToken(settings);
            var capabilitySettings = _capabilitySettings
                ??= _toolFactory.CreateCapabilitySettingsOwner(_dataRoot, settings);
            capabilitySettings.Reload();
            var publication = _toolFactory.CreateTools(
                settings,
                capabilitySettings,
                cancellationToken,
                () => McpPersistedSecurityBoundaryRevision.Capture(_dataRoot));
            var tools = publication.Tools;

            PublishStatus(new McpServerRuntimeStatus(
                false,
                "Starting",
                "Starting Ali's local MCP server...",
                settings.Endpoint,
                0));

            WebApplication? application = null;
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ApplicationName = typeof(McpServerHost).Assembly.FullName,
                    Args = []
                });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls($"http://127.0.0.1:{settings.Port}");
                builder.Services
                    .AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = settings.Stateless)
                    .WithTools(tools);

                application = builder.Build();
                application.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(settings.Path)
                        && settings.RequireAuthentication
                        && !HasValidBearerToken(context, token!))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await context.Response.WriteAsync("MCP authentication failed.", context.RequestAborted)
                            .ConfigureAwait(false);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
                application.MapGet("/health", () => Results.Json(new
                {
                    service = "Ali MCP Server",
                    state = "running",
                    endpoint = settings.Endpoint,
                    exposedTools = tools.Count
                }));
                application.MapMcp(settings.Path);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _application = application;
                application = null;

                PublishStatus(new McpServerRuntimeStatus(
                    true,
                    "Running",
                    publication.Issues.Count == 0
                        ? $"Listening locally with {tools.Count} exposed tool(s)."
                        : $"Listening locally with {tools.Count} exposed tool(s); capability policy withheld {publication.Issues.Count} configured tool(s).",
                    settings.Endpoint,
                    tools.Count));
                return true;
            }
            catch (Exception ex)
            {
                if (application is not null)
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }

                PublishStatus(new McpServerRuntimeStatus(
                    false,
                    "Failed",
                    ex.Message,
                    settings.Endpoint,
                    0));
                throw;
            }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
            var application = _application;
            _application = null;
            var loaded = LoadSettingsResult();
            var settings = loaded.Settings;
            if (application is null)
            {
                if (loaded.Status == McpSettingsLoadStatus.FailedClosed)
                {
                    PublishLoadFailure(loaded);
                }
                else
                {
                    PublishStoppedStatus(settings);
                }
                return;
            }

            PublishStatus(Status with
            {
                IsRunning = false,
                State = "Stopping",
                Message = "Stopping Ali's local MCP server..."
            });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await application.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }

            if (loaded.Status == McpSettingsLoadStatus.FailedClosed)
            {
                PublishLoadFailure(loaded);
            }
            else
            {
                PublishStoppedStatus(settings);
            }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> RefreshIfRunningAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is null)
            {
                return false;
            }

            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    public static void Validate(McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Turn on the MCP server switch and save the settings before starting it.");
        }

        if (!string.Equals(settings.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ali's MCP server currently permits loopback binding only (127.0.0.1).");
        }

        if (settings.Port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("The MCP server port must be between 1024 and 65535.");
        }

        if (settings.RequireAuthentication
            && string.IsNullOrWhiteSpace(settings.AuthenticationEnvironmentVariable))
        {
            throw new InvalidOperationException("Choose an environment variable that contains the MCP server bearer token.");
        }
    }

    private static string? ResolveAuthenticationToken(McpServerSettings settings)
    {
        if (!settings.RequireAuthentication)
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable(settings.AuthenticationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Authentication is required, but {settings.AuthenticationEnvironmentVariable} is not set. "
                + "Ali never stores the bearer token in the settings file.");
        }

        return token.Trim();
    }

    private static bool HasValidBearerToken(HttpContext context, string expectedToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(authorization[prefix.Length..].Trim());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private void PublishStoppedStatus(McpServerSettings settings) =>
        PublishStatus(new McpServerRuntimeStatus(
            false,
            settings.Enabled ? "Stopped" : "Disabled",
            settings.Enabled
                ? "The MCP server is enabled but not currently running."
                : "The MCP server is off. Ali's current behavior is unchanged.",
            settings.Endpoint,
            0));

    private void PublishLoadFailure(McpServerSettingsLoadResult loaded) =>
        PublishStatus(new McpServerRuntimeStatus(
            false,
            "Failed closed",
            loaded.Error ?? "mcp-server.json failed safely. Ali did not overwrite it.",
            loaded.Settings.Endpoint,
            0));

    private void PublishStatus(McpServerRuntimeStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        var subscribers = StatusChanged?.GetInvocationList();
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                ((EventHandler<McpServerRuntimeStatus>)subscriber)(this, status);
            }
            catch (Exception)
            {
                // Status observers are non-authoritative telemetry. A faulty UI or
                // diagnostic subscriber must not alter listener lifecycle state, and
                // must not prevent healthy subscribers from receiving the update.
            }
        }
    }
}
