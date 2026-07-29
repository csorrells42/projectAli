using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class WebCodingProviderTests
{
    [Fact]
    public async Task UnifiedProviderAnalyzesBuildsAndTestsARealNodeWorkspace()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Web/package.json", "{\"type\":\"module\"}", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/index.html", "<!doctype html><html><body><main id='app'>Ali</main></body></html>", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/math.js", "export const add = (left, right) => left + right;", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/math.test.js", "import test from 'node:test'; import assert from 'node:assert/strict'; import { add } from './math.js'; test('add', () => assert.equal(add(2, 3), 5));", TestContext.Current.CancellationToken);

            await using var module = new AliCodingModule(access);
            var inspection = module.MultiLanguage.InspectProject("Workspace/Web/package.json");
            var analysis = await module.MultiLanguage.AnalyzeAsync("Workspace/Web/package.json", TestContext.Current.CancellationToken);
            var build = await module.MultiLanguage.BuildAsync("Workspace/Web/package.json", null, TestContext.Current.CancellationToken);
            var test = await module.MultiLanguage.TestAsync("Workspace/Web/package.json", null, TestContext.Current.CancellationToken);
            var run = await module.MultiLanguage.RunAsync("Workspace/Web/math.js", null, TestContext.Current.CancellationToken);

            Assert.Equal("web-node", inspection.Provider);
            Assert.True(analysis.Success, analysis.Output);
            Assert.True(build.Success, build.Output);
            Assert.True(test.Success, test.Output);
            Assert.Contains("pass 1", test.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(run.Success, run.Output);
        });
    }

    [Fact]
    public async Task UnifiedProviderDetectsMalformedMixedWebContentWithoutLanguageGuessing()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Web/package.json", "{\"scripts\":{}}", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Web/broken.ts", "export function broken() { return [1, 2; }", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var result = await module.MultiLanguage.AnalyzeAsync("Workspace/Web/broken.ts", TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("delimiter", result.Output, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliWebProviderTests", Guid.NewGuid().ToString("N"));
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
