using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Mcp;
using Ali.Modules.Storage;
using Ali.Modules.Coding;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class McpServerTests
{
    [Fact]
    public void Defaults_KeepServerAndEveryToolOff()
    {
        var settings = new McpServerSettings().Normalize();

        Assert.False(settings.Enabled);
        Assert.True(settings.RequireAuthentication);
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.NotEmpty(settings.Tools);
        Assert.All(settings.Tools, tool => Assert.False(tool.Enabled));
    }

    [Fact]
    public void Store_RoundTripsServerAndPerToolExposure()
    {
        var root = CreateTemporaryRoot();
        try
        {
            McpServerSettingsStore.Save(root, new McpServerSettings
            {
                Enabled = true,
                Port = 9444,
                Path = "agent/mcp/",
                RequireAuthentication = false,
                Tools =
                [
                    new McpServerToolPolicy
                    {
                        Name = AliCapabilityCatalog.GetCurrentLocalTimeName,
                        Enabled = true
                    }
                ]
            });

            var restored = McpServerSettingsStore.LoadOrDefault(root);

            Assert.True(restored.Enabled);
            Assert.Equal(9444, restored.Port);
            Assert.Equal("/agent/mcp", restored.Path);
            Assert.False(restored.RequireAuthentication);
            Assert.True(Assert.Single(
                restored.Tools,
                tool => tool.Name == AliCapabilityCatalog.GetCurrentLocalTimeName).Enabled);
            Assert.False(Assert.Single(
                restored.Tools,
                tool => tool.Name == AliCapabilityCatalog.SearchMemoryName).Enabled);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Host_StartsAsynchronouslyAndAdvertisesOnlyEnabledTools()
    {
        var root = CreateTemporaryRoot();
        var port = ReserveAvailablePort();
        var localSources = new EmptySourceRetriever();
        var toolFactory = new AliMcpServerToolFactory(
            localSources,
            localSources,
            new McpWebResearchClient(static () => new WebSourceBackendSettings { UseMcpResearch = false }),
            new FileMemoryStore(root),
            new FileReminderStore(root),
            AssistantProfile.Create("Ali"));
        await using var host = new McpServerHost(root, toolFactory);
        host.SaveSettings(new McpServerSettings
        {
            Enabled = true,
            Port = port,
            RequireAuthentication = false,
            Tools =
            [
                new McpServerToolPolicy
                {
                    Name = AliCapabilityCatalog.GetCurrentLocalTimeName,
                    Enabled = true
                }
            ]
        });

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            var probe = await new McpClientManager(root).ProbeAsync(new McpServerProfile
            {
                Id = "integration-test",
                Name = "Ali test server",
                Enabled = true,
                Transport = McpTransportKinds.Http,
                Endpoint = host.LoadSettings().Endpoint,
                ConnectionTimeoutSeconds = 10
            }, TestContext.Current.CancellationToken);

            Assert.True(probe.Succeeded, probe.Status);
            var tool = Assert.Single(probe.Tools);
            Assert.Equal(AliCapabilityCatalog.GetCurrentLocalTimeName, tool.Name);
            Assert.True(host.IsRunning);

            var manager = new McpClientManager(root);
            manager.SaveSettings(new McpClientSettings
            {
                Enabled = true,
                Servers =
                [
                    new McpServerProfile
                    {
                        Id = "ali-local",
                        Name = "Ali Local",
                        Enabled = true,
                        Transport = McpTransportKinds.Http,
                        Endpoint = host.LoadSettings().Endpoint,
                        Tools =
                        [
                            new McpToolPolicy
                            {
                                Name = AliCapabilityCatalog.GetCurrentLocalTimeName,
                                Enabled = true,
                                RequiresApproval = true
                            }
                        ]
                    }
                ]
            });
            await using var session = await manager.CreateEnabledToolSessionAsync(
                TestContext.Current.CancellationToken);
            var resolved = Assert.Single(session.Tools);
            Assert.True(resolved.RequiresApproval);
            Assert.Equal("mcp_ali_local_get_current_local_time", resolved.Function.Name);
            var invocation = await resolved.Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);
            Assert.NotNull(invocation);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task FullCatalog_AllCoordinatorToolsAreDiscoverableAndCallableWithoutCamera()
    {
        var root = CreateTemporaryRoot();
        var port = ReserveAvailablePort();
        var webSources = new RecordingSourceRetriever("web");
        var localSources = new RecordingSourceRetriever("local");
        var memories = new FileMemoryStore(root);
        var reminders = new FileReminderStore(root);
        var codingRoot = Path.Combine(root, "workspace", "McpCode");
        Directory.CreateDirectory(codingRoot);
        await File.WriteAllTextAsync(
            Path.Combine(codingRoot, "McpCode.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(codingRoot, "Example.cs"),
            "namespace McpCode; public sealed class Example;",
            TestContext.Current.CancellationToken);
        var toolFactory = new AliMcpServerToolFactory(
            localSources,
            webSources,
            new McpWebResearchClient(static () => new WebSourceBackendSettings { UseMcpResearch = false }),
            memories,
            reminders,
            AssistantProfile.Create("Ali"),
            CreateCodingModule(root));
        await using var host = new McpServerHost(root, toolFactory);
        host.SaveSettings(new McpServerSettings
        {
            Enabled = true,
            Port = port,
            RequireAuthentication = false,
            Tools = McpServerToolCatalog.CreateDefaultPolicies()
                .Select(policy => new McpServerToolPolicy
                {
                    Name = policy.Name,
                    Description = policy.Description,
                    Enabled = true,
                    WritesLocalData = policy.WritesLocalData,
                    UsesNetwork = policy.UsesNetwork,
                    ReadsPrivateData = policy.ReadsPrivateData
                })
                .ToArray()
        });

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "Ali full catalog test",
                Endpoint = new Uri(host.LoadSettings().Endpoint),
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(10)
            });
            await using var client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.Current.CancellationToken);
            var discovered = await client.ListToolsAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var expected = McpServerToolCatalog.CreateDefaultPolicies()
                .Select(policy => policy.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var actual = discovered
                .Select(tool => tool.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
            Assert.DoesNotContain(actual, name => name.Contains("camera", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(actual, name => name.Contains("vision", StringComparison.OrdinalIgnoreCase));

            await CallSuccessfullyAsync(client, AliCapabilityCatalog.ListAvailableToolsName, []);
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.RememberFactName, new()
            {
                ["fact"] = "The integration-test name is Morgan.",
                ["category"] = "person"
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.SearchMemoryName, new()
            {
                ["query"] = "Morgan"
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.SearchCurrentWebName, new()
            {
                ["query"] = "current integration test news",
                ["topic"] = "news"
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.ResearchWebName, new()
            {
                ["question"] = "Compare two test sources."
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.SearchLocalLibraryName, new()
            {
                ["query"] = "integration manual"
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.CreateReminderName, new()
            {
                ["title"] = "MCP integration test reminder",
                ["dueAtLocal"] = DateTimeOffset.Now.AddHours(1).ToString("O")
            });
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.GetAssistantIdentityName, []);
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.GetCurrentLocalTimeName, []);
            await CallSuccessfullyAsync(client, AliCapabilityCatalog.RoslynInspectSolutionName, new()
            {
                ["targetPath"] = "Workspace/McpCode/McpCode.csproj"
            });

            Assert.Single(memories.List().Memories);
            Assert.Single(reminders.List().Reminders);
            Assert.Equal(1, webSources.CallCount);
            Assert.Equal(1, localSources.CallCount);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task BearerAuthentication_RejectsMissingTokenAndAcceptsEnvironmentSecret()
    {
        var root = CreateTemporaryRoot();
        var port = ReserveAvailablePort();
        var environmentVariable = "ALI_MCP_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(environmentVariable, "correct-test-secret");
        var sources = new EmptySourceRetriever();
        var toolFactory = new AliMcpServerToolFactory(
            sources,
            sources,
            new McpWebResearchClient(static () => new WebSourceBackendSettings { UseMcpResearch = false }),
            new FileMemoryStore(root),
            new FileReminderStore(root),
            AssistantProfile.Create("Ali"));
        await using var host = new McpServerHost(root, toolFactory);
        host.SaveSettings(new McpServerSettings
        {
            Enabled = true,
            Port = port,
            RequireAuthentication = true,
            AuthenticationEnvironmentVariable = environmentVariable,
            Tools =
            [
                new McpServerToolPolicy
                {
                    Name = AliCapabilityCatalog.GetCurrentLocalTimeName,
                    Enabled = true
                }
            ]
        });

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            var manager = new McpClientManager(root);
            var withoutToken = await manager.ProbeAsync(new McpServerProfile
            {
                Id = "missing-token",
                Name = "Missing token",
                Enabled = true,
                Transport = McpTransportKinds.Http,
                Endpoint = host.LoadSettings().Endpoint,
                ConnectionTimeoutSeconds = 10
            }, TestContext.Current.CancellationToken);
            var withToken = await manager.ProbeAsync(new McpServerProfile
            {
                Id = "correct-token",
                Name = "Correct token",
                Enabled = true,
                Transport = McpTransportKinds.Http,
                Endpoint = host.LoadSettings().Endpoint,
                AuthenticationHeaderName = "Authorization",
                AuthenticationPrefix = "Bearer ",
                AuthenticationEnvironmentVariable = environmentVariable,
                ConnectionTimeoutSeconds = 10
            }, TestContext.Current.CancellationToken);

            Assert.False(withoutToken.Succeeded);
            Assert.True(withToken.Succeeded, withToken.Status);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(environmentVariable, null);
            DeleteTemporaryRoot(root);
        }
    }

    private static async Task CallSuccessfullyAsync(
        McpClient client,
        string toolName,
        Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
    }

    private static int ReserveAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static AliCodingModule CreateCodingModule(string root)
    {
        var permissions = new AgentToolPermissionStore(root);
        var store = new AliWorkstationFileStore(
        [
            new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
        ], Path.Combine(root, "trash"));
        var audit = new AgentFileActionAuditStore(root, activeUsers: null);
        return new AliCodingModule(new AliWorkstationFileAccess(store, audit, permissions));
    }

    private static string CreateTemporaryRoot()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "AliMcpServerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EmptySourceRetriever : ISourceRetriever
    {
        public Task<SourceRetrievalResult> RetrieveAsync(
            string userText,
            CancellationToken cancellationToken) => Task.FromResult(SourceRetrievalResult.Empty);
    }

    private sealed class RecordingSourceRetriever(string kind) : ISourceRetriever
    {
        public int CallCount { get; private set; }

        public Task<SourceRetrievalResult> RetrieveAsync(
            string userText,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new SourceRetrievalResult(
                [new SourceExcerpt(
                    1,
                    kind,
                    $"Controlled {kind} source",
                    $"https://example.invalid/{kind}",
                    DateTimeOffset.UtcNow,
                    $"Controlled {kind} evidence for {userText}.")],
                []));
        }
    }
}
