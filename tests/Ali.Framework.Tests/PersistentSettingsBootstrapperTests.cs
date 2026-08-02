using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.Storage;
using Ali.Modules.Voice;

namespace Ali.Framework.Tests;

public sealed class PersistentSettingsBootstrapperTests
{
    [Fact]
    public void MissingAliFilesTree_IsRecreatedWithSafeLmStudioSettings()
    {
        using var location = TemporaryDirectory.Create();
        var settingsRoot = Path.Combine(location.Path, "AliFiles", "Settings");
        var profileRoot = Path.Combine(location.Path, "AliFiles", "Data", "Profiles", "test-profile");
        var profile = new AssistantProfile("Ali", "test-profile", DateTimeOffset.UtcNow);

        PersistentUserDataBootstrapper.EnsureCreated(
            settingsRoot,
            profileRoot,
            profile,
            new FileConversationStore(profileRoot),
            new FileMemoryStore(profileRoot),
            new FileReminderStore(profileRoot),
            new FileCorrectionQueueStore(profileRoot));

        Assert.True(File.Exists(AssistantProfileStore.GetProfilePath(settingsRoot)));
        Assert.True(File.Exists(RuntimeSettingsStore.GetSettingsPath(settingsRoot)));
        Assert.True(File.Exists(VoiceRuntimeSettingsStore.GetSettingsPath(settingsRoot)));
        Assert.True(File.Exists(WebSourceBackendSettingsStore.GetSettingsPath(settingsRoot)));
        Assert.True(File.Exists(LocalVectorLibrarySettingsStore.GetSettingsPath(settingsRoot)));

        var runtime = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(settingsRoot);
        Assert.NotNull(runtime);
        Assert.False(runtime.Enabled);
        Assert.Equal(LocalRuntimeEngines.LmStudio, runtime.Engine);
        Assert.Equal("http://127.0.0.1:1234/v1/", runtime.Endpoint.ToString());
        Assert.Empty(runtime.Model);
        Assert.Equal(8192, runtime.ContextTokens);
        Assert.Equal(2048, runtime.OutputTokenLimit);
        Assert.Equal(1, runtime.Temperature);
        Assert.Null(runtime.ReasoningEffort);
    }

    [Fact]
    public void ExistingValidRuntimeSettings_AreUsedWithoutBeingRewritten()
    {
        using var location = TemporaryDirectory.Create();
        Directory.CreateDirectory(location.Path);
        var path = RuntimeSettingsStore.GetSettingsPath(location.Path);
        const string existing = """
            {
              "enabled": false,
              "endpoint": "http://127.0.0.1:13305/api/v1/",
              "model": "user-selected-model",
              "displayName": "User selection",
              "family": "local",
              "size": "custom",
              "quantization": "custom",
              "contextTokens": 4096,
              "outputTokenLimit": 512,
              "temperature": 0.4,
              "topP": null,
              "streamingEnabled": true,
              "supportsVision": false,
              "supportsToolCalls": false,
              "allowPrivateLanEndpoint": false,
              "engine": "Lemonade",
              "reasoningEffort": null
            }
            """;
        File.WriteAllText(path, existing);

        RuntimeSettingsStore.EnsureValidOrReplace(location.Path);

        Assert.Equal(existing, File.ReadAllText(path));
    }

    [Fact]
    public void RuntimeSettingsRoundTrip_PreservesExplicitEndpointAndModelExactly()
    {
        using var location = TemporaryDirectory.Create();
        var endpoint = new Uri("http://127.0.0.1:49001/custom/v1/");
        const string model = "Publisher/GPT-OSS-20B@Q4_K_M";
        var options = RuntimeSettingsStore.GetDefaultOptions() with
        {
            Enabled = true,
            Endpoint = endpoint,
            Model = model,
            Engine = LocalRuntimeEngines.LlamaCpp,
            ContextTokens = 65_536,
            OutputTokenLimit = 8_192,
            SupportsToolCalls = true
        };

        RuntimeSettingsStore.Save(location.Path, options);
        var loaded = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(location.Path);

        Assert.NotNull(loaded);
        Assert.Equal(LocalRuntimeEngines.LlamaCpp, loaded.Engine);
        Assert.Equal(endpoint.ToString(), loaded.Endpoint.ToString());
        Assert.Equal(model, loaded.Model);
        Assert.Equal(65_536, loaded.ContextTokens);
        Assert.Equal(8_192, loaded.OutputTokenLimit);
        Assert.True(loaded.SupportsToolCalls);
    }

    [Fact]
    public void InvalidRuntimeSettings_AreBackedUpAndReplaced()
    {
        using var location = TemporaryDirectory.Create();
        var settingsRoot = Path.Combine(location.Path, "AliFiles", "Settings");
        Directory.CreateDirectory(settingsRoot);
        var path = RuntimeSettingsStore.GetSettingsPath(settingsRoot);
        File.WriteAllText(path, "{ definitely-not-valid-json");

        RuntimeSettingsStore.EnsureValidOrReplace(settingsRoot);

        Assert.NotNull(RuntimeSettingsStore.LoadOpenAiCompatibleOptions(settingsRoot));
        var backups = Directory.GetFiles(
            Path.Combine(location.Path, "AliFiles", "Backups", "InvalidSettings"),
            "runtime-settings.json",
            SearchOption.AllDirectories);
        Assert.Single(backups);
        Assert.Equal("{ definitely-not-valid-json", File.ReadAllText(backups[0]));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali.Framework.Tests",
                Guid.NewGuid().ToString("N"));
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
