using Ali.Modules.Coding;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class CrossLanguageArchitectureTests
{
    [Fact]
    public async Task ArchitectureMapsMixedLanguagesCyclesHotspotsAndMermaid()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Mixed/pyproject.toml", "[project]\nname='mixed'", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/a.py", "import b\ndef first(): return b.second()", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/b.py", "import a\ndef second(): return 2", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/web/app.ts", "import { render } from './view'; export const start = render;", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/web/view.ts", "export function render() { return 'ok'; }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/native/main.cpp", "#include \"engine.hpp\"\nint main() { return run(); }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/native/engine.hpp", "#pragma once\nint run();", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);

            var report = await module.CrossLanguageArchitecture.InspectAsync("Workspace/Mixed/pyproject.toml", TestContext.Current.CancellationToken);

            Assert.True(report.Success);
            Assert.True(report.Files >= 7);
            Assert.Contains(report.Edges, edge => edge.Source == "a.py" && edge.Target == "b.py" && edge.Internal);
            Assert.Contains(report.Edges, edge => edge.Source == "web/app.ts" && edge.Target == "web/view.ts" && edge.Internal);
            Assert.Contains(report.Edges, edge => edge.Source == "native/main.cpp" && edge.Target == "native/engine.hpp" && edge.Internal);
            Assert.Contains(report.Cycles, cycle => cycle.Contains("a.py") && cycle.Contains("b.py"));
            Assert.Contains("flowchart LR", report.Mermaid, StringComparison.Ordinal);
            Assert.NotEmpty(report.Hotspots);
        });
    }

    [Fact]
    public async Task ArchitectureToolIsCatalogedForMcpAndLockedDownReads()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await using var module = new AliCodingModule(access);
            Assert.Contains(module.CreateFunctions(), function => function.Name == AliCapabilityCatalog.CodingInspectArchitectureName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.CodingInspectArchitectureName);
            Assert.Contains(McpServerToolCatalog.CreateDefaultPolicies(), policy => policy.Name == AliCapabilityCatalog.CodingInspectArchitectureName && !policy.Enabled);
            Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingInspectArchitectureName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingInspectArchitectureName, AgentPermissionProfile.LockedDown));
            await Task.CompletedTask;
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliCrossLanguageArchitectureTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore([new("Workspace", Path.Combine(root, "workspace"))], Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(store, new AgentFileActionAuditStore(root, activeUsers: null), permissions);
            await action(root, access);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
