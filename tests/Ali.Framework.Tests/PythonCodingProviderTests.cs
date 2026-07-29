using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class PythonCodingProviderTests
{
    [Fact]
    public async Task PythonProviderAnalyzesBuildsAndTestsThroughPortableRuntime()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Python/pyproject.toml", "[project]\nname='ali_sample'\nversion='1.0.0'", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Python/calculator.py", "def add(left: int, right: int) -> int:\n    return left + right", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Python/test_calculator.py", "import unittest\nfrom calculator import add\n\nclass CalculatorTests(unittest.TestCase):\n    def test_add(self):\n        self.assertEqual(5, add(2, 3))", TestContext.Current.CancellationToken);

            await using var module = new AliCodingModule(access);
            var inspection = module.MultiLanguage.InspectProject("Workspace/Python/pyproject.toml");
            var analysis = await module.MultiLanguage.AnalyzeAsync("Workspace/Python/pyproject.toml", TestContext.Current.CancellationToken);
            var build = await module.MultiLanguage.BuildAsync("Workspace/Python/pyproject.toml", null, TestContext.Current.CancellationToken);
            var tests = await module.MultiLanguage.TestAsync("Workspace/Python/pyproject.toml", null, TestContext.Current.CancellationToken);

            Assert.Equal(AliProgrammingLanguage.Python, inspection.Language);
            Assert.Equal("python-cpython", inspection.Provider);
            Assert.True(analysis.Success, analysis.Output);
            Assert.Contains("calculator", analysis.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(build.Success, build.Output);
            Assert.True(tests.Success, tests.Output);
            Assert.Contains("test_add", tests.Output, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task PythonProviderReportsOptionalToolingTruthfully()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Python/app.py", "print('hello')", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var report = module.MultiLanguage.GetCapabilities();
            var provider = Assert.Single(report.Providers, item => item.Id == "python-cpython");

            Assert.Contains(provider.Toolchains, tool => tool.Name == "python" && tool.Available);
            Assert.Contains(provider.Toolchains, tool => tool.Name == "ruff");
            Assert.Contains(provider.Toolchains, tool => tool.Name == "basedpyright");
            Assert.Contains(provider.Toolchains, tool => tool.Name == "debugpy");
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliPythonProviderTests", Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
