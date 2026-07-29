using System.Text;
using System.Text.Json.Nodes;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Indexing;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Protocols;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class MultiLanguageCodingFoundationTests
{
    [Fact]
    public async Task ResolverDetectsManifestAndSourceLanguagesWithoutEnglishRouting()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Python/pyproject.toml", "[project]\nname='sample'", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Python/src/app.py", "def main():\n    return 42", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/package.json", "{\"scripts\":{}}", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/src/app.ts", "export function start() {}", TestContext.Current.CancellationToken);

            var resolver = new AliLanguageProjectResolver(access);
            var python = resolver.Resolve("Workspace/Python/src/app.py");
            var web = resolver.Resolve("Workspace/Web/src/app.ts");

            Assert.Equal(AliProgrammingLanguage.Python, python.Language);
            Assert.Equal("pyproject.toml", python.ManifestName);
            Assert.Equal(AliProgrammingLanguage.TypeScript, web.Language);
            Assert.Equal("package.json", web.ManifestName);
            Assert.EndsWith(Path.Combine("workspace", "Python"), python.ProjectDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("workspace", "Web"), web.ProjectDirectory, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ResolverAcceptsAProjectFolderWhenItContainsOneUnambiguousManifest()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync(
                "Workspace/Game/Game.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.Current.CancellationToken);

            var resolved = new AliLanguageProjectResolver(access).Resolve("Workspace/Game");
            var dotnetProject = new AliCodingProjectResolver(access).ResolveExistingProject("Workspace/Game");
            var roslynTarget = new AliCodingProjectResolver(access).ResolveExistingTarget("Workspace/Game");

            Assert.Equal(AliProgrammingLanguage.CSharp, resolved.Language);
            Assert.Equal("Game.csproj", resolved.ManifestName);
            Assert.EndsWith("Game.csproj", resolved.PhysicalPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("workspace", "Game"), resolved.ProjectDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Game.csproj", dotnetProject.PhysicalPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Game.csproj", roslynTarget.PhysicalPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task StructuralIndexFindsSymbolsAcrossAllRequestedLanguagesAndSkipsBuildTrees()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Mixed/pyproject.toml", "[project]\nname='mixed'", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/app.py", "class PythonEngine:\n    pass\n\ndef calculate():\n    return 1", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/site.ts", "export interface WebContract {}\nexport function render() {}", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/Worker.java", "public class JavaWorker {\n public void execute() {}\n}", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/native.cpp", "class NativeWorker {};\nint compute() { return 1; }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Mixed/bin/ignored.py", "class MustNotAppear: pass", TestContext.Current.CancellationToken);

            var resolver = new AliLanguageProjectResolver(access);
            var index = new AliSourceIndexService(resolver);
            var result = await index.BuildAsync("Workspace/Mixed/pyproject.toml", TestContext.Current.CancellationToken);
            var search = await index.SearchAsync("Workspace/Mixed/pyproject.toml", "Worker", 20, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.False(result.Truncated);
            Assert.Contains(result.Symbols, symbol => symbol.Name == "PythonEngine" && symbol.Language == AliProgrammingLanguage.Python);
            Assert.Contains(result.Symbols, symbol => symbol.Name == "WebContract" && symbol.Language == AliProgrammingLanguage.TypeScript);
            Assert.Contains(result.Symbols, symbol => symbol.Name == "JavaWorker" && symbol.Language == AliProgrammingLanguage.Java);
            Assert.Contains(result.Symbols, symbol => symbol.Name == "NativeWorker" && symbol.Language == AliProgrammingLanguage.Cpp);
            Assert.DoesNotContain(result.Symbols, symbol => symbol.Name == "MustNotAppear");
            Assert.Equal(2, search.Matches.Count(symbol => symbol.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase)));
        });
    }

    [Fact]
    public async Task ContentLengthTransportRoundTripsLspAndDapFraming()
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 7,
            ["result"] = new JsonObject { ["answer"] = 42 }
        };
        var framed = AliContentLengthMessageTransport.Frame(message);
        await using var incoming = new MemoryStream(framed);
        await using var outgoing = new MemoryStream();
        await using var transport = new AliContentLengthMessageTransport(outgoing, incoming, leaveOpen: true);

        var parsed = await transport.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, parsed?["id"]?.GetValue<int>());
        Assert.Equal(42, parsed?["result"]?["answer"]?.GetValue<int>());

        await transport.WriteAsync(new JsonObject { ["seq"] = 1, ["type"] = "request" }, TestContext.Current.CancellationToken);
        var outputText = Encoding.ASCII.GetString(outgoing.ToArray());
        Assert.StartsWith("Content-Length:", outputText, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"request\"", outputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodingModulePublishesOneGenericMcpCompatibleSurfaceAndLiveCapabilities()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await using var module = new AliCodingModule(access);
            var functions = module.CreateFunctions();
            var report = module.MultiLanguage.GetCapabilities();

            var expected = new[]
            {
                AliCapabilityCatalog.CodingListCapabilitiesName,
                AliCapabilityCatalog.CodingInspectProjectName,
                AliCapabilityCatalog.CodingIndexProjectName,
                AliCapabilityCatalog.CodingSearchSymbolsName,
                AliCapabilityCatalog.CodingAnalyzeProjectName,
                AliCapabilityCatalog.CodingFormatProjectName,
                AliCapabilityCatalog.CodingBuildProjectName,
                AliCapabilityCatalog.CodingTestProjectName,
                AliCapabilityCatalog.CodingRunProjectName,
                AliCapabilityCatalog.CodingBuildContextName,
                AliCapabilityCatalog.CodingProbeServiceName,
                AliCapabilityCatalog.CodingInspectProcessName
            };
            Assert.All(expected, name => Assert.Contains(functions, function => function.Name == name));
            Assert.All(expected, name => Assert.Contains(AliCapabilityCatalog.Tools, capability => capability.Name == name));
            Assert.All(expected, name => Assert.Contains(McpServerToolCatalog.CreateDefaultPolicies(), policy => policy.Name == name && !policy.Enabled));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingFormatProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingBuildProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingTestProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingRunProjectName));
            Assert.Contains(report.Providers, provider => provider.Id == "dotnet-roslyn" && provider.Capabilities.HasFlag(AliLanguageCapability.Debug));
            Assert.Contains(report.SharedInfrastructure, item => item.Contains("Language Server Protocol", StringComparison.Ordinal));
            Assert.Contains(report.SharedInfrastructure, item => item.Contains("Debug Adapter Protocol", StringComparison.Ordinal));
            await Task.CompletedTask;
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMultiLanguageFoundationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            await action(root, access);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
