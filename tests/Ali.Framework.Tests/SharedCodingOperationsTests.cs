using System.Net;
using System.Text;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Operations;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class SharedCodingOperationsTests
{
    [Fact]
    public async Task Python_RunAndLargeProjectContext_UseSharedProviderInfrastructure()
    {
        await WithModuleAsync(async (root, module) =>
        {
            var project = Path.Combine(root, "workspace", "sample");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "app.py"),
                "def calculate_total(values):\n    return sum(values)\n\nprint(calculate_total([2, 3, 5]))\n",
                TestContext.Current.CancellationToken);
            for (var index = 0; index < 40; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(project, $"noise_{index}.py"),
                    $"def unrelated_{index}():\n    return {index}\n",
                    TestContext.Current.CancellationToken);
            }

            var run = await module.MultiLanguage.RunAsync(
                "Workspace/sample/app.py", null, TestContext.Current.CancellationToken);
            var context = await module.Operations.BuildContextAsync(
                "Workspace/sample/app.py", "where is calculate_total implemented", 4, 8_000,
                TestContext.Current.CancellationToken);

            Assert.True(run.Success, run.Output);
            Assert.Contains("10", run.Output, StringComparison.Ordinal);
            Assert.True(context.Success);
            Assert.InRange(context.FilesSelected, 1, 4);
            Assert.Contains(context.Snippets, snippet =>
                snippet.File.Equals("app.py", StringComparison.OrdinalIgnoreCase)
                && snippet.Text.Contains("calculate_total", StringComparison.Ordinal));
            Assert.True(context.CharactersReturned <= 8_000);
        });
    }

    [Fact]
    public async Task HttpProbe_IsBoundedAndRequiresAProjectContext()
    {
        await WithModuleAsync(async (root, module) =>
        {
            var project = Path.Combine(root, "workspace", "probe");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "app.py"), "print('probe')\n",
                TestContext.Current.CancellationToken);
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 100_000), Encoding.UTF8, "application/json")
            };
            using var http = new HttpClient(new StubHandler(response));
            using var operations = new AliCodingOperations(
                new AliLanguageProjectResolver(CreateAccess(root)),
                module.LanguageProviders,
                http);

            var result = await operations.ProbeHttpAsync(
                "Workspace/probe/app.py", "https://service.example/status", "GET",
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.True(result.ResponsePreview.Length <= 65_536);
            Assert.True(result.Truncated);
            await Assert.ThrowsAsync<ArgumentException>(() => operations.ProbeHttpAsync(
                "Workspace/probe/app.py", "file:///c:/secret.txt", "GET",
                TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task CapabilityReport_SeparatesDeclaredFromActuallyAvailableCapabilities()
    {
        await WithModuleAsync((root, module) =>
        {
            var report = module.MultiLanguage.GetCapabilities();
            Assert.All(report.Providers, provider =>
                Assert.Equal(provider.AvailableCapabilities & provider.Capabilities, provider.AvailableCapabilities));
            Assert.Contains(report.Providers, provider => provider.AvailableCapabilities.HasFlag(AliLanguageCapability.Run));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingRunProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingProbeServiceName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingInspectProcessName));
            Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingBuildContextName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(
                AliCapabilityCatalog.CodingBuildContextName,
                AgentPermissionProfile.LockedDown));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RuntimeSnapshot_ReportsLiveProcessEvidence()
    {
        await WithModuleAsync(async (root, module) =>
        {
            var project = Path.Combine(root, "workspace", "runtime");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "app.py"), "print('runtime')\n",
                TestContext.Current.CancellationToken);

            var snapshot = module.Operations.InspectProcess(
                "Workspace/runtime/app.py", Environment.ProcessId);

            Assert.True(snapshot.Success, snapshot.Summary);
            Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
            Assert.True(snapshot.WorkingSetBytes > 0);
            Assert.True(snapshot.ThreadCount > 0);
        });
    }

    private static async Task WithModuleAsync(Func<string, AliCodingModule, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliSharedCodingOperationsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var module = new AliCodingModule(CreateAccess(root));
            await action(root, module);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static AliWorkstationFileAccess CreateAccess(string root)
    {
        var permissions = new AgentToolPermissionStore(root);
        var store = new AliWorkstationFileStore(
            [new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))],
            Path.Combine(root, "trash"));
        return new AliWorkstationFileAccess(
            store,
            new AgentFileActionAuditStore(root, activeUsers: null),
            permissions);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
