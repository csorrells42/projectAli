using Ali.Modules.Mcp;
using Ali.Modules.Capabilities;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Ali.Framework.Tests;

public sealed class McpClientSettingsTests
{
    [Fact]
    public void Defaults_AreEntirelyDisabled()
    {
        var settings = new McpClientSettings();
        var server = new McpServerProfile();
        var tool = new McpToolPolicy();

        Assert.False(settings.Enabled);
        Assert.False(server.Enabled);
        Assert.False(server.InheritEnvironmentVariables);
        Assert.False(tool.Enabled);
        Assert.True(tool.RequiresApproval);
    }

    [Fact]
    public void Store_RoundTripsConnectionAndToolPolicies()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new McpClientSettings
            {
                Enabled = true,
                Servers =
                [
                    new McpServerProfile
                    {
                        Name = "Local Files",
                        Enabled = true,
                        Transport = McpTransportKinds.Stdio,
                        Command = "mcp-files.exe",
                        Arguments = ["--root", "C:\\Approved"],
                        EnvironmentVariables =
                        [
                            new McpEnvironmentVariableBinding
                            {
                                Name = "TOKEN",
                                SourceEnvironmentVariable = "ALI_MCP_TOKEN"
                            }
                        ],
                        Tools =
                        [
                            new McpToolPolicy
                            {
                                Name = "read_file",
                                Description = "Read an approved file.",
                                Enabled = true,
                                RequiresApproval = false,
                                ReadOnlyHint = true
                            }
                        ]
                    }
                ]
            };

            McpClientSettingsStore.Save(root, settings);
            var restored = McpClientSettingsStore.LoadOrDefault(root);
            var manager = new McpClientManager(root);

            Assert.True(restored.Enabled);
            var server = Assert.Single(restored.Servers);
            Assert.False(string.IsNullOrWhiteSpace(server.Id));
            Assert.Equal(McpTransportKinds.Stdio, server.Transport);
            Assert.Equal(["--root", "C:\\Approved"], server.Arguments);
            Assert.Equal("ALI_MCP_TOKEN", Assert.Single(server.EnvironmentVariables).SourceEnvironmentVariable);
            var tool = Assert.Single(server.Tools);
            Assert.True(tool.Enabled);
            Assert.False(tool.RequiresApproval);
            Assert.True(tool.ReadOnlyHint);
            Assert.Equal(1, manager.CountEnabledConfiguredToolPolicies());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MissingServerId_RemainsDeterministicallyWithheldUntilExplicitSavePersistsOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = McpClientSettingsStore.GetSettingsPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                """
                {
                  "enabled": false,
                  "servers": [
                    {
                      "name": "Legacy without ID",
                      "enabled": false,
                      "connectionTimeoutSeconds": 30,
                      "tools": []
                    }
                  ]
                }
                """);

            var first = McpClientSettingsStore.LoadOrDefault(root);
            var second = McpClientSettingsStore.LoadOrDefault(root);
            Assert.Equal(string.Empty, Assert.Single(first.Servers).Id);
            Assert.Equal(string.Empty, Assert.Single(second.Servers).Id);

            McpClientSettingsStore.Save(root, first);
            var persisted = Assert.Single(McpClientSettingsStore.LoadOrDefault(root).Servers).Id;
            var reloaded = Assert.Single(McpClientSettingsStore.LoadOrDefault(root).Servers).Id;
            Assert.False(string.IsNullOrWhiteSpace(persisted));
            Assert.Equal(persisted, reloaded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PerProfileTimeout_IsPreservedAndCancelsTheCombinedConnectionWindow()
    {
        var profile = new McpServerProfile
        {
            Name = "Slow server",
            Endpoint = "http://127.0.0.1:65530/mcp",
            ConnectionTimeoutSeconds = 1
        };
        var settings = McpClientSettingsStore.Normalize(new McpClientSettings
        {
            Servers = [profile]
        });

        Assert.Equal(1, Assert.Single(settings.Servers).ConnectionTimeoutSeconds);
        Assert.Null(McpClientManager.Validate(profile));
        using var timeout = McpClientManager.CreateProfileTimeoutTokenSource(
            TestContext.Current.CancellationToken,
            profile.ConnectionTimeoutSeconds);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(5), timeout.Token));
    }

    [Fact]
    public async Task ExhaustedTurnSetupBudget_SkipsRemainingServersWithoutDelayingTheTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new McpClientManager(root);
            manager.SaveSettings(new McpClientSettings
            {
                Enabled = true,
                Servers = Enumerable.Range(1, 2)
                    .Select(index => new McpServerProfile
                    {
                        Id = $"server-{index}",
                        Name = $"Slow server {index}",
                        Enabled = true,
                        Endpoint = $"http://127.0.0.1:{65000 + index}/mcp",
                        ConnectionTimeoutSeconds = 300,
                        Tools =
                        [
                            new McpToolPolicy
                            {
                                Name = "read",
                                Enabled = true,
                                ReadOnlyHint = true,
                                RequiresApproval = false
                            }
                        ]
                    })
                    .ToList()
            });
            var started = DateTimeOffset.UtcNow;

            await using var session = await manager.CreateEnabledToolSessionAsync(
                TimeSpan.Zero,
                TestContext.Current.CancellationToken);

            Assert.Empty(session.Tools);
            Assert.Equal(2, session.Warnings.Count);
            Assert.All(session.Warnings, warning =>
                Assert.Contains("setup budget", warning.Message, StringComparison.OrdinalIgnoreCase));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OuterCancellation_StillCancelsSetupImmediately()
    {
        var manager = new McpClientManager(Path.Combine(
            Path.GetTempPath(),
            "AliMcpTests",
            Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CreateEnabledToolSessionAsync(
                McpClientManager.DefaultTurnSetupBudget,
                cancellation.Token));
    }

    [Fact]
    public async Task MissingStdioExecutable_WithholdsServerWithoutFailingTheNativeTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new McpClientManager(root);
            manager.SaveSettings(new McpClientSettings
            {
                Enabled = true,
                Servers =
                [
                    new McpServerProfile
                    {
                        Id = "missing-stdio",
                        Name = "Missing stdio server",
                        Enabled = true,
                        Transport = McpTransportKinds.Stdio,
                        Command = "ali-mcp-command-that-does-not-exist-42.exe",
                        ConnectionTimeoutSeconds = 1,
                        Tools =
                        [
                            new McpToolPolicy
                            {
                                Name = "read",
                                Enabled = true,
                                RequiresApproval = true,
                                SchemaFingerprint = new string('A', 64)
                            }
                        ]
                    }
                ]
            });

            await using var session = await manager.CreateEnabledToolSessionAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(session.Tools);
            Assert.Contains(session.Warnings, warning =>
                warning.ServerName == "Missing stdio server"
                && warning.Message.Contains("failed safely", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ModelToolName_IsNamespacedAndConnectorSafe()
    {
        var name = McpClientManager.BuildModelToolName(
            new McpServerProfile { Name = "My Files & Notes" },
            "Read Document!");

        Assert.StartsWith("mcp_read_document_", name);
        Assert.True(name.Length <= 64);
        Assert.Matches("^[a-z0-9_]+$", name);
    }

    [Fact]
    public void ModelToolName_UsesStableServerAndSchemaIdentityAcrossDisplayRename()
    {
        var original = "read_document";
        var first = McpClientManager.BuildModelToolName(
            new McpServerProfile { Id = "stable-server", Name = "Original label" },
            original,
            "schema-a");
        var renamed = McpClientManager.BuildModelToolName(
            new McpServerProfile { Id = "stable-server", Name = "Renamed label" },
            original,
            "schema-a");
        var differentServer = McpClientManager.BuildModelToolName(
            new McpServerProfile { Id = "other-server", Name = "Original label" },
            original,
            "schema-a");
        var changedSchema = McpClientManager.BuildModelToolName(
            new McpServerProfile { Id = "stable-server", Name = "Original label" },
            original,
            "schema-b");

        Assert.Equal(first, renamed);
        Assert.NotEqual(first, differentServer);
        Assert.NotEqual(first, changedSchema);
    }

    [Fact]
    public void LossyModelNameCollision_WithholdsEveryAmbiguousExternalTool()
    {
        var profile = new McpServerProfile { Name = "Long names" };
        var firstOriginal = new string('a', 80) + "_first";
        var secondOriginal = new string('a', 80) + "_second";
        var firstModelName = McpClientManager.BuildModelToolName(profile, firstOriginal);
        var secondModelName = McpClientManager.BuildModelToolName(profile, secondOriginal);
        Assert.Equal(firstModelName, secondModelName);

        var collision = McpClientManager.RejectModelNameCollisions(
        [
            ResolvedTool(
                AIFunctionFactory.Create(() => "first", firstModelName),
                "server-one",
                "Server one",
                firstOriginal),
            ResolvedTool(
                AIFunctionFactory.Create(() => "second", secondModelName),
                "server-two",
                "Server two",
                secondOriginal)
        ]);

        Assert.Empty(collision.Tools);
        Assert.Equal(2, collision.Warnings.Count);
        Assert.All(collision.Warnings, warning =>
            Assert.Contains("collides", warning.Message, StringComparison.OrdinalIgnoreCase));
        Assert.All(collision.Warnings, warning =>
            Assert.DoesNotContain(firstModelName, warning.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentBindingDraft_RejectsMalformedAndDuplicateDestinations()
    {
        var malformed = new McpServerProfileViewModel(new McpServerProfile())
        {
            EnvironmentVariableBindingsText = "TOKEN=ALI_TOKEN\nbroken"
        };
        var malformedError = Assert.Throws<InvalidOperationException>(malformed.ToModel);
        Assert.Contains("line 2", malformedError.Message, StringComparison.OrdinalIgnoreCase);

        var duplicate = new McpServerProfileViewModel(new McpServerProfile())
        {
            EnvironmentVariableBindingsText = "TOKEN=ALI_TOKEN\ntoken=OTHER_TOKEN"
        };
        var duplicateError = Assert.Throws<InvalidOperationException>(duplicate.ToModel);
        Assert.Contains("more than once", duplicateError.Message, StringComparison.OrdinalIgnoreCase);

        var tooManyArguments = new McpServerProfileViewModel(new McpServerProfile())
        {
            ArgumentsText = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, McpClientSettingsStore.MaximumArgumentCount + 1))
        };
        var argumentError = Assert.Throws<InvalidOperationException>(tooManyArguments.ToModel);
        Assert.Contains("bounded settings limit", argumentError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeValidation_RejectsDraftsOutsidePersistedConnectionBounds()
    {
        var tooManyArguments = ValidHttpProfile();
        tooManyArguments.Arguments = Enumerable.Range(
                0,
                McpClientSettingsStore.MaximumArgumentCount + 1)
            .Select(index => index.ToString())
            .ToList();
        var duplicateDestinations = ValidHttpProfile();
        duplicateDestinations.EnvironmentVariables =
        [
            new McpEnvironmentVariableBinding
            {
                Name = "TOKEN",
                SourceEnvironmentVariable = "FIRST"
            },
            new McpEnvironmentVariableBinding
            {
                Name = "token",
                SourceEnvironmentVariable = "SECOND"
            }
        ];
        var oversizedEndpoint = ValidHttpProfile();
        oversizedEndpoint.Endpoint = new string(
            'x',
            McpClientSettingsStore.MaximumConnectionFieldCharacters + 1);

        Assert.NotNull(McpClientManager.Validate(tooManyArguments));
        Assert.NotNull(McpClientManager.Validate(duplicateDestinations));
        Assert.NotNull(McpClientManager.Validate(oversizedEndpoint));
    }

    [Fact]
    public void BoundaryHasher_StopsAfterObservedExtentAndOneGrowthSentinel()
    {
        using var stream = new EndlessReadStream();

        Assert.Throws<IOException>(() => McpBoundedFileHash.CalculateExactExtent(
            stream,
            expectedLength: 32,
            maximumBytes: 32,
            fileName: "mcp-clients.json",
            boundaryDescription: "Incoming MCP settings file"));

        Assert.Equal(33, stream.BytesRead);
    }

    [Fact]
    public void OversizedJsonSchema_IsRejectedWithoutMaterializingAnotherRawSchemaString()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            payload = new string('x', McpClientManager.MaximumRemoteSchemaCharacters)
        }));

        Assert.False(McpClientManager.IsBoundedJson(
            document.RootElement,
            McpClientManager.MaximumRemoteSchemaCharacters));
    }

    [Fact]
    public void Discovery_PreservesExistingChoiceAndApprovalGatesEveryNewTool()
    {
        var viewModel = new McpServerProfileViewModel(new McpServerProfile
        {
            Name = "Test",
            Tools =
            [
                new McpToolPolicy
                {
                    Name = "known",
                    Enabled = true,
                    RequiresApproval = false,
                    ReadOnlyHint = true,
                    SchemaFingerprint = "schema-known"
                }
            ]
        });

        viewModel.MergeDiscoveredTools(
        [
            new McpDiscoveredTool("known", "Known tool", true, false, "schema-known"),
            new McpDiscoveredTool("new_read", "New read tool", true, false, "schema-read"),
            new McpDiscoveredTool("new_write", "New tool", false, true, "schema-write")
        ]);

        var known = Assert.Single(viewModel.Tools, tool => tool.Name == "known");
        Assert.True(known.Enabled);
        Assert.False(known.RequiresApproval);
        var newReadTool = Assert.Single(viewModel.Tools, tool => tool.Name == "new_read");
        Assert.False(newReadTool.Enabled);
        Assert.True(newReadTool.RequiresApproval);
        Assert.True(newReadTool.ReadOnlyHint);
        var newTool = Assert.Single(viewModel.Tools, tool => tool.Name == "new_write");
        Assert.False(newTool.Enabled);
        Assert.True(newTool.RequiresApproval);
        Assert.True(newTool.DestructiveHint);
    }

    [Fact]
    public void DiscoveryNamePlan_WithholdsEveryCaseInsensitiveDuplicateAndFailsWholeOversizedScan()
    {
        var duplicates = McpClientManager.PlanDiscoveryNames(["Read", "read", "Unique"]);
        var oversized = McpClientManager.PlanDiscoveryNames(
            Enumerable.Range(0, McpClientManager.MaximumDiscoveryScanCount + 1)
                .Select(index => $"tool-{index}"));

        Assert.False(duplicates.ScanLimitExceeded);
        Assert.Contains("Read", duplicates.DuplicateNames);
        Assert.Contains("read", duplicates.DuplicateNames);
        Assert.True(oversized.ScanLimitExceeded);
        Assert.Empty(oversized.DuplicateNames);
    }

    [Fact]
    public void ChangedDiscoveryResetsAuthorityAndRetainsMissingSavedPolicies()
    {
        var viewModel = new McpServerProfileViewModel(new McpServerProfile
        {
            Name = "Test",
            Tools =
            [
                new McpToolPolicy
                {
                    Name = "changed",
                    Description = "Old",
                    Enabled = true,
                    RequiresApproval = false,
                    ReadOnlyHint = true,
                    SchemaFingerprint = "schema-old"
                },
                new McpToolPolicy
                {
                    Name = "temporarily_missing",
                    Description = "Retain me",
                    Enabled = true,
                    RequiresApproval = false,
                    ReadOnlyHint = true,
                    SchemaFingerprint = "schema-retained"
                }
            ]
        });

        viewModel.MergeDiscoveredTools(
        [
            new McpDiscoveredTool(
                "changed",
                "New",
                ReadOnlyHint: false,
                DestructiveHint: true,
                SchemaFingerprint: "schema-new")
        ]);

        var changed = Assert.Single(viewModel.Tools, tool => tool.Name == "changed");
        Assert.False(changed.Enabled);
        Assert.True(changed.RequiresApproval);
        Assert.True(changed.DestructiveHint);
        var missing = Assert.Single(
            viewModel.Tools,
            tool => tool.Name == "temporarily_missing");
        Assert.False(missing.WasAdvertisedInLastDiscovery);
        Assert.Equal("Retain me", missing.Description);
    }

    [Fact]
    public async Task CorruptSettings_FailClosedRemainUnchangedAndWithholdTheTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = McpClientSettingsStore.GetSettingsPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var corruptBytes = "{ this is not valid json"u8.ToArray();
            File.WriteAllBytes(path, corruptBytes);

            var loaded = McpClientSettingsStore.Load(root);
            var viewModel = new McpSettingsViewModel(new McpClientManager(root));
            viewModel.SaveCommand.Execute(null);
            await using var session = await new McpClientManager(root)
                .CreateEnabledToolSessionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(McpSettingsLoadStatus.FailedClosed, loaded.Status);
            Assert.Empty(loaded.Settings.Servers);
            Assert.NotNull(loaded.Error);
            Assert.Contains("mcp-clients.json", loaded.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(root, loaded.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            Assert.Contains("failed safely", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(corruptBytes, File.ReadAllBytes(path));
            Assert.Empty(session.Tools);
            Assert.Contains(session.Warnings, warning =>
                warning.Message.Contains("failed safely", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LockedSettings_FailClosedWithoutChangingBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            McpClientSettingsStore.Save(root, new McpClientSettings());
            var path = McpClientSettingsStore.GetSettingsPath(root);
            var expected = File.ReadAllBytes(path);
            McpClientSettingsLoadResult loaded;
            using (var locked = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                loaded = McpClientSettingsStore.Load(root);
            }

            Assert.Equal(McpSettingsLoadStatus.FailedClosed, loaded.Status);
            Assert.Equal(expected, File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OptimisticSaveConflict_PreservesTheNewerFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            McpClientSettingsStore.Save(root, new McpClientSettings());
            var path = McpClientSettingsStore.GetSettingsPath(root);
            var expectedRevision = McpClientSettingsBoundaryRevision.Capture(path);
            const string newerDocument = "{\"enabled\":false,\"servers\":[]}";
            File.WriteAllText(path, newerDocument);

            var error = Assert.Throws<InvalidOperationException>(() =>
                McpClientSettingsStore.Save(
                    root,
                    new McpClientSettings { Enabled = true },
                    expectedRevision));

            Assert.Contains("changed after Reload", error.Message, StringComparison.Ordinal);
            Assert.Equal(newerDocument, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OversizedSerializedDraft_PreservesTheExistingSettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            McpClientSettingsStore.Save(root, new McpClientSettings());
            var path = McpClientSettingsStore.GetSettingsPath(root);
            var original = File.ReadAllBytes(path);
            var oversized = new McpClientSettings
            {
                Servers = Enumerable.Range(0, 8)
                    .Select(serverIndex => new McpServerProfile
                    {
                        Id = $"server-{serverIndex}",
                        Name = $"Server {serverIndex}",
                        Tools = Enumerable.Range(
                                0,
                                McpClientSettingsStore.MaximumToolsPerServer)
                            .Select(toolIndex => new McpToolPolicy
                            {
                                Name = $"tool-{toolIndex}",
                                Description = new string(
                                    'x',
                                    McpClientSettingsStore.MaximumToolDescriptionCharacters)
                            })
                            .ToList()
                    })
                    .ToList()
            };

            Assert.Throws<InvalidOperationException>(() =>
                McpClientSettingsStore.Save(root, oversized));

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DuplicateLoadedServerIds_AreRepairedOnceAndRemainStableAcrossUiSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = McpClientSettingsStore.GetSettingsPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new McpClientSettings
            {
                Servers =
                [
                    new McpServerProfile { Id = "duplicate", Name = "First" },
                    new McpServerProfile { Id = "duplicate", Name = "Second" }
                ]
            }));
            var viewModel = new McpSettingsViewModel(new McpClientManager(root));

            viewModel.SaveCommand.Execute(null);
            var firstSaveIds = McpClientSettingsStore.LoadOrDefault(root).Servers
                .Select(server => server.Id)
                .ToArray();
            viewModel.SaveCommand.Execute(null);
            var secondSaveIds = McpClientSettingsStore.LoadOrDefault(root).Servers
                .Select(server => server.Id)
                .ToArray();

            Assert.Equal(2, firstSaveIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(firstSaveIds, secondSaveIds);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void InvalidOperationTimeout_IsRejectedInsteadOfSubstituted()
    {
        var viewModel = new McpServerProfileViewModel(new McpServerProfile())
        {
            ConnectionTimeoutText = "not-a-timeout"
        };

        var error = Assert.Throws<InvalidOperationException>(viewModel.ToModel);

        Assert.Contains("1 through 300", error.Message, StringComparison.Ordinal);
    }

    private static McpResolvedTool ResolvedTool(
        AIFunction function,
        string serverId,
        string serverName,
        string originalName) =>
        new(
            function,
            serverId,
            serverName,
            originalName,
            ConfiguredEnabled: true,
            RequiresApproval: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            ConfiguredDeclarationFingerprint: CapabilitySchemaIdentity.Calculate(function),
            InvocationTimeoutSeconds: 30,
            SchemaFingerprint: CapabilitySchemaIdentity.Calculate(function));

    private static McpServerProfile ValidHttpProfile() => new()
    {
        Id = "server-a",
        Name = "Server A",
        Transport = McpTransportKinds.Http,
        Endpoint = "http://127.0.0.1:65530/mcp",
        ConnectionTimeoutSeconds = 30
    };

    private sealed class EndlessReadStream : Stream
    {
        public int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
