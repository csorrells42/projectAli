using System.Security.Cryptography;
using System.Text;
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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _statusLock = new();
    private WebApplication? _application;
    private McpServerRuntimeStatus _status;

    internal McpServerHost(string dataRoot, AliMcpServerToolFactory toolFactory)
    {
        _dataRoot = dataRoot;
        _toolFactory = toolFactory;
        var settings = LoadSettings();
        _status = new McpServerRuntimeStatus(
            false,
            settings.Enabled ? "Stopped" : "Disabled",
            settings.Enabled
                ? "The MCP server is enabled but not currently running."
                : "The MCP server is off. Ali's current behavior is unchanged.",
            settings.Endpoint,
            settings.Tools.Count(tool => tool.Enabled));
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

    public McpServerSettings LoadSettings() => McpServerSettingsStore.LoadOrDefault(_dataRoot);

    public McpServerSettings SaveSettings(McpServerSettings settings) =>
        McpServerSettingsStore.Save(_dataRoot, settings);

    public async Task StartIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        var settings = LoadSettings();
        if (!settings.Enabled)
        {
            PublishStatus(new McpServerRuntimeStatus(
                false,
                "Disabled",
                "The MCP server is off. Ali's current behavior is unchanged.",
                settings.Endpoint,
                settings.Tools.Count(tool => tool.Enabled)));
            return;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                return;
            }

            var settings = LoadSettings();
            Validate(settings);
            var token = ResolveAuthenticationToken(settings);
            var tools = _toolFactory.CreateTools(settings);

            PublishStatus(new McpServerRuntimeStatus(
                false,
                "Starting",
                "Starting Ali's local MCP server...",
                settings.Endpoint,
                tools.Count));

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
                    $"Listening locally with {tools.Count} exposed tool(s).",
                    settings.Endpoint,
                    tools.Count));
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
                    tools.Count));
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = _application;
            _application = null;
            var settings = LoadSettings();
            if (application is null)
            {
                PublishStoppedStatus(settings);
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

            PublishStoppedStatus(settings);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
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
            settings.Tools.Count(tool => tool.Enabled)));

    private void PublishStatus(McpServerRuntimeStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }
}
