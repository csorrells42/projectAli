using Ali.Modules.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ali.McpHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var configurationPath = ResolveConfigurationPath(args);
            var configuration = AliMcpHostConfiguration.LoadOrCreate(configurationPath);
            await using var tools = HeadlessMcpToolRuntime.Create(
                configuration.DataRoot,
                AppContext.BaseDirectory,
                configuration.ServerSettings,
                configuration.WorkspaceRoot,
                configuration.Path);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools(tools.Tools);

            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await WriteFailureAsync(ex).ConfigureAwait(false);
            Console.Error.WriteLine($"Ali MCP host failed: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveConfigurationPath(string[] args)
    {
        if (args.Length == 0)
        {
            return System.IO.Path.Combine(AppContext.BaseDirectory, "Ali.McpHost.xml");
        }

        if (args.Length == 2 && args[0].Equals("--config", StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.Path.GetFullPath(args[1]);
        }

        throw new ArgumentException("Usage: Ali.McpHost.exe [--config <path-to-Ali.McpHost.xml>]");
    }

    private static async Task WriteFailureAsync(Exception exception)
    {
        try
        {
            var logRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AliFiles",
                "Data",
                "Logs");
            Directory.CreateDirectory(logRoot);
            var message = $"{DateTimeOffset.Now:O} {exception}\n";
            await File.AppendAllTextAsync(
                System.IO.Path.Combine(logRoot, "Ali.McpHost.log"),
                message).ConfigureAwait(false);
        }
        catch
        {
            // Standard error remains available to the MCP client if file logging is unavailable.
        }
    }
}
