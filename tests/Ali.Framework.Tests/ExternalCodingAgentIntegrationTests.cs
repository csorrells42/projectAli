using Ali.Modules.Coding.Agents;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.WorkstationFiles;
using System.Text.Json;

namespace Ali.Framework.Tests;

public sealed class ExternalCodingAgentIntegrationTests
{
    [Theory]
    [InlineData(ProgrammingAgentModes.Off)]
    [InlineData(ProgrammingAgentModes.Aider)]
    [InlineData(ProgrammingAgentModes.OpenHands)]
    [InlineData(ProgrammingAgentModes.Hybrid)]
    [InlineData("retired-provider")]
    public async Task EveryRetiredExternalSelection_DoesNotStartEitherProvider(string mode)
    {
        var calls = new List<string>();
        var openHands = new FakeProvider("OpenHands", calls);
        var aider = new FakeProvider("Aider", calls);
        await WithAgentsAsync(
            mode,
            aider,
            openHands,
            async agents =>
            {
                var result = await agents.ExecuteAsync(
                    "Workspace/sample/sample.csproj",
                    "Add a verified feature.",
                    TestContext.Current.CancellationToken);

                Assert.False(result.Success);
                Assert.Empty(calls);
                Assert.Empty(result.Passes);
                Assert.Contains("Ali is the selected coding executor", result.Summary, StringComparison.Ordinal);
                Assert.Contains("No external coding agent was started", result.Summary, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void RetiredExecuteTool_IsAbsentFromAuthoritativeInventory()
    {
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingAgentExecuteName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingAgentStatusName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.CodingAgentStatusName,
            AgentPermissionProfile.LockedDown));
        var inventory = AliCapabilityCatalog.ListAvailableTools(new AgentOrchestrationSettings
        {
            ProgrammingAgentMode = ProgrammingAgentModes.Aider
        });
        Assert.DoesNotContain(inventory.Tools, tool => tool.Name == AliCapabilityCatalog.CodingAgentExecuteName);
        Assert.DoesNotContain(inventory.Tools, tool => tool.Name == AliCapabilityCatalog.CodingAgentStatusName);
    }

    [Fact]
    public async Task OpenHands_ReceivesAliRuntimeEndpointAndExactTokenBudget()
    {
        var runner = new FakeOpenHandsProcessRunner();
        var runtime = RuntimeSettingsStore.GetDefaultOptions() with
        {
            Endpoint = new Uri("http://127.0.0.1:13305/api/v1/"),
            Model = "gpt-oss-20b-mxfp4-GGUF",
            ContextTokens = 32768,
            OutputTokenLimit = 8192,
            Temperature = 0.25,
            TopP = 0.9
        };
        var provider = new OpenHandsCodingAgentProvider(
            () => new AgentOrchestrationSettings { OpenHandsWslDistribution = "Ubuntu-24.04" },
            () => runtime,
            runner);

        var result = await provider.ExecuteAsync(
            @"C:\work\sample",
            "Implement and verify the feature.",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var execution = Assert.Single(runner.Calls, call => call.Contains("--headless"));
        Assert.Contains("ALI_OPENHANDS_MODEL=openai/gpt-oss-20b-mxfp4-GGUF", execution);
        Assert.Contains("ALI_OPENHANDS_BASE_URL=http://127.0.0.1:13305/api/v1", execution);
        Assert.Contains("ALI_OPENHANDS_CONTEXT_TOKENS=32768", execution);
        Assert.Contains("ALI_OPENHANDS_MAX_OUTPUT_TOKENS=8192", execution);
        Assert.Contains("ALI_OPENHANDS_TEMPERATURE=0.25", execution);
        Assert.Contains("ALI_OPENHANDS_REASONING_EFFORT=low", execution);
        Assert.Contains("ALI_OPENHANDS_TOP_P=0.9", execution);
        Assert.Contains("OPENHANDS_SUPPRESS_BANNER=1", execution);
        Assert.Contains("PYTHONWARNINGS=ignore::DeprecationWarning", execution);
        Assert.Contains("NO_COLOR=1", execution);
        Assert.DoesNotContain("--override-with-envs", execution);
        Assert.DoesNotContain("--task", execution);
        Assert.Contains("--file", execution);
        Assert.Equal("Implement and verify the feature.", runner.ObservedTaskFileContents);
        Assert.Contains(execution, argument => argument.Contains("ali-openhands-tools/openhands/bin/python", StringComparison.Ordinal));
        Assert.Contains(execution, argument => argument.EndsWith("ali_openhands_launcher.py", StringComparison.Ordinal));
        Assert.Contains(
            runner.Calls,
            call => call.Any(argument => argument.Contains("$HOME/.local/bin/openhands", StringComparison.Ordinal))
                && call.Any(argument => argument.Contains("--version", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OpenHands_DoesNotAcceptZeroExitWithoutFinishEvent()
    {
        var runner = new FakeOpenHandsProcessRunner
        {
            ExecutionOutput = "{\"kind\": \"ConversationErrorEvent\", \"detail\": \"model failed\"}"
        };
        var provider = new OpenHandsCodingAgentProvider(
            () => new AgentOrchestrationSettings { OpenHandsWslDistribution = "Ubuntu-24.04" },
            RuntimeSettingsStore.GetDefaultOptions,
            runner);

        var result = await provider.ExecuteAsync(
            @"C:\work\sample",
            "Implement and verify the feature.",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("without a finish event", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenHands_PublishesHumanReadableStructuredProgress()
    {
        var runner = new FakeOpenHandsProcessRunner
        {
            ExecutionOutput = string.Join(
                '\n',
                """{"kind":"ActionEvent","action":{"kind":"CmdRunAction"},"args":{"command":"dotnet build --configuration Release"}}""",
                """{"kind":"ObservationEvent","observation":{"kind":"CmdOutputObservation"},"content":"Build succeeded."}""",
                """{"kind":"FinishObservation"}""")
        };
        var progress = new List<ExternalCodingAgentProgress>();
        var provider = new OpenHandsCodingAgentProvider(
            () => new AgentOrchestrationSettings { OpenHandsWslDistribution = "Ubuntu-24.04" },
            RuntimeSettingsStore.GetDefaultOptions,
            runner,
            progress.Add);

        var result = await provider.ExecuteAsync(
            @"C:\work\sample",
            "Implement and verify the feature.",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains(progress, item => item.Title == "OpenHands accepted the coding job");
        Assert.Contains(progress, item => item.Title.Contains("chose cmd run", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, item => item.Detail.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, item => item.Title.Contains("completed cmd output", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, item => item.Kind == ExternalCodingAgentProgressKind.Completed);
        Assert.DoesNotContain(progress, item => item.Detail.Contains('{') || item.Detail.Contains('}'));
    }

    [Fact]
    public void OpenHandsProgressParser_RedactsSecretsAndRejectsRawNoise()
    {
        Assert.True(OpenHandsProgressParser.TryParseEvent(
            """{"kind":"ActionEvent","action":"CmdRunAction","args":{"command":"tool --api_key=secret-value --project sample"}}""",
            out var progress));
        Assert.Contains("[redacted]", progress.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", progress.Detail, StringComparison.Ordinal);
        Assert.False(OpenHandsProgressParser.TryParseEvent("ordinary console noise", out _));
    }

    [Fact]
    public async Task Aider_ReceivesAliRuntimeBudgetAndConnectorSettingsThroughPrivateFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliAiderProviderTests", Guid.NewGuid().ToString("N"));
        await PrepareFakeAiderInstallAsync(root);
        var runner = new FakeAiderProcessRunner();
        var runtime = RuntimeSettingsStore.GetDefaultOptions() with
        {
            Model = "gpt-oss-20b-mxfp4-GGUF",
            ContextTokens = 32768,
            OutputTokenLimit = 8192,
            Temperature = 0.25,
            TopP = 0.9,
            ReasoningEffort = "medium"
        };
        try
        {
            var provider = new AiderCodingAgentProvider(root, () => runtime, runner);
            var result = await provider.ExecuteAsync(
                root,
                "Implement and verify the feature.",
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal("Implement and verify the feature.", runner.ObservedTaskFileContents);
            Assert.False(File.Exists(runner.ObservedTaskFilePath));
            Assert.False(File.Exists(runner.ObservedModelMetadataPath));
            Assert.False(File.Exists(runner.ObservedModelSettingsPath));
            Assert.DoesNotContain("--message", runner.ExecutionArguments);
            Assert.Contains("--message-file", runner.ExecutionArguments);
            Assert.Contains("--model-metadata-file", runner.ExecutionArguments);
            Assert.Contains("--model-settings-file", runner.ExecutionArguments);
            Assert.EndsWith("ali_aider_launcher.py", runner.ExecutionArguments[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--no-auto-commits", runner.ExecutionArguments);
            Assert.Contains("--no-dirty-commits", runner.ExecutionArguments);
            Assert.Contains("--auto-lint", runner.ExecutionArguments);
            Assert.Contains("--auto-test", runner.ExecutionArguments);
            Assert.Contains("AliAiderProviderTests.csproj", runner.ExecutionArguments);
            Assert.Equal(
                "dotnet build \"AliAiderProviderTests.csproj\" --configuration Release --nologo",
                FakeAiderProcessRunner.ValueAfter(runner.ExecutionArguments, "--test-cmd"));
            Assert.DoesNotContain("--reasoning-effort", runner.ExecutionArguments);

            using var metadata = JsonDocument.Parse(runner.ObservedModelMetadata!);
            var model = metadata.RootElement.GetProperty("openai/gpt-oss-20b-mxfp4-GGUF");
            Assert.Equal(32768, model.GetProperty("max_tokens").GetInt32());
            Assert.Equal(24576, model.GetProperty("max_input_tokens").GetInt32());
            Assert.Equal(8192, model.GetProperty("max_output_tokens").GetInt32());
            var settings = Assert.IsType<string>(runner.ObservedModelSettings);
            Assert.Contains("use_temperature: 0.25", settings, StringComparison.Ordinal);
            Assert.Contains("max_tokens: 8192", settings, StringComparison.Ordinal);
            Assert.Contains("top_p: 0.9", settings, StringComparison.Ordinal);
            Assert.Contains("reasoning_effort: medium", settings, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Aider_DoesNotAcceptZeroExitWhenItsEditCommandFailed()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliAiderProviderFailureTests", Guid.NewGuid().ToString("N"));
        await PrepareFakeAiderInstallAsync(root);
        try
        {
            var runner = new FakeAiderProcessRunner
            {
                ExecutionOutput = "Cmd('git') failed due to: exit code(128)"
            };
            var provider = new AiderCodingAgentProvider(root, RuntimeSettingsStore.GetDefaultOptions, runner);

            var result = await provider.ExecuteAsync(
                root,
                "Implement and verify the feature.",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("execution failure", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task PrepareFakeAiderInstallAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "runtime", "python"));
        Directory.CreateDirectory(Path.Combine(root, "runtime", "aider-packages", "aider"));
        Directory.CreateDirectory(Path.Combine(root, "Modules", "Coding", "Agents", "Tools"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "runtime", "python", "python.exe"),
            string.Empty,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Modules", "Coding", "Agents", "Tools", "ali_aider_launcher.py"),
            string.Empty,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "AliAiderProviderTests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            TestContext.Current.CancellationToken);
    }

    private static async Task WithAgentsAsync(
        string mode,
        IExternalCodingAgentProvider aider,
        IExternalCodingAgentProvider openHands,
        Func<AliExternalCodingAgents, Task> test,
        bool createExistingProject = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliExternalCodingAgentTests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace", "sample");
        if (createExistingProject)
        {
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.Current.CancellationToken);
        }
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))],
                Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(root, activeUsers: null),
                permissions);
            var agents = new AliExternalCodingAgents(
                access,
                () => new AgentOrchestrationSettings { ProgrammingAgentMode = mode },
                RuntimeSettingsStore.GetDefaultOptions,
                root,
                aider: aider,
                openHands: openHands);
            await test(agents);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FakeProvider(
        string name,
        List<string> calls,
        bool succeeds = true) : IExternalCodingAgentProvider
    {
        public string Name => name;

        public Task<ExternalCodingAgentProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalCodingAgentProviderStatus(Name, true, "test", "ready", Name));

        public Task<ExternalCodingAgentPassResult> ExecuteAsync(
            string projectDirectory,
            string objective,
            CancellationToken cancellationToken)
        {
            calls.Add(Name);
            Assert.True(Directory.Exists(projectDirectory));
            Assert.Contains("objective", objective, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new ExternalCodingAgentPassResult(
                Name,
                succeeds,
                succeeds ? 0 : 1,
                1,
                succeeds ? "completed" : "failed",
                "test output"));
        }
    }

    private sealed class FakeOpenHandsProcessRunner : IExternalCodingAgentProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public string? ObservedTaskFileContents { get; private set; }

        public string ExecutionOutput { get; init; } = "{\"kind\": \"FinishObservation\"}";

        public Task<Ali.Modules.Coding.Infrastructure.BoundedProcessResult> RunAsync(
            string executable,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null,
            Action<string, bool>? outputLine = null)
        {
            Calls.Add(arguments.ToArray());
            var output = arguments.Any(argument => argument.Contains("--version", StringComparison.Ordinal))
                ? "OpenHands 1.16.0"
                : arguments.Contains("wslpath")
                    ? ResolveMappedPath(arguments.Last())
                    : arguments.Contains("ip") && arguments.Contains("route")
                        ? "default via 172.22.64.1 dev eth0"
                        : ExecutionOutput;
            if (arguments.Contains("--headless"))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    outputLine?.Invoke(line.Trim(), false);
                }
            }
            return Task.FromResult(new Ali.Modules.Coding.Infrastructure.BoundedProcessResult(
                true,
                0,
                output,
                25,
                false));
        }

        private string ResolveMappedPath(string windowsPath)
        {
            if (windowsPath.EndsWith("ali_openhands_launcher.py", StringComparison.OrdinalIgnoreCase))
            {
                return "/mnt/c/release/Modules/Coding/Agents/Tools/ali_openhands_launcher.py";
            }

            if (windowsPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                ObservedTaskFileContents = File.ReadAllText(windowsPath);
                return "/mnt/c/temp/ali-openhands-task.txt";
            }

            return "/mnt/c/work/sample";
        }
    }

    private sealed class FakeAiderProcessRunner : IExternalCodingAgentProcessRunner
    {
        public IReadOnlyList<string> ExecutionArguments { get; private set; } = [];

        public string? ObservedTaskFileContents { get; private set; }

        public string? ObservedTaskFilePath { get; private set; }

        public string? ObservedModelMetadata { get; private set; }

        public string? ObservedModelMetadataPath { get; private set; }

        public string? ObservedModelSettings { get; private set; }

        public string? ObservedModelSettingsPath { get; private set; }

        public string ExecutionOutput { get; init; } = "Applied edit";

        public Task<Ali.Modules.Coding.Infrastructure.BoundedProcessResult> RunAsync(
            string executable,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null,
            Action<string, bool>? outputLine = null)
        {
            var version = arguments.Contains("--version");
            if (!version)
            {
                ExecutionArguments = arguments.ToArray();
                ObservedTaskFilePath = ValueAfter(arguments, "--message-file");
                ObservedModelMetadataPath = ValueAfter(arguments, "--model-metadata-file");
                ObservedModelSettingsPath = ValueAfter(arguments, "--model-settings-file");
                ObservedTaskFileContents = File.ReadAllText(ObservedTaskFilePath);
                ObservedModelMetadata = File.ReadAllText(ObservedModelMetadataPath);
                ObservedModelSettings = File.ReadAllText(ObservedModelSettingsPath);
                Assert.NotNull(environment);
                Assert.Equal(
                    Path.Combine(workingDirectory, "runtime", "aider-packages"),
                    environment["ALI_AIDER_PACKAGES"]);
                Assert.Equal("1", environment["PYTHONNOUSERSITE"]);
            }

            if (!version)
            {
                foreach (var line in ExecutionOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    outputLine?.Invoke(line.Trim(), false);
                }
            }
            return Task.FromResult(new Ali.Modules.Coding.Infrastructure.BoundedProcessResult(
                true,
                0,
                version ? "__main__.py 0.86.2" : ExecutionOutput,
                25,
                false));
        }

        public static string ValueAfter(IReadOnlyList<string> arguments, string option)
        {
            var index = -1;
            for (var candidate = 0; candidate < arguments.Count; candidate++)
            {
                if (string.Equals(arguments[candidate], option, StringComparison.Ordinal))
                {
                    index = candidate;
                    break;
                }
            }

            Assert.InRange(index, 0, arguments.Count - 2);
            return arguments[index + 1];
        }
    }
}
