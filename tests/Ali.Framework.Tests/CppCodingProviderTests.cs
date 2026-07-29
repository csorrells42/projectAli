using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class CppCodingProviderTests
{
    [Fact]
    public async Task CppProviderAnalyzesBuildsAndRunsNativeTests()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Cpp/CMakeLists.txt", "cmake_minimum_required(VERSION 3.25)\nproject(AliSample LANGUAGES CXX)", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Cpp/calculator.hpp", "#pragma once\nint add(int left, int right);", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Cpp/calculator.cpp", "#include \"calculator.hpp\"\nint add(int left, int right) { return left + right; }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Cpp/main.cpp", "#include <iostream>\n#include \"calculator.hpp\"\nint main() { std::cout << add(2, 3); return 0; }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Cpp/calculator_test.cpp", "#include \"calculator.hpp\"\n#include <iostream>\nint main() { if (add(2, 3) != 5) return 1; std::cout << \"PASS\"; return 0; }", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);

            var inspection = module.MultiLanguage.InspectProject("Workspace/Cpp/CMakeLists.txt");
            var analysis = await module.MultiLanguage.AnalyzeAsync("Workspace/Cpp/CMakeLists.txt", TestContext.Current.CancellationToken);
            var build = await module.MultiLanguage.BuildAsync("Workspace/Cpp/CMakeLists.txt", "Release", TestContext.Current.CancellationToken);
            var test = await module.MultiLanguage.TestAsync("Workspace/Cpp/CMakeLists.txt", null, TestContext.Current.CancellationToken);
            var run = await module.MultiLanguage.RunAsync("Workspace/Cpp/CMakeLists.txt", "Release", TestContext.Current.CancellationToken);

            Assert.Equal(AliProgrammingLanguage.Cpp, inspection.Language);
            Assert.Equal("cpp-msvc", inspection.Provider);
            Assert.True(analysis.Success, analysis.Output);
            Assert.True(build.Success, build.Output);
            Assert.Contains(build.Artifacts, File.Exists);
            Assert.True(test.Success, test.Output);
            Assert.Contains("PASS", test.Output, StringComparison.Ordinal);
            Assert.True(run.Success, run.Output);
            Assert.Contains("5", run.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CppProviderReturnsRealClangDiagnostics()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Cpp/broken.cpp", "int main() { return missing_symbol; }", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var result = await module.MultiLanguage.AnalyzeAsync("Workspace/Cpp/broken.cpp", TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("missing_symbol", result.Output, StringComparison.Ordinal);
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliCppProviderTests", Guid.NewGuid().ToString("N"));
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
