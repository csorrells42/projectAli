using Microsoft.CodeAnalysis;

namespace Ali.Modules.Coding.RoslynActions;

internal enum AliRoslynDocumentKind
{
    Regular,
    Additional,
    AnalyzerConfig
}

internal enum AliRoslynDocumentChangeKind
{
    Add,
    Replace,
    Delete,
    Rename,
    RenameAndReplace
}

/// <summary>
/// Exact Roslyn document graph delta authenticated inside the protected action handle.
/// Source operation sequences bind every delta back to the durable source manifest.
/// </summary>
internal sealed record AliRoslynDocumentChange(
    AliRoslynDocumentChangeKind Kind,
    AliRoslynDocumentKind DocumentKind,
    string ProjectRelativePath,
    string? SourceRelativePath,
    string? DestinationRelativePath,
    string? CanonicalName,
    string[] CanonicalFolders,
    string? StagedName,
    string[] StagedFolders,
    SourceCodeKind SourceCodeKind,
    int[] SourceOperationSequences);
