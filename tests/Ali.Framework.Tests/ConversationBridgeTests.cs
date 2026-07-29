using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Ali.Modules.ConversationBridge;

namespace Ali.Framework.Tests;

public sealed class ConversationBridgeTests
{
    [Fact]
    public void MissingSettings_GenerateStableAuthenticatedOffByDefaultConfiguration()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var first = ConversationBridgeSettingsStore.LoadOrCreate(root);
            var second = ConversationBridgeSettingsStore.LoadOrCreate(root);

            Assert.False(first.Enabled);
            Assert.Equal("127.0.0.1", new Uri(first.Endpoint).Host);
            Assert.Equal(64, first.AuthenticationToken.Length);
            Assert.Equal(first, second);
            Assert.True(File.Exists(ConversationBridgeSettingsStore.GetPath(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Host_RequiresAuthenticationAndSubmitsThroughProvidedLivePipeline()
    {
        var root = CreateTemporaryRoot();
        var port = ReserveLoopbackPort();
        var token = new ConversationBridgeSettings().AuthenticationToken;
        var submitted = new List<string>();
        var snapshot = CreateSnapshot("Ready");
        var host = new ConversationBridgeHost(
            root,
            (text, _) =>
            {
                submitted.Add(text);
                snapshot = CreateSnapshot("Response complete", text);
                return Task.FromResult(snapshot);
            },
            () => snapshot);
        host.SaveSettings(new ConversationBridgeSettings
        {
            Enabled = true,
            Port = port,
            AuthenticationToken = token
        });

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var unauthorized = await client.GetAsync(
                "/v1/session",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var initial = await client.GetFromJsonAsync<ConversationBridgeSnapshot>(
                "/v1/session",
                TestContext.Current.CancellationToken);
            Assert.NotNull(initial);
            Assert.Equal("Ready", initial.Status);

            var response = await client.PostAsJsonAsync(
                "/v1/turns",
                new ConversationBridgeTurnRequest("hello from bridge"),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var completed = await response.Content.ReadFromJsonAsync<ConversationBridgeSnapshot>(
                TestContext.Current.CancellationToken);

            Assert.Equal(["hello from bridge"], submitted);
            Assert.NotNull(completed);
            Assert.Equal("Response complete", completed.Status);
            Assert.Contains(completed.Messages, message => message.Text == "hello from bridge");

            var health = await client.GetStringAsync(
                "/health",
                TestContext.Current.CancellationToken);
            Assert.Contains("\"canApprovePermissions\":false", health, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SettingsTab_ExposesLiveSessionControlsAndUserOnlyApprovalBoundary()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));
        var architecture = File.ReadAllText(FindRepositoryFile("docs", "AgentFrameworkArchitecture.md"));

        Assert.Contains("SettingsConversationBridgeEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsConversationBridgeToken", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsSaveConversationBridge", xaml, StringComparison.Ordinal);
        Assert.Contains("cannot approve permission prompts", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same typed-input method used by the Send button", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no permission-decision endpoint", architecture, StringComparison.OrdinalIgnoreCase);
        var mainViewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains(
            "await SendTextAsync(text, VoiceInputOrigin.Typed, voiceMetadata: null)",
            mainViewModel,
            StringComparison.Ordinal);
        var helper = File.ReadAllText(FindRepositoryFile("tools", "TalkToAli.ps1"));
        Assert.Contains("/v1/session", helper, StringComparison.Ordinal);
        Assert.Contains("/v1/turns", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $settings.authenticationToken", helper, StringComparison.OrdinalIgnoreCase);
    }

    private static ConversationBridgeSnapshot CreateSnapshot(string status, string? text = null) => new(
        "Ali",
        "conversation-test",
        false,
        status,
        "Ready",
        null,
        text is null
            ? []
            : [new ConversationBridgeMessage(
                "message-test",
                "User",
                text,
                "Verified",
                DateTimeOffset.UtcNow,
                false,
                [new ConversationBridgeRenderBlock("paragraph", text)])],
        [],
        DateTimeOffset.UtcNow);

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "AliConversationBridgeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }
}
