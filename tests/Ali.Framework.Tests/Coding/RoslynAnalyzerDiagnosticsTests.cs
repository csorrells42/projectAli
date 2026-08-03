using System.Collections.Immutable;
using System.Security;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class RoslynAnalyzerDiagnosticsTests
{
    [Fact]
    public async Task Analyze_IncludesExactProjectAnalyzersAndRetainsCompilerDiagnostics()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliRoslynAnalyzerTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var analyzerPath = typeof(AliCustomProjectAnalyzer).Assembly.Location;

            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            var escapedAnalyzerPath = SecurityElement.Escape(analyzerPath)
                ?? throw new InvalidOperationException("The analyzer test path could not be escaped.");
            await access.Store.WriteAsync(
                "Workspace/AnalyzerTarget/AnalyzerTarget.csproj",
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="{{escapedAnalyzerPath}}" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/AnalyzerTarget/Broken.cs",
                """
                namespace AnalyzerTarget;

                public sealed class Broken
                {
                    public MissingType Value { get; }
                }
                """,
                TestContext.Current.CancellationToken);

            var resolver = new AliCodingProjectResolver(access);
            var tools = new AliRoslynCodingTools(
                resolver,
                new AliCodingProjectTracker(),
                Path.Combine(root, "roslyn-analyzer-audit.jsonl"));
            var first = await tools.AnalyzeAsync(
                "Workspace/AnalyzerTarget/AnalyzerTarget.csproj",
                TestContext.Current.CancellationToken);
            var second = await tools.AnalyzeAsync(
                "Workspace/AnalyzerTarget/AnalyzerTarget.csproj",
                TestContext.Current.CancellationToken);

            Assert.False(first.Success);
            Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Id == "ALI9001");
            Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Id == "CS0246");
            Assert.True(first.Diagnostics.Count <= 200);
            Assert.Contains("loaded analyzers", first.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                first.Diagnostics.Select(DiagnosticIdentity),
                second.Diagnostics.Select(DiagnosticIdentity));

            var compilerIndex = first.Diagnostics
                .Select((diagnostic, index) => (diagnostic, index))
                .Single(item => item.diagnostic.Id == "CS0246")
                .index;
            var analyzerIndex = first.Diagnostics
                .Select((diagnostic, index) => (diagnostic, index))
                .Single(item => item.diagnostic.Id == "ALI9001")
                .index;
            Assert.True(compilerIndex < analyzerIndex);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task QualityScan_UsesCompilerOnlyDiagnosticsWithoutExecutingProjectAnalyzersOrGenerators()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliRoslynCompilerOnlyTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var analyzerPath = typeof(AliCustomProjectAnalyzer).Assembly.Location;

            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            var escapedAnalyzerPath = SecurityElement.Escape(analyzerPath)
                ?? throw new InvalidOperationException("The analyzer test path could not be escaped.");
            await access.Store.WriteAsync(
                "Workspace/CompilerOnlyTarget/CompilerOnlyTarget.csproj",
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="{{escapedAnalyzerPath}}" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/CompilerOnlyTarget/UsesGeneratedType.cs",
                """
                namespace CompilerOnlyTarget;

                public sealed class UsesGeneratedType
                {
                    public ProjectGeneratedType Value { get; } = new();
                }
                """,
                TestContext.Current.CancellationToken);

            var resolver = new AliCodingProjectResolver(access);
            var tools = new AliRoslynCodingTools(
                resolver,
                new AliCodingProjectTracker(),
                Path.Combine(root, "roslyn-compiler-only-audit.jsonl"));

            var analyzerCapable = await tools.AnalyzeAsync(
                "Workspace/CompilerOnlyTarget/CompilerOnlyTarget.csproj",
                TestContext.Current.CancellationToken);
            Assert.Contains(analyzerCapable.Diagnostics, diagnostic => diagnostic.Id == "ALI9001");
            Assert.DoesNotContain(
                analyzerCapable.Diagnostics,
                diagnostic => IsMissingGeneratedType(diagnostic));

            var compilerOnly = await tools.AnalyzeCompilerOnlyAsync(
                "Workspace/CompilerOnlyTarget/CompilerOnlyTarget.csproj",
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(compilerOnly.Diagnostics, diagnostic => diagnostic.Id == "ALI9001");
            Assert.Contains(compilerOnly.Diagnostics, diagnostic => IsMissingGeneratedType(diagnostic));
            Assert.Contains(
                "compiler diagnostics only",
                compilerOnly.Summary,
                StringComparison.OrdinalIgnoreCase);

            var quality = new AliQualityEngineering(resolver, tools);
            var result = await quality.ScanAsync(
                "Workspace/CompilerOnlyTarget/CompilerOnlyTarget.csproj",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "ALI9001");
            Assert.Contains(result.Findings, finding => IsMissingGeneratedType(finding));
            Assert.True(File.Exists(result.SarifPath));
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    private static string DiagnosticIdentity(RoslynDiagnosticItem diagnostic) =>
        string.Join(
            "|",
            diagnostic.Severity,
            diagnostic.File,
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Id,
            diagnostic.Message);

    private static bool IsMissingGeneratedType(RoslynDiagnosticItem diagnostic) =>
        diagnostic.Id == "CS0246"
        && diagnostic.Message.Contains("ProjectGeneratedType", StringComparison.Ordinal);

    private static bool IsMissingGeneratedType(QualityFinding finding) =>
        finding.RuleId == "CS0246"
        && finding.Message.Contains("ProjectGeneratedType", StringComparison.Ordinal);

    private static async Task DeleteTemporaryRootAsync(string root)
    {
        for (var attempt = 1; Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 20
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}

// This analyzer intentionally lives in the test assembly so Roslyn can load a real project
// analyzer without leaving a locked temporary DLL behind on Windows.
#pragma warning disable RS1036, RS1037, RS1038, RS1041, RS2008
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AliCustomProjectAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        "ALI9001",
        "Ali analyzer test",
        "Custom analyzer diagnostic",
        "Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(static analysis =>
            analysis.ReportDiagnostic(Diagnostic.Create(Rule, Location.None)));
    }
}

[Generator(LanguageNames.CSharp)]
public sealed class AliCustomProjectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "ProjectGeneratedType.g.cs",
            """
            namespace CompilerOnlyTarget;

            public sealed class ProjectGeneratedType
            {
            }
            """));
    }
}
#pragma warning restore RS1036, RS1037, RS1038, RS1041, RS2008
