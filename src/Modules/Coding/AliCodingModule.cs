using Ali.Modules.Coordinator;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coding;

/// <summary>
/// Composition root for Ali's C# engineering capability. The coordinator and MCP
/// server share this one module instance and the same bounded tool definitions.
/// </summary>
public sealed class AliCodingModule
{
    internal AliCodingModule(AliWorkstationFileAccess fileAccess)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        var auditPath = Path.Combine(Path.GetDirectoryName(fileAccess.Audit.Path)!, "dotnet-actions.jsonl");
        var tracker = new AliCodingProjectTracker();
        var resolver = new AliCodingProjectResolver(fileAccess);
        ProjectScaffolder = new AliDotNetProjectScaffolder(fileAccess, tracker, auditPath);
        Tools = new AliRoslynCodingTools(resolver, tracker, auditPath);
    }

    internal AliDotNetProjectScaffolder ProjectScaffolder { get; }
    internal AliRoslynCodingTools Tools { get; }

    internal IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<DotNetCreateProjectResult>>)ProjectScaffolder.CreateAsync,
            AliCapabilityCatalog.DotNetCreateProjectName,
            "Create a new C# project scaffold in an empty approved folder. projectPath must be a virtual .csproj path such as Desktop/TicTacToe/TicTacToe.csproj; template must be wpf or console. After success, write the complete requested source before building."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynAnalysisResult>>)Tools.AnalyzeAsync,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            "Load an approved C# project with Roslyn and return semantic compiler diagnostics with exact source locations."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynFormatResult>>)Tools.FormatAsync,
            AliCapabilityCatalog.RoslynFormatProjectName,
            "Format every C# document in an approved project with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynSymbolResult>>)Tools.FindSymbolAsync,
            AliCapabilityCatalog.RoslynFindSymbolName,
            "Find C# type or member declarations semantically with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynCompletionResult>>)Tools.GetCompletionsAsync,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            "Return Roslyn IntelliSense completion candidates at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynSolutionOverviewResult>>)Tools.InspectSolutionAsync,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            "Inspect an approved .csproj, .sln, or .slnx and return its C# project graph, references, target frameworks, and document counts."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynDocumentResult>>)Tools.InspectDocumentAsync,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            "Return Roslyn's semantic outline, live diagnostics, and classified source spans for one C# document."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynPositionResult>>)Tools.InspectPositionAsync,
            AliCapabilityCatalog.RoslynInspectPositionName,
            "Return Roslyn hover text, definitions, and invocation signatures at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynReferenceResult>>)Tools.FindReferencesAsync,
            AliCapabilityCatalog.RoslynFindReferencesName,
            "Find every semantic reference to the C# symbol at a one-based source position across a project or solution."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, string, CancellationToken, Task<RoslynRenameResult>>)Tools.PreviewRenameAsync,
            AliCapabilityCatalog.RoslynPreviewRenameName,
            "Preview Roslyn's exact solution-wide semantic rename without writing files."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, string, CancellationToken, Task<RoslynRenameResult>>)Tools.ApplyRenameAsync,
            AliCapabilityCatalog.RoslynApplyRenameName,
            "Apply Roslyn's solution-wide semantic rename after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetBuildResult>>)Tools.BuildAsync,
            AliCapabilityCatalog.DotNetBuildName,
            "Restore and compile an approved C# project through Microsoft's in-process MSBuild API."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetRunResult>>)Tools.RunAsync,
            AliCapabilityCatalog.DotNetRunName,
            "Launch an already-built .NET application from an approved project after user approval.")
    ];
}
