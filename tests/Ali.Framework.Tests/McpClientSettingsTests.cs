using Ali.Modules.Mcp;
using Ali.UI.ViewModels;

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

            Assert.True(restored.Enabled);
            var server = Assert.Single(restored.Servers);
            Assert.Equal(McpTransportKinds.Stdio, server.Transport);
            Assert.Equal(["--root", "C:\\Approved"], server.Arguments);
            Assert.Equal("ALI_MCP_TOKEN", Assert.Single(server.EnvironmentVariables).SourceEnvironmentVariable);
            var tool = Assert.Single(server.Tools);
            Assert.True(tool.Enabled);
            Assert.False(tool.RequiresApproval);
            Assert.True(tool.ReadOnlyHint);
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

        Assert.Equal("mcp_my_files_notes_read_document", name);
        Assert.Matches("^[a-z0-9_]+$", name);
    }

    [Fact]
    public void Discovery_PreservesExistingChoiceAndLocksNewToolsDown()
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
                    RequiresApproval = false
                }
            ]
        });

        viewModel.MergeDiscoveredTools(
        [
            new McpDiscoveredTool("known", "Known tool", true, false),
            new McpDiscoveredTool("new_write", "New tool", false, true)
        ]);

        var known = Assert.Single(viewModel.Tools, tool => tool.Name == "known");
        Assert.True(known.Enabled);
        Assert.False(known.RequiresApproval);
        var newTool = Assert.Single(viewModel.Tools, tool => tool.Name == "new_write");
        Assert.False(newTool.Enabled);
        Assert.True(newTool.RequiresApproval);
        Assert.True(newTool.DestructiveHint);
    }
}
