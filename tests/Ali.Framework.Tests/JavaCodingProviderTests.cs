using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class JavaCodingProviderTests
{
    [Fact]
    public async Task JavaProviderAnalyzesBuildsAndRunsExecutableTestsWithAssertions()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Java/pom.xml", "<project><modelVersion>4.0.0</modelVersion><groupId>ali</groupId><artifactId>sample</artifactId><version>1.0</version></project>", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Java/src/ali/Calculator.java", "package ali; public final class Calculator { public static int add(int a, int b) { return a + b; } }", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Java/test/ali/CalculatorTest.java", "package ali; public final class CalculatorTest { public static void main(String[] args) { assert Calculator.add(2, 3) == 5 : \"sum\"; System.out.println(\"PASS\"); } }", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);

            var inspection = module.MultiLanguage.InspectProject("Workspace/Java/pom.xml");
            var analysis = await module.MultiLanguage.AnalyzeAsync("Workspace/Java/pom.xml", TestContext.Current.CancellationToken);
            var build = await module.MultiLanguage.BuildAsync("Workspace/Java/pom.xml", null, TestContext.Current.CancellationToken);
            var test = await module.MultiLanguage.TestAsync("Workspace/Java/pom.xml", null, TestContext.Current.CancellationToken);

            Assert.Equal(AliProgrammingLanguage.Java, inspection.Language);
            Assert.Equal("java-temurin", inspection.Provider);
            Assert.True(analysis.Success, analysis.Output);
            Assert.True(build.Success, build.Output);
            Assert.True(test.Success, test.Output);
            Assert.Contains("PASS", test.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task JavaProviderReturnsRealCompilerDiagnostics()
    {
        await WithAccessAsync(async (_, access) =>
        {
            await access.Store.WriteAsync("Workspace/Java/App.java", "public class App { public static void main(String[] args) { missing( } }", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var result = await module.MultiLanguage.AnalyzeAsync("Workspace/Java/App.java", TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("error", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("App.java", result.Output, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task WithAccessAsync(Func<string, AliWorkstationFileAccess, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliJavaProviderTests", Guid.NewGuid().ToString("N"));
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
