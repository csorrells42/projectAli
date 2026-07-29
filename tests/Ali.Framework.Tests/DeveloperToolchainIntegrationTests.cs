using Ali.Modules.Coding;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class DeveloperToolchainIntegrationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task VisualStudioAndGccAreRealAndGccCompilesRunsAndTestsCpp()
    {
        await WithModuleAsync(async (_, access, module) =>
        {
            await access.Store.WriteAsync("Workspace/Native/main.cpp", "#include <iostream>\nint main(){std::cout << \"ALI_GCC_OK\"; return 0;}\n", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Native/math.cpp", "int answer(){return 42;}\n", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Native/math_test.cpp", "int answer(); int main(){return answer()==42 ? 0 : 1;}\n", TestContext.Current.CancellationToken);

            var visualStudio = module.VisualStudio.Inspect();
            var gcc = module.GnuNative.Inspect();
            Assert.True(visualStudio.Success);
            Assert.Contains(visualStudio.Instances, item => item.Ide && item.MsBuild && item.Msvc && item.CMake && item.Ninja && item.TestPlatform);
            Assert.True(gcc.Success, gcc.Summary);

            var analyze = await module.GnuNative.ExecuteAsync("Workspace/Native/main.cpp", "analyze", "Debug", TestContext.Current.CancellationToken);
            var build = await module.GnuNative.ExecuteAsync("Workspace/Native/main.cpp", "build", "Release", TestContext.Current.CancellationToken);
            var test = await module.GnuNative.ExecuteAsync("Workspace/Native/main.cpp", "test", "Debug", TestContext.Current.CancellationToken);
            var run = await module.GnuNative.ExecuteAsync("Workspace/Native/main.cpp", "run", "Release", TestContext.Current.CancellationToken);
            Assert.True(analyze.Success, analyze.Output);
            Assert.True(build.Success, build.Output);
            Assert.True(test.Success, test.Output);
            Assert.True(run.Success, run.Output);
            Assert.Contains("ALI_GCC_OK", run.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ArduinoCliCompilesUnoAndPicoSketchesAndSharesTheInstalledIdeStore()
    {
        await WithModuleAsync(async (_, access, module) =>
        {
            const string sketch = "void setup(){pinMode(LED_BUILTIN, OUTPUT);}\nvoid loop(){digitalWrite(LED_BUILTIN, HIGH); delay(10); digitalWrite(LED_BUILTIN, LOW); delay(10);}\n";
            await access.Store.WriteAsync("Workspace/Blink/Blink.ino", sketch, TestContext.Current.CancellationToken);
            var inspection = await module.Arduino.InspectAsync(TestContext.Current.CancellationToken);
            Assert.True(inspection.Success, inspection.Summary);
            Assert.True(File.Exists(inspection.Ide));
            Assert.Contains(inspection.InstalledCores, core => core.Contains("arduino:avr", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(inspection.InstalledCores, core => core.Contains("arduino:mbed_rp2040", StringComparison.OrdinalIgnoreCase));

            var uno = await module.Arduino.CompileAsync("Workspace/Blink/Blink.ino", "arduino:avr:uno", TestContext.Current.CancellationToken);
            var pico = await module.Arduino.CompileAsync("Workspace/Blink/Blink.ino", "arduino:mbed_rp2040:pico", TestContext.Current.CancellationToken);
            Assert.True(uno.Success, uno.Output);
            Assert.True(pico.Success, pico.Output);
            Assert.NotEmpty(uno.Artifacts);
            Assert.NotEmpty(pico.Artifacts);
        });
    }

    [Fact]
    public async Task EmbeddedToolsAreModelCallableMcpReadyPermissionProtectedAndLegacyBridgeFree()
    {
        await WithModuleAsync((_, _, module) =>
        {
            var names = new[]
            {
                AliCapabilityCatalog.VisualStudioInspectName, AliCapabilityCatalog.VisualStudioBuildName, AliCapabilityCatalog.VisualStudioOpenName,
                AliCapabilityCatalog.GnuNativeInspectName, AliCapabilityCatalog.GnuNativeExecuteName,
                AliCapabilityCatalog.ArduinoInspectName, AliCapabilityCatalog.ArduinoSearchLibrariesName,
                AliCapabilityCatalog.ArduinoInstallCoreName, AliCapabilityCatalog.ArduinoInstallLibraryName,
                AliCapabilityCatalog.ArduinoCompileName, AliCapabilityCatalog.ArduinoUploadName, AliCapabilityCatalog.ArduinoOpenIdeName,
                AliCapabilityCatalog.RaspberryPiLibrariesName, AliCapabilityCatalog.RaspberryPiProbeName,
                AliCapabilityCatalog.RaspberryPiInspectLibrariesName, AliCapabilityCatalog.RaspberryPiSearchPackagesName,
                AliCapabilityCatalog.RaspberryPiDeployName
            };
            var functions = module.CreateFunctions();
            var policies = McpServerToolCatalog.CreateDefaultPolicies();
            Assert.All(names, name => Assert.Contains(functions, function => function.Name == name));
            Assert.All(names, name => Assert.Contains(AliCapabilityCatalog.Tools, capability => capability.Name == name));
            Assert.All(names, name => Assert.Contains(policies, policy => policy.Name == name && !policy.Enabled));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.VisualStudioBuildName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.GnuNativeExecuteName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ArduinoUploadName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RaspberryPiDeployName));

            var libraries = module.RaspberryPi.GetLibraryCatalog();
            Assert.Contains(libraries, item => item.Name == "GPIO Zero" && item.Language == "Python");
            Assert.Contains(libraries, item => item.Name == "libgpiod" && item.Language == "C/C++");
            Assert.Contains(libraries, item => item.Name == "Pico SDK");

            var staleFiles = Directory.EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetExtension(path) is ".vsix" or ".vsixmanifest")
                .ToArray();
            Assert.Empty(staleFiles);
            return Task.CompletedTask;
        });
    }

    private static async Task WithModuleAsync(Func<string, AliWorkstationFileAccess, AliCodingModule, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDeveloperToolchainTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore([new("Workspace", Path.Combine(root, "workspace"))], Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(store, new AgentFileActionAuditStore(root, null), permissions);
            await using var module = new AliCodingModule(access);
            await action(root, access, module);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
