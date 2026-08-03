using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.WorkstationFiles;
using Microsoft.Agents.AI;
using Microsoft.Win32.SafeHandles;

#pragma warning disable MAAI001 // Agent Framework file-memory storage is isolated behind this module.

namespace Ali.Modules.AgentWorkMemory;

internal enum AliWorkMemoryMutation
{
    Write,
    Replace,
    ReplaceLines,
    Delete
}

internal enum AliWorkMemoryPreparationCheckpoint
{
    ExactTargetCaptured,
    StagingEntryCopied,
    CanonicalSourceIdentityBound,
    StagingSourceIdentityBound,
    BeforeDomainPlanPersisted
}

internal enum AliWorkMemoryPublicationCheckpoint
{
    BeforeCanonicalToBackup,
    AfterCanonicalToBackup,
    BeforeStagingToCanonical,
    AfterStagingToCanonical,
    BeforeBackupParentCreate,
    AfterBackupParentCreate,
    AfterFinalStagingCheckBeforeRename,
    BeforeRecoveryQuarantine,
    BeforeRecoveryRestore,
    BeforeStagedFileWriteSwap,
    BeforeStagedFileDeleteDisposition,
    BeforeDurableCompletion,
    AfterClassificationSnapshot,
    AfterRootRenameBeforeReseal
}

internal sealed class AliWorkMemorySimulatedInterruptionException(
    AliWorkMemoryPublicationCheckpoint checkpoint) : IOException(
        $"Simulated work-memory interruption at {checkpoint}.")
{
    internal AliWorkMemoryPublicationCheckpoint Checkpoint { get; } = checkpoint;
}

internal sealed record AliWorkMemoryNamespaceBinding(
    string RelativePath,
    string PhysicalPath,
    string Identity);

internal sealed record AliWorkMemoryCapturedTarget(
    AgentWorkMemoryScope Scope,
    string FileName,
    string WorkspacePath,
    string MainFilePath,
    string DescriptionFileName,
    string DescriptionFilePath,
    AliFileTreeItemSnapshot WorkspaceBefore,
    AliFileTreeItemSnapshot MainFileBefore,
    AliFileTreeItemSnapshot DescriptionFileBefore,
    string? MainFileContent,
    TargetStateSnapshot TargetState);

internal sealed record AliWorkMemoryDomainPlan(
    string DomainId,
    AliWorkMemoryMutation Mutation,
    string ToolName,
    AgentWorkMemoryScope Scope,
    string FileName,
    string CanonicalWorkspacePath,
    string StagingWorkspacePath,
    string BackupWorkspacePath,
    AliFileTreeItemSnapshot WorkspaceBefore,
    AliFileTreeItemSnapshot WorkspaceAfter,
    AliFileTreeItemSnapshot MainFileBefore,
    AliFileTreeItemSnapshot MainFileAfter,
    string DescriptionFileName,
    AliFileTreeItemSnapshot DescriptionFileBefore,
    AliFileTreeItemSnapshot DescriptionFileAfter,
    AliFileTreeItemSnapshot MemoryIndexFileAfter,
    AliFileTreeItemSnapshot BackupBefore,
    AliFileTreeItemSnapshot BackupAfter,
    IReadOnlyList<AliWorkMemoryNamespaceBinding> CanonicalParentSpine,
    string? CanonicalWorkspaceIdentity,
    IReadOnlyList<AliWorkMemoryNamespaceBinding> StagingContainerSpine,
    string StagingWorkspaceIdentity,
    string BackupParentSeedPath,
    string BackupParentSeedIdentity,
    string EmptyBackupSeedPath,
    string EmptyBackupSeedIdentity,
    IReadOnlyList<AliWorkMemoryNamespaceBinding> DurableTransactionsSpine);

internal sealed record AliWorkMemoryExecutionBinding(
    int FormatVersion,
    string PlanId,
    string DomainId,
    string DomainDigest,
    string AuthorizationDigest,
    IReadOnlyList<AliWorkMemoryNamespaceBinding> BackupParentSpine,
    string BackupWorkspaceIdentity,
    string StagingWorkspaceIdentity,
    string? CanonicalWorkspaceIdentity);

internal static class AliExactWorkMemoryArguments
{
    private static readonly HashSet<string> ReservedDosDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM\u00B9",
            "COM\u00B2",
            "COM\u00B3",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT\u00B9",
            "LPT\u00B2",
            "LPT\u00B3"
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static string ReadFileName(string toolName, JsonElement arguments)
    {
        switch (toolName)
        {
            case AliCapabilityCatalog.WorkMemoryWriteName:
                RequireObject(arguments, ["fileName", "content"], ["description"]);
                break;
            case AliCapabilityCatalog.WorkMemoryDeleteName:
                RequireObject(arguments, ["fileName"], []);
                break;
            case AliCapabilityCatalog.WorkMemoryReplaceName:
                RequireObject(
                    arguments,
                    ["fileName", "oldString", "newString", "replaceAll"],
                    []);
                break;
            case AliCapabilityCatalog.WorkMemoryReplaceLinesName:
                RequireObject(arguments, ["fileName", "edits"], []);
                break;
            default:
                throw new InvalidDataException(
                    "The exact work-memory parser does not own this tool name.");
        }

        return RequireSafeFlatFileName(
            RequireString(arguments, "fileName", allowEmpty: false));
    }

    internal static string RequireSafeFlatFileName(
        string fileName,
        bool allowExactFrameworkReservedName = false)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var baseNameEnd = fileName.IndexOf('.');
        var baseName = (baseNameEnd < 0 ? fileName : fileName[..baseNameEnd])
            .TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal)
            || fileName.EndsWith('.')
            || fileName.IndexOfAny(invalidCharacters) >= 0
            || fileName.Any(character => character < ' '
                || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            || (!allowExactFrameworkReservedName
                && (string.Equals(fileName, MemoryIndexFileName, StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("_description.md", StringComparison.OrdinalIgnoreCase)))
            || ReservedDosDeviceNames.Contains(baseName)
            || Path.IsPathFullyQualified(fileName)
            || fileName is "." or "..")
        {
            throw new InvalidDataException(
                "The exact work-memory file name must be a Windows-safe flat, non-reserved name.");
        }
        return fileName;
    }

    private const string MemoryIndexFileName = "memories.md";

    internal static string ComputePostContent(
        string toolName,
        JsonElement arguments,
        string? currentContent)
    {
        return toolName switch
        {
            AliCapabilityCatalog.WorkMemoryWriteName =>
                RequireString(arguments, "content", allowEmpty: true),
            AliCapabilityCatalog.WorkMemoryReplaceName =>
                Replace(arguments, currentContent),
            AliCapabilityCatalog.WorkMemoryReplaceLinesName =>
                ReplaceLines(arguments, currentContent),
            _ => throw new InvalidDataException(
                "The exact work-memory mutation has no text postimage.")
        };
    }

    internal static string? ReadDescription(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("description", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } description)
        {
            throw new InvalidDataException(
                "The exact work-memory 'description' value is invalid.");
        }
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }

    internal static string DescriptionFileName(string fileName)
    {
        var extensionIndex = fileName.LastIndexOf('.');
        return extensionIndex > 0
            ? fileName[..extensionIndex] + "_description.md"
            : fileName + "_description.md";
    }

    private static string Replace(JsonElement arguments, string? currentContent)
    {
        if (currentContent is null)
        {
            throw new FileNotFoundException(
                "The exact work-memory replace target does not exist.");
        }
        var oldString = RequireString(arguments, "oldString", allowEmpty: false);
        var newString = RequireString(arguments, "newString", allowEmpty: true);
        var replaceAll = RequireBoolean(arguments, "replaceAll");
        var occurrences = CountOccurrences(currentContent, oldString);
        if (occurrences == 0)
        {
            throw new InvalidDataException(
                "The exact work-memory oldString was not found.");
        }
        if (!replaceAll && occurrences != 1)
        {
            throw new InvalidDataException(
                "The exact work-memory oldString is ambiguous unless replaceAll is true.");
        }
        if (replaceAll)
        {
            return currentContent.Replace(oldString, newString, StringComparison.Ordinal);
        }
        var index = currentContent.IndexOf(oldString, StringComparison.Ordinal);
        return string.Concat(
            currentContent.AsSpan(0, index),
            newString,
            currentContent.AsSpan(index + oldString.Length));
    }

    private static string ReplaceLines(JsonElement arguments, string? currentContent)
    {
        if (currentContent is null)
        {
            throw new FileNotFoundException(
                "The exact work-memory line-edit target does not exist.");
        }
        if (!arguments.TryGetProperty("edits", out var edits)
            || edits.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The exact work-memory edits argument must be an array.");
        }
        var replacements = new Dictionary<int, string>();
        foreach (var edit in edits.EnumerateArray())
        {
            RequireObject(edit, ["line_number", "new_line"], []);
            if (!edit.TryGetProperty("line_number", out var lineNumberElement)
                || lineNumberElement.ValueKind != JsonValueKind.Number
                || !lineNumberElement.TryGetInt32(out var lineNumber))
            {
                throw new InvalidDataException(
                    "Each exact work-memory line_number must be a 32-bit integer.");
            }
            if (!replacements.TryAdd(
                    lineNumber,
                    RequireString(edit, "new_line", allowEmpty: true)))
            {
                throw new InvalidDataException(
                    "An exact work-memory line is targeted more than once.");
            }
        }
        if (replacements.Count == 0)
        {
            throw new InvalidDataException(
                "At least one exact work-memory line edit is required.");
        }
        var lines = SplitLinesKeepEnds(currentContent);
        if (replacements.Keys.Any(line => line <= 0 || line > lines.Count))
        {
            throw new InvalidDataException(
                "An exact work-memory line edit is outside the 1-based file range.");
        }
        var builder = new StringBuilder(currentContent.Length);
        for (var index = 0; index < lines.Count; index++)
        {
            builder.Append(replacements.TryGetValue(index + 1, out var replacement)
                ? replacement
                : lines[index]);
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> SplitLinesKeepEnds(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] is not ('\r' or '\n'))
            {
                continue;
            }
            var end = index + 1;
            if (content[index] == '\r' && end < content.Length && content[end] == '\n')
            {
                end++;
                index++;
            }
            lines.Add(content[start..end]);
            start = end;
        }
        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }
        return lines;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string RequireString(
        JsonElement arguments,
        string propertyName,
        bool allowEmpty)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || (!allowEmpty && text.Length == 0))
        {
            throw new InvalidDataException(
                $"The exact work-memory '{propertyName}' value is invalid.");
        }
        return text;
    }

    private static bool RequireBoolean(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException(
                $"The exact work-memory '{propertyName}' value is missing.");
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"The exact work-memory '{propertyName}' value must be Boolean.")
        };
    }

    private static void RequireObject(
        JsonElement value,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The exact work-memory arguments must be an object.");
        }
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !present.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The exact work-memory arguments contain unsupported or duplicate property '{property.Name}'.");
            }
        }
        if (required.Any(property => !present.Contains(property)))
        {
            throw new InvalidDataException(
                "The exact work-memory arguments are missing a required property.");
        }
    }
}

/// <summary>
/// Binds a memory-file path to the literal final component that Windows opened. A DOS short-name
/// alias can otherwise pass lexical validation while resolving to a Framework-maintained file.
/// Work-memory files also reject multiply linked identities because the staged tree copy does not
/// preserve hard-link topology.
/// </summary>
internal static class AliWorkMemoryWindowsFileIdentity
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint FileNameNormalized = 0;
    private const int MaximumFinalPathCharacters = 64 * 1024;
    private const int MaximumTreeEntries = 100_000;
    private const int MaximumCleanupDepth = 256;

    internal static IReadOnlyList<AliWorkMemoryNamespaceBinding> CaptureDirectorySpine(
        string parentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var anchor = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(parent)
            ?? throw new InvalidDataException(
                "The work-memory namespace path has no volume root."));
        var handles = new List<SafeFileHandle>();
        var bindings = new List<AliWorkMemoryNamespaceBinding>();
        try
        {
            var current = anchor;
            var anchorHandle = OpenExistingDirectoryForSpine(current);
            handles.Add(anchorHandle);
            bindings.Add(new AliWorkMemoryNamespaceBinding(
                ".",
                current,
                CaptureIdentity(anchorHandle)));
            var relative = Path.GetRelativePath(anchor, parent);
            if (!string.Equals(relative, ".", StringComparison.Ordinal))
            {
                foreach (var component in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    RequireLeaf(component);
                    current = Path.Combine(current, component);
                    var handle = OpenDirectoryRelative(handles[^1], component);
                    handles.Add(handle);
                    RequireLiteralLeaf(handle, component);
                    bindings.Add(new AliWorkMemoryNamespaceBinding(
                        Path.GetRelativePath(anchor, current).Replace('\\', '/'),
                        current,
                        CaptureIdentity(handle)));
                }
            }
            return bindings;
        }
        finally
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    internal static AliWorkMemoryDirectorySpine OpenDirectorySpine(
        IReadOnlyList<AliWorkMemoryNamespaceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0
            || !string.Equals(bindings[0].RelativePath, ".", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact work-memory namespace spine has no authenticated volume anchor.");
        }

        var handles = new List<SafeFileHandle>();
        try
        {
            var anchor = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bindings[0].PhysicalPath));
            if (!string.Equals(
                    anchor,
                    Path.TrimEndingDirectorySeparator(Path.GetPathRoot(anchor) ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The exact work-memory namespace spine does not begin at its volume root.");
            }
            var anchorHandle = OpenExistingDirectoryForSpine(anchor);
            handles.Add(anchorHandle);
            RequireIdentity(anchorHandle, bindings[0].Identity);
            var current = anchor;
            for (var index = 1; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                var leaf = Path.GetFileName(binding.PhysicalPath);
                RequireLeaf(leaf);
                var expectedPath = Path.Combine(current, leaf);
                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.PhysicalPath)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        binding.RelativePath.Replace('\\', '/'),
                        Path.GetRelativePath(anchor, expectedPath).Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The exact work-memory namespace spine is not one contiguous relative chain.");
                }
                var handle = OpenDirectoryRelative(handles[^1], leaf);
                handles.Add(handle);
                RequireLiteralLeaf(handle, leaf);
                RequireIdentity(handle, binding.Identity);
                current = expectedPath;
            }
            SafeFileHandle parentHandle;
            if (bindings.Count == 1)
            {
                parentHandle = OpenExistingDirectory(
                    anchor,
                    FileListDirectory | FileAddFile | FileAddSubdirectory
                    | FileReadAttributes | Synchronize,
                    FileShare.ReadWrite);
                handles.Add(parentHandle);
                RequireIdentity(parentHandle, bindings[0].Identity);
            }
            else
            {
                var parentLeaf = Path.GetFileName(bindings[^1].PhysicalPath);
                parentHandle = OpenDirectoryRelative(
                    handles[^2],
                    parentLeaf,
                    FileListDirectory | FileAddFile | FileAddSubdirectory
                    | FileReadAttributes | Synchronize);
                handles.Add(parentHandle);
                RequireIdentity(parentHandle, bindings[^1].Identity);
            }
            return new AliWorkMemoryDirectorySpine(
                handles,
                bindings.ToArray(),
                parentHandle);
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
            throw;
        }
    }

    internal static AliWorkMemoryBoundDirectory CreateBoundChildDirectory(
        AliWorkMemoryDirectorySpine parent,
        string leaf)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return CreateBoundChildDirectory(parent.ParentHandle, parent.ParentPath, leaf);
    }

    internal static AliWorkMemoryBoundDirectory CreateBoundChildDirectory(
        AliWorkMemoryBoundDirectory parent,
        string leaf)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return CreateBoundChildDirectory(parent.Handle, parent.Path, leaf);
    }

    internal static AliWorkMemoryBoundDirectory OpenBoundChildDirectory(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string? expectedIdentity = null,
        bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return OpenBoundChildDirectory(
            parent.ParentHandle,
            parent.ParentPath,
            leaf,
            expectedIdentity,
            writable,
            FileShare.ReadWrite);
    }

    internal static AliWorkMemoryBoundDirectory OpenSealedBoundChildDirectory(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string? expectedIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return OpenBoundChildDirectory(
            parent.ParentHandle,
            parent.ParentPath,
            leaf,
            expectedIdentity,
            writable: false,
            share: FileShare.Read);
    }

    internal static AliWorkMemoryBoundDirectory? TryOpenBoundChildDirectory(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string? expectedIdentity = null)
    {
        try
        {
            return OpenBoundChildDirectory(parent, leaf, expectedIdentity);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static AliWorkMemoryBoundDirectory? TryOpenSealedBoundChildDirectory(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string? expectedIdentity = null)
    {
        try
        {
            return OpenSealedBoundChildDirectory(parent, leaf, expectedIdentity);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<AliWorkMemoryNamespaceBinding> ExtendSpine(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        RequireLeaf(leaf);
        using var child = OpenDirectoryRelative(
            parent.ParentHandle,
            leaf,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShare.ReadWrite | FileShare.Delete);
        RequireLiteralLeaf(child, leaf);
        RequireIdentity(child, expectedIdentity);
        var childPath = Path.Combine(parent.ParentPath, leaf);
        var result = parent.Bindings.ToList();
        var anchor = result[0].PhysicalPath;
        result.Add(new AliWorkMemoryNamespaceBinding(
            Path.GetRelativePath(anchor, childPath).Replace('\\', '/'),
            childPath,
            expectedIdentity));
        return result;
    }

    internal static string CaptureExistingDirectoryIdentity(string path)
    {
        using var handle = OpenExistingDirectory(
            path,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete);
        return CaptureIdentity(handle);
    }

    internal static bool PathMatchesDirectoryIdentity(string path, string expectedIdentity)
    {
        try
        {
            return string.Equals(
                CaptureExistingDirectoryIdentity(path),
                expectedIdentity,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static void RenameNoReplace(
        AliWorkMemoryBoundDirectory source,
        AliWorkMemoryDirectorySpine destinationParent,
        string destinationLeaf)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationParent);
        RenameHandleNoReplace(
            source.Handle,
            source.Identity,
            destinationParent.ParentHandle,
            destinationParent.ParentIdentity,
            destinationLeaf);
    }

    internal static AliFileTreeItemSnapshot CaptureBoundSnapshot(
        AliWorkMemoryBoundDirectory directory,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!PathMatchesDirectoryIdentity(currentPath, directory.Identity))
        {
            throw new IOException(
                "The exact work-memory path no longer names its held directory.");
        }
        var snapshot = AliFileTreeSnapshotter.CaptureStable(currentPath);
        if (!PathMatchesDirectoryIdentity(currentPath, directory.Identity))
        {
            throw new IOException(
                "The exact work-memory directory identity changed during recapture.");
        }
        return snapshot;
    }

    internal static AliWorkMemoryTreeClosure CaptureBoundTreeClosure(
        AliWorkMemoryBoundDirectory directory,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var count = 0;
        var children = CaptureClosureChildren(
            directory.Handle,
            currentPath,
            directory.Identity,
            ref count,
            depth: 0);
        var closure = new AliWorkMemoryTreeClosure(
            directory.Handle,
            directory.Identity,
            children);
        try
        {
            closure.InitialSnapshot = CaptureBoundSnapshot(closure, currentPath);
            return closure;
        }
        catch
        {
            closure.Dispose();
            throw;
        }
    }

    internal static AliFileTreeItemSnapshot CaptureBoundSnapshot(
        AliWorkMemoryTreeClosure closure,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(closure);
        RequireTreeClosureUnchanged(closure, currentPath);
        var snapshot = AliFileTreeSnapshotter.CaptureStable(currentPath);
        RequireTreeClosureUnchanged(closure, currentPath);
        return snapshot;
    }

    internal static void RequireTreeClosureUnchanged(
        AliWorkMemoryTreeClosure closure,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(closure);
        if (!PathMatchesDirectoryIdentity(currentPath, closure.RootIdentity))
        {
            throw new IOException(
                "The exact sealed work-memory root no longer names its held directory.");
        }
        RequireClosureChildrenUnchanged(
            closure.RootHandle,
            currentPath,
            closure.RootIdentity,
            closure.Children);
        if (!PathMatchesDirectoryIdentity(currentPath, closure.RootIdentity))
        {
            throw new IOException(
                "The exact sealed work-memory root changed during verification.");
        }
    }

    internal static void PrepareTreeClosureForRootRename(
        AliWorkMemoryTreeClosure closure,
        string currentPath,
        AliFileTreeItemSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentNullException.ThrowIfNull(expected);
        if (CaptureBoundSnapshot(closure, currentPath) != expected)
        {
            throw new IOException(
                "The exact sealed work-memory tree changed before its root rename.");
        }
        closure.ReleaseChildrenForRootRename();
    }

    internal static void ResealTreeClosureAfterRootRename(
        AliWorkMemoryTreeClosure closure,
        string currentPath,
        AliFileTreeItemSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentNullException.ThrowIfNull(expected);
        if (!closure.RootRenamePrepared)
        {
            throw new InvalidOperationException(
                "The exact work-memory tree was not prepared for a root rename.");
        }
        if (!PathMatchesDirectoryIdentity(currentPath, closure.RootIdentity))
        {
            throw new IOException(
                "The renamed work-memory root path does not resolve to its held directory identity.");
        }

        var count = 0;
        var children = CaptureClosureChildren(
            closure.RootHandle,
            currentPath,
            closure.RootIdentity,
            ref count,
            depth: 0);
        closure.ReplaceChildrenAfterRootRename(children);
        try
        {
            if (CaptureBoundSnapshot(closure, currentPath) != expected)
            {
                throw new IOException(
                    "The exact work-memory tree changed while its renamed root was resealed.");
            }
            closure.CompleteRootRename();
        }
        catch
        {
            closure.ReleaseChildrenForFailedReseal();
            throw;
        }
    }

    private static List<AliWorkMemoryTreeClosureNode> CaptureClosureChildren(
        SafeFileHandle parentHandle,
        string parentPath,
        string parentIdentity,
        ref int count,
        int depth)
    {
        if (depth > MaximumCleanupDepth)
        {
            throw new IOException(
                $"The exact work-memory tree closure cannot exceed {MaximumCleanupDepth} directory levels.");
        }
        var names = EnumerateBoundLeafNames(parentPath, parentIdentity);
        var children = new List<AliWorkMemoryTreeClosureNode>(names.Length);
        try
        {
            foreach (var name in names)
            {
                if (++count > MaximumTreeEntries)
                {
                    throw new IOException(
                        $"The exact work-memory tree closure cannot exceed {MaximumTreeEntries} entries.");
                }
                var entry = OpenBoundChildEntry(
                    parentHandle,
                    parentPath,
                    name,
                    deleteAccess: false);
                try
                {
                    var descendants = entry.IsDirectory
                        ? CaptureClosureChildren(
                            entry.Handle,
                            entry.Path,
                            entry.Identity,
                            ref count,
                            checked(depth + 1))
                        : new List<AliWorkMemoryTreeClosureNode>();
                    children.Add(new AliWorkMemoryTreeClosureNode(
                        name,
                        entry,
                        descendants));
                }
                catch
                {
                    entry.Dispose();
                    throw;
                }
            }
            RequireSameBoundLeafNames(parentPath, parentIdentity, names);
            return children;
        }
        catch
        {
            DisposeClosureNodes(children);
            throw;
        }
    }

    private static void RequireClosureChildrenUnchanged(
        SafeFileHandle parentHandle,
        string parentPath,
        string parentIdentity,
        IReadOnlyList<AliWorkMemoryTreeClosureNode> children)
    {
        RequireSameBoundLeafNames(
            parentPath,
            parentIdentity,
            children.Select(child => child.Name).ToArray());
        foreach (var child in children)
        {
            using var current = OpenBoundChildEntry(
                parentHandle,
                parentPath,
                child.Name,
                deleteAccess: false);
            if (!string.Equals(current.Identity, child.Entry.Identity, StringComparison.Ordinal)
                || current.IsDirectory != child.Entry.IsDirectory)
            {
                throw new IOException(
                    "An exact sealed work-memory child identity changed after capture.");
            }
            if (child.Entry.IsDirectory)
            {
                RequireClosureChildrenUnchanged(
                    child.Entry.Handle,
                    Path.Combine(parentPath, child.Name),
                    child.Entry.Identity,
                    child.Children);
            }
            else
            {
                RequireSingleLink(child.Entry.Handle);
            }
        }
    }

    private static void DisposeClosureNodes(
        IEnumerable<AliWorkMemoryTreeClosureNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.Dispose();
        }
    }

    internal static void DeleteEmptyBoundDirectory(AliWorkMemoryBoundDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var information = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                directory.Handle,
                FileDispositionInfo,
                ref information,
                1))
        {
            ThrowIo(
                "The exact empty work-memory directory could not be deleted by its held handle.",
                Marshal.GetLastWin32Error());
        }
    }

    internal static AliWorkMemoryBoundEntry OpenBoundChildEntry(
        AliWorkMemoryBoundDirectory parent,
        string leaf,
        bool deleteAccess)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return OpenBoundChildEntry(parent.Handle, parent.Path, leaf, deleteAccess);
    }

    internal static AliWorkMemoryBoundEntry OpenBoundChildEntry(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        bool deleteAccess)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return OpenBoundChildEntry(
            parent.ParentHandle,
            parent.ParentPath,
            leaf,
            deleteAccess);
    }

    internal static AliWorkMemoryBoundEntry OpenBoundChildEntry(
        AliWorkMemoryBoundEntry parent,
        string leaf,
        bool deleteAccess)
    {
        ArgumentNullException.ThrowIfNull(parent);
        parent.RequireDirectory();
        return OpenBoundChildEntry(parent.Handle, parent.Path, leaf, deleteAccess);
    }

    internal static AliWorkMemoryBoundEntry? TryOpenBoundChildEntry(
        AliWorkMemoryBoundDirectory parent,
        string leaf,
        bool deleteAccess)
    {
        try
        {
            return OpenBoundChildEntry(parent, leaf, deleteAccess);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static AliWorkMemoryBoundEntry? TryOpenBoundChildEntry(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        bool deleteAccess)
    {
        try
        {
            return OpenBoundChildEntry(parent, leaf, deleteAccess);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static string? ReadOptionalBoundText(
        AliWorkMemoryBoundDirectory parent,
        string leaf)
    {
        using var entry = TryOpenBoundChildEntry(parent, leaf, deleteAccess: false);
        if (entry is null)
        {
            return null;
        }
        entry.RequireFile();
        var length = RandomAccess.GetLength(entry.Handle);
        if (length > int.MaxValue)
        {
            throw new IOException("The exact work-memory text file is too large to read.");
        }
        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = RandomAccess.Read(entry.Handle, bytes.AsSpan(offset), offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The exact held work-memory text file ended during its read.");
                }
                offset += read;
            }
            using var memory = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                memory,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);
            return reader.ReadToEnd();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void WriteBoundText(
        AliWorkMemoryBoundDirectory parent,
        string leaf,
        string content,
        Action beforeSwap)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(beforeSwap);
        RequireLeaf(leaf);
        var temporaryLeaf = "ali-work-memory-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var temporary = CreateBoundChildFile(parent, temporaryLeaf);
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = Encoding.UTF8.GetBytes(content);
        var renamed = false;
        try
        {
            RandomAccess.Write(temporary.Handle, preamble, 0);
            RandomAccess.Write(temporary.Handle, bytes, preamble.Length);
            RandomAccess.FlushToDisk(temporary.Handle);
            if (CaptureBoundFileSnapshot(temporary)
                != AliAgentWorkMemoryExecutionCoordinator.SnapshotForText(content))
            {
                throw new IOException(
                    "The exact work-memory temporary write did not match its postimage.");
            }
            var existing = TryOpenBoundChildEntry(parent, leaf, deleteAccess: true);
            try
            {
                existing?.RequireFile();
                if (existing is not null)
                {
                    DeleteBoundEntry(existing);
                    existing.Dispose();
                    existing = null;
                }
                beforeSwap();
                RenameHandleNoReplace(
                    temporary.Handle,
                    temporary.Identity,
                    parent.Handle,
                    parent.Identity,
                    leaf);
                renamed = true;
                using var published = OpenBoundChildEntry(
                    parent.Handle,
                    parent.Path,
                    leaf,
                    deleteAccess: false,
                    share: FileShare.ReadWrite | FileShare.Delete);
                published.RequireFile();
                if (!string.Equals(
                        published.Identity,
                        temporary.Identity,
                        StringComparison.Ordinal)
                    || CaptureBoundFileSnapshot(published)
                    != AliAgentWorkMemoryExecutionCoordinator.SnapshotForText(content))
                {
                    throw new IOException(
                        "The exact staged work-memory write could not be recaptured after its handle rename.");
                }
            }
            finally
            {
                existing?.Dispose();
            }
        }
        catch
        {
            if (!renamed)
            {
                try
                {
                    DeleteBoundEntry(temporary);
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                    // The exact temporary remains bounded by staging admission if cleanup races.
                }
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preamble);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static bool DeleteBoundFile(
        AliWorkMemoryBoundDirectory parent,
        string leaf,
        Action beforeDisposition)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(beforeDisposition);
        using var entry = TryOpenBoundChildEntry(parent, leaf, deleteAccess: true);
        if (entry is null)
        {
            return false;
        }
        entry.RequireFile();
        beforeDisposition();
        DeleteBoundEntry(entry);
        entry.Dispose();
        using var replacement = TryOpenBoundChildEntry(parent, leaf, deleteAccess: false);
        if (replacement is not null)
        {
            throw new IOException(
                "The exact staged work-memory delete raced with an unrecognized replacement.");
        }
        return true;
    }

    internal static AliFileTreeItemSnapshot CaptureBoundFileSnapshot(
        AliWorkMemoryBoundEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.RequireFile();
        var length = RandomAccess.GetLength(entry.Handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                var read = RandomAccess.Read(
                    entry.Handle,
                    buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)),
                    offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The exact held work-memory file ended during recapture.");
                }
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            if (RandomAccess.GetLength(entry.Handle) != length)
            {
                throw new IOException(
                    "The exact held work-memory file length changed during recapture.");
            }
            return new AliFileTreeItemSnapshot(
                "file",
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void DeleteBoundEntry(AliWorkMemoryBoundEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var information = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                entry.Handle,
                FileDispositionInfo,
                ref information,
                1))
        {
            ThrowIo(
                "The exact work-memory entry could not be deleted by its held handle.",
                Marshal.GetLastWin32Error());
        }
    }

    internal static IReadOnlyList<FileStoreEntry> ListBoundChildren(
        AliWorkMemoryBoundDirectory parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var names = EnumerateBoundLeafNames(parent.Path, parent.Identity);
        var results = new List<FileStoreEntry>(names.Length);
        foreach (var name in names)
        {
            using var entry = OpenBoundChildEntry(parent, name, deleteAccess: false);
            results.Add(new FileStoreEntry(
                name,
                entry.IsDirectory ? FileStoreEntry.Directory : FileStoreEntry.File));
        }
        RequireSameBoundLeafNames(parent.Path, parent.Identity, names);
        return results;
    }

    internal static void DeleteBoundDirectoryTree(AliWorkMemoryBoundDirectory root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var count = 0;
        var children = CaptureCleanupChildren(
            root.Handle,
            root.Path,
            root.Identity,
            ref count,
            depth: 0);
        try
        {
            RequireCleanupTreeUnchanged(root.Path, root.Identity, children);
            foreach (var child in children)
            {
                DeleteCleanupNode(child);
            }
            DeleteEmptyBoundDirectory(root);
        }
        finally
        {
            DisposeCleanupNodes(children);
        }
    }

    private static List<AliWorkMemoryCleanupNode> CaptureCleanupChildren(
        SafeFileHandle parentHandle,
        string parentPath,
        string parentIdentity,
        ref int count,
        int depth)
    {
        if (depth > MaximumCleanupDepth)
        {
            throw new IOException(
                $"The exact work-memory cleanup cannot exceed {MaximumCleanupDepth} directory levels.");
        }
        var names = EnumerateBoundLeafNames(parentPath, parentIdentity);
        var children = new List<AliWorkMemoryCleanupNode>(names.Length);
        try
        {
            foreach (var name in names)
            {
                if (++count > MaximumTreeEntries)
                {
                    throw new IOException(
                        $"The exact work-memory cleanup cannot exceed {MaximumTreeEntries} entries.");
                }
                var entry = OpenBoundChildEntry(
                    parentHandle,
                    parentPath,
                    name,
                    deleteAccess: true);
                try
                {
                    var descendants = entry.IsDirectory
                        ? CaptureCleanupChildren(
                            entry.Handle,
                            entry.Path,
                            entry.Identity,
                            ref count,
                            checked(depth + 1))
                        : new List<AliWorkMemoryCleanupNode>();
                    children.Add(new AliWorkMemoryCleanupNode(entry, descendants));
                }
                catch
                {
                    entry.Dispose();
                    throw;
                }
            }
            RequireSameBoundLeafNames(parentPath, parentIdentity, names);
            return children;
        }
        catch
        {
            DisposeCleanupNodes(children);
            throw;
        }
    }

    private static void RequireCleanupTreeUnchanged(
        string parentPath,
        string parentIdentity,
        IReadOnlyList<AliWorkMemoryCleanupNode> children)
    {
        RequireSameBoundLeafNames(
            parentPath,
            parentIdentity,
            children.Select(child => Path.GetFileName(child.Entry.Path)
                ?? throw new InvalidDataException(
                    "The exact work-memory cleanup entry has no leaf name."))
                .ToArray());
        foreach (var child in children)
        {
            if (child.Entry.IsDirectory)
            {
                RequireCleanupTreeUnchanged(
                    child.Entry.Path,
                    child.Entry.Identity,
                    child.Children);
            }
        }
    }

    private static void DeleteCleanupNode(AliWorkMemoryCleanupNode node)
    {
        foreach (var child in node.Children)
        {
            DeleteCleanupNode(child);
        }
        DeleteBoundEntry(node.Entry);
        node.Entry.Dispose();
    }

    private static void DisposeCleanupNodes(IEnumerable<AliWorkMemoryCleanupNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.Dispose();
        }
    }

    private static string[] EnumerateBoundLeafNames(string path, string identity)
    {
        if (!PathMatchesDirectoryIdentity(path, identity))
        {
            throw new IOException(
                "The exact work-memory cleanup path no longer names its held directory.");
        }
        var names = Directory.EnumerateFileSystemEntries(path)
            .Select(entry => Path.GetFileName(entry)
                ?? throw new InvalidDataException(
                    "The exact work-memory directory enumeration has no leaf name."))
            .ToArray();
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw new InvalidDataException(
                "The exact work-memory directory enumeration contains an ambiguous leaf name.");
        }
        foreach (var name in names)
        {
            RequireLeaf(name!);
        }
        if (!PathMatchesDirectoryIdentity(path, identity))
        {
            throw new IOException(
                "The exact work-memory directory identity changed during enumeration.");
        }
        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RequireSameBoundLeafNames(
        string path,
        string identity,
        IReadOnlyList<string> expected)
    {
        var actual = EnumerateBoundLeafNames(path, identity);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new IOException(
                "The exact work-memory directory changed while its cleanup manifest was held.");
        }
    }

    private sealed class AliWorkMemoryCleanupNode(
        AliWorkMemoryBoundEntry entry,
        IReadOnlyList<AliWorkMemoryCleanupNode> children) : IDisposable
    {
        internal AliWorkMemoryBoundEntry Entry { get; } = entry;

        internal IReadOnlyList<AliWorkMemoryCleanupNode> Children { get; } = children;

        public void Dispose()
        {
            DisposeCleanupNodes(Children);
            Entry.Dispose();
        }
    }

    internal static bool RequireOptionalLiteralSingleLinkFile(
        string path,
        string expectedFileName,
        string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        FileStream stream;
        try
        {
            stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                writeThrough: false,
                invalidMessage);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException exception) when (
            exception.InnerException is Win32Exception
            {
                NativeErrorCode: ErrorFileNotFound
            })
        {
            // OpenRegularFile preserves its no-follow handle validation but wraps a failed
            // CreateFileW call in IOException. Only a missing final leaf is optional. A missing
            // parent (ERROR_PATH_NOT_FOUND) or any other replacement/open failure must fail closed.
            return false;
        }

        using (stream)
        {
            RequireOpenedLiteralSingleLinkFile(
                stream,
                path,
                expectedFileName,
                invalidMessage);
        }
        return true;
    }

    internal static void RequireLiteralSingleLinkTree(string root, string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(root, invalidMessage);
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumTreeEntries)
                {
                    throw new IOException(
                        $"The work-memory identity boundary cannot exceed {MaximumTreeEntries} entries.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(invalidMessage);
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                        entry,
                        invalidMessage);
                    pending.Push(entry);
                    continue;
                }

                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    invalidMessage);
                RequireOpenedLiteralSingleLinkFile(
                    stream,
                    entry,
                    Path.GetFileName(entry),
                    invalidMessage);
            }
        }
    }

    internal static void RequireOpenedLiteralSingleLinkFile(
        FileStream stream,
        string path,
        string expectedFileName,
        string invalidMessage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        _ = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            invalidMessage);
        var finalPath = ReadFinalPath(stream.SafeFileHandle, invalidMessage);
        var trimmed = finalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = trimmed.LastIndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var finalComponent = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        if (!string.Equals(finalComponent, expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                invalidMessage + " The requested name resolves through a non-literal Windows file alias.");
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle, string invalidMessage)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                FileNameNormalized);
            if (length == 0)
            {
                throw new IOException(
                    invalidMessage,
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < buffer.Capacity)
            {
                return buffer.ToString();
            }

            capacity = checked((int)length + 1);
        }

        throw new InvalidDataException(invalidMessage + " The final Windows path is too long.");
    }

    private static AliWorkMemoryBoundDirectory CreateBoundChildDirectory(
        SafeFileHandle parent,
        string parentPath,
        string leaf)
    {
        var path = Path.Combine(parentPath, leaf);
        var handle = OpenOrCreateDirectoryRelative(parent, leaf, FileCreate);
        try
        {
            RequireLiteralLeaf(handle, leaf);
            var identity = CaptureIdentity(handle);
            if (!PathMatchesDirectoryIdentity(path, identity))
            {
                throw new IOException(
                    "The atomically created work-memory directory is not bound at its authenticated path.");
            }
            return new AliWorkMemoryBoundDirectory(handle, path, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static AliWorkMemoryBoundEntry OpenBoundChildEntry(
        SafeFileHandle parent,
        string parentPath,
        string leaf,
        bool deleteAccess) => OpenBoundChildEntry(
        parent,
        parentPath,
        leaf,
        deleteAccess,
        FileShare.Read);

    private static AliWorkMemoryBoundEntry OpenBoundChildEntry(
        SafeFileHandle parent,
        string parentPath,
        string leaf,
        bool deleteAccess,
        FileShare share)
    {
        RequireLeaf(leaf);
        var handle = OpenOrCreateRelativeEntry(
            parent,
            leaf,
            FileOpen,
            (deleteAccess ? DeleteAccess : 0)
            | FileListDirectory | FileReadAttributes | Synchronize,
            directoryOnly: false,
            fileOnly: false,
            share);
        try
        {
            RequireLiteralLeaf(handle, leaf);
            var attributes = ValidateEntryHandle(handle);
            var kind = (attributes & FileAttributes.Directory) != 0
                ? "directory"
                : "file";
            if (string.Equals(kind, "file", StringComparison.Ordinal))
            {
                RequireSingleLink(handle);
            }
            return new AliWorkMemoryBoundEntry(
                handle,
                Path.Combine(parentPath, leaf),
                kind,
                CaptureEntryIdentity(handle, kind));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static AliWorkMemoryBoundEntry CreateBoundChildFile(
        AliWorkMemoryBoundDirectory parent,
        string leaf)
    {
        RequireLeaf(leaf);
        var handle = OpenOrCreateRelativeEntry(
            parent.Handle,
            leaf,
            FileCreate,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | Synchronize,
            directoryOnly: false,
            fileOnly: true,
            share: FileShare.Read);
        try
        {
            RequireLiteralLeaf(handle, leaf);
            _ = ValidateEntryHandle(handle);
            RequireSingleLink(handle);
            return new AliWorkMemoryBoundEntry(
                handle,
                Path.Combine(parent.Path, leaf),
                "file",
                CaptureEntryIdentity(handle, "file"));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenOrCreateRelativeEntry(
        SafeFileHandle parent,
        string leaf,
        uint createDisposition,
        uint desiredAccess,
        bool directoryOnly,
        bool fileOnly,
        FileShare share)
    {
        RequireLeaf(leaf);
        var nameBytes = Encoding.Unicode.GetBytes(leaf);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new PathTooLongException(
                "The exact work-memory entry leaf exceeds the Windows native name limit.");
        }
        var nameBuffer = Marshal.StringToHGlobalUni(leaf);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)nameBytes.Length),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var typeOption = directoryOnly
                ? FileDirectoryFile
                : fileOnly
                    ? FileNonDirectoryFile
                    : 0;
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                createDisposition == FileCreate ? FileAttributeNormal : 0,
                (uint)share,
                createDisposition,
                typeOption | FileOpenReparsePoint | FileSynchronousIoNonAlert,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                ThrowIo(
                    createDisposition == FileCreate
                        ? "The exact work-memory entry could not be created beneath its held parent."
                        : "The exact work-memory entry could not be opened relative to its held parent.",
                    checked((int)RtlNtStatusToDosError(status)));
            }
            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new IOException(
                    "Windows returned no valid handle for the exact relative work-memory entry.");
            }
            _ = ValidateEntryHandle(handle);
            return handle;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RenameHandleNoReplace(
        SafeFileHandle source,
        string sourceIdentity,
        SafeFileHandle destinationParent,
        string destinationParentIdentity,
        string destinationLeaf)
    {
        RequireLeaf(destinationLeaf);
        if (!string.Equals(
                VolumeIdentity(sourceIdentity),
                VolumeIdentity(destinationParentIdentity),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The exact work-memory handle rename cannot cross filesystem volumes.");
        }
        var fileNameBytes = Encoding.Unicode.GetBytes(destinationLeaf);
        var fileNameOffset = Marshal.OffsetOf<FileRenameInformationHeader>(
                nameof(FileRenameInformationHeader.FileNameLength))
            .ToInt32() + sizeof(uint);
        var size = checked(
            Marshal.SizeOf<FileRenameInformationHeader>() + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(size);
        var addedRef = false;
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            // Replacement is deliberately forbidden. The variable-length native rename buffer
            // binds the exact simple leaf to the held destination directory handle.
            destinationParent.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(
                new FileRenameInformationHeader
                {
                    ReplaceIfExists = 0,
                    RootDirectory = destinationParent.DangerousGetHandle(),
                    FileNameLength = checked((uint)fileNameBytes.Length)
                },
                buffer,
                fDeleteOld: false);
            Marshal.Copy(
                fileNameBytes,
                0,
                IntPtr.Add(buffer, fileNameOffset),
                fileNameBytes.Length);
            var status = NtSetInformationFile(
                    source,
                    out _,
                    buffer,
                    checked((uint)size),
                    FileRenameInformation);
            if (status < 0)
            {
                ThrowIo(
                    "The exact work-memory entry could not be renamed through its held destination parent.",
                    checked((int)RtlNtStatusToDosError(status)));
            }
        }
        finally
        {
            if (addedRef)
            {
                destinationParent.DangerousRelease();
            }
            CryptographicOperations.ZeroMemory(fileNameBytes);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static AliWorkMemoryBoundDirectory OpenBoundChildDirectory(
        SafeFileHandle parent,
        string parentPath,
        string leaf,
        string? expectedIdentity,
        bool writable,
        FileShare share)
    {
        var handle = OpenDirectoryRelative(
            parent,
            leaf,
            DeleteAccess | FileListDirectory | FileReadAttributes | Synchronize
            | (writable ? FileAddFile | FileAddSubdirectory : 0),
            share);
        try
        {
            RequireLiteralLeaf(handle, leaf);
            var identity = CaptureIdentity(handle);
            if (expectedIdentity is not null
                && !string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
            {
                throw new IOException(
                    "The exact relative work-memory directory identity changed after authorization.");
            }
            return new AliWorkMemoryBoundDirectory(
                handle,
                Path.Combine(parentPath, leaf),
                identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenExistingDirectoryForSpine(string path) =>
        OpenExistingDirectory(
            path,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShare.ReadWrite);

    private static SafeFileHandle OpenExistingDirectory(
        string path,
        uint desiredAccess,
        FileShare share)
    {
        var handle = CreateFileW(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(Path.GetFullPath(path)),
            desiredAccess,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            ThrowIo(
                "The exact work-memory directory could not be opened without following links.",
                error,
                path);
        }
        ValidateDirectoryHandle(handle);
        return handle;
    }

    private static SafeFileHandle OpenDirectoryRelative(
        SafeFileHandle parent,
        string leaf) => OpenDirectoryRelative(
        parent,
        leaf,
        FileListDirectory | FileReadAttributes | Synchronize);

    private static SafeFileHandle OpenDirectoryRelative(
        SafeFileHandle parent,
        string leaf,
        uint desiredAccess) => OpenOrCreateDirectoryRelative(
        parent,
        leaf,
        FileOpen,
        desiredAccess);

    private static SafeFileHandle OpenDirectoryRelative(
        SafeFileHandle parent,
        string leaf,
        uint desiredAccess,
        FileShare share) => OpenOrCreateDirectoryRelative(
        parent,
        leaf,
        FileOpen,
        desiredAccess,
        share);

    private static SafeFileHandle OpenOrCreateDirectoryRelative(
        SafeFileHandle parent,
        string leaf,
        uint createDisposition,
        uint? requestedAccess = null,
        FileShare share = FileShare.ReadWrite)
    {
        RequireLeaf(leaf);
        var nameBytes = Encoding.Unicode.GetBytes(leaf);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new PathTooLongException(
                "The exact work-memory directory leaf exceeds the Windows native name limit.");
        }
        var nameBuffer = Marshal.StringToHGlobalUni(leaf);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)nameBytes.Length),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = NtCreateFile(
                out var handle,
                requestedAccess
                ?? (DeleteAccess | FileListDirectory | FileAddFile | FileAddSubdirectory
                    | FileReadAttributes | Synchronize),
                ref attributes,
                out _,
                IntPtr.Zero,
                createDisposition == FileCreate ? FileAttributeNormal : 0,
                (uint)share,
                createDisposition,
                FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                ThrowIo(
                    createDisposition == FileCreate
                        ? "The exact work-memory directory could not be created atomically beneath its held parent."
                        : "The exact work-memory directory could not be opened relative to its held parent.",
                    checked((int)RtlNtStatusToDosError(status)));
            }
            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new IOException(
                    "Windows returned no valid handle for the exact relative work-memory directory.");
            }
            ValidateDirectoryHandle(handle);
            return handle;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static string CaptureIdentity(SafeFileHandle handle)
    {
        return CaptureEntryIdentity(handle, "directory");
    }

    private static string CaptureEntryIdentity(SafeFileHandle handle, string expectedKind)
    {
        var attributes = ValidateEntryHandle(handle);
        var actualKind = (attributes & FileAttributes.Directory) != 0
            ? "directory"
            : "file";
        if (!string.Equals(actualKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact work-memory namespace object changed kind during identity capture.");
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            ThrowIo(
                "The exact work-memory entry identity could not be captured.",
                Marshal.GetLastWin32Error());
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{actualKind}:{information.VolumeSerialNumber:x16}:{information.FileId.High:x16}{information.FileId.Low:x16}");
    }

    private static void RequireIdentity(SafeFileHandle handle, string expectedIdentity)
    {
        if (!string.Equals(
                CaptureIdentity(handle),
                expectedIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The exact work-memory namespace identity changed after preparation.");
        }
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle)
    {
        var attributes = ValidateEntryHandle(handle);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The exact work-memory namespace object is not a local disk directory.");
        }
    }

    private static FileAttributes ValidateEntryHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk)
        {
            throw new InvalidDataException(
                "The exact work-memory namespace object is not a local disk entry.");
        }
        var attributes = File.GetAttributes(handle);
        if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The exact work-memory namespace object changed kind or contains a reparse point.");
        }
        return attributes;
    }

    private static void RequireSingleLink(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            ThrowIo(
                "The exact work-memory file link count could not be captured.",
                Marshal.GetLastWin32Error());
        }
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "The exact work-memory file has a hard-link alias (is multiply linked) and cannot be mutated safely.");
        }
    }

    private static void RequireLiteralLeaf(SafeFileHandle handle, string expectedLeaf)
    {
        var finalPath = ReadFinalPath(
            handle,
            "The exact work-memory directory final path could not be authenticated.");
        var trimmed = finalPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var separator = trimmed.LastIndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var actualLeaf = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        if (!string.Equals(actualLeaf, expectedLeaf, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact work-memory namespace component resolves through a non-literal Windows alias.");
        }
    }

    private static string VolumeIdentity(string identity)
    {
        var parts = identity.Split(':');
        if (parts.Length != 3
            || (!string.Equals(parts[0], "directory", StringComparison.Ordinal)
                && !string.Equals(parts[0], "file", StringComparison.Ordinal))
            || parts[1].Length != 16
            || parts[2].Length != 32)
        {
            throw new InvalidDataException(
                "The exact work-memory filesystem identity has an invalid format.");
        }
        return parts[1];
    }

    private static void RequireLeaf(string leaf)
    {
        _ = AliExactWorkMemoryArguments.RequireSafeFlatFileName(
            leaf,
            allowExactFrameworkReservedName: true);
        if (leaf.Any(character => character < ' ')
            || leaf.Contains(':')
            || leaf.EndsWith(' ')
            || leaf.EndsWith('.'))
        {
            throw new InvalidDataException(
                "The exact work-memory handle-rooted operation requires one ordinary Windows leaf name.");
        }
    }

    private static void ThrowIo(string message, int error, string? path = null)
    {
        if (error == ErrorFileNotFound)
        {
            throw new FileNotFoundException(message, path);
        }
        if (error == ErrorPathNotFound)
        {
            throw new DirectoryNotFoundException(message);
        }
        throw new IOException(message, new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        internal uint ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathCharacters,
        uint flags);
}

internal sealed class AliWorkMemoryDirectorySpine(
    IReadOnlyList<SafeFileHandle> handles,
    IReadOnlyList<AliWorkMemoryNamespaceBinding> bindings,
    SafeFileHandle parentHandle) : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _handles = handles;

    internal IReadOnlyList<AliWorkMemoryNamespaceBinding> Bindings { get; } = bindings;

    internal SafeFileHandle ParentHandle { get; } = parentHandle;

    internal string ParentPath => Bindings[^1].PhysicalPath;

    internal string ParentIdentity => Bindings[^1].Identity;

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }
    }
}

internal sealed class AliWorkMemoryBoundDirectory(
    SafeFileHandle handle,
    string path,
    string identity) : IDisposable
{
    internal SafeFileHandle Handle { get; } = handle;

    internal string Path { get; } = path;

    internal string Identity { get; } = identity;

    public void Dispose() => Handle.Dispose();
}

internal sealed class AliWorkMemoryBoundEntry(
    SafeFileHandle handle,
    string path,
    string kind,
    string identity) : IDisposable
{
    internal SafeFileHandle Handle { get; } = handle;

    internal string Path { get; } = path;

    internal string Kind { get; } = kind;

    internal string Identity { get; } = identity;

    internal bool IsDirectory => string.Equals(Kind, "directory", StringComparison.Ordinal);

    internal void RequireDirectory()
    {
        if (!IsDirectory)
        {
            throw new InvalidDataException(
                "The exact work-memory entry is not the expected directory.");
        }
    }

    internal void RequireFile()
    {
        if (IsDirectory)
        {
            throw new InvalidDataException(
                "The exact work-memory entry is not the expected regular file.");
        }
    }

    public void Dispose() => Handle.Dispose();
}

internal sealed class AliWorkMemoryTreeClosure(
    SafeFileHandle rootHandle,
    string rootIdentity,
    IReadOnlyList<AliWorkMemoryTreeClosureNode> children) : IDisposable
{
    private IReadOnlyList<AliWorkMemoryTreeClosureNode> _children = children;
    private bool _rootRenamePrepared;

    internal SafeFileHandle RootHandle { get; } = rootHandle;

    internal string RootIdentity { get; } = rootIdentity;

    internal IReadOnlyList<AliWorkMemoryTreeClosureNode> Children => _children;

    internal bool RootRenamePrepared => _rootRenamePrepared;

    internal AliFileTreeItemSnapshot InitialSnapshot { get; set; } =
        AliFileTreeItemSnapshot.Absent;

    internal void ReleaseChildrenForRootRename()
    {
        if (_rootRenamePrepared)
        {
            throw new InvalidOperationException(
                "The exact work-memory tree is already prepared for a root rename.");
        }
        DisposeChildren();
        _rootRenamePrepared = true;
    }

    internal void ReplaceChildrenAfterRootRename(
        IReadOnlyList<AliWorkMemoryTreeClosureNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (!_rootRenamePrepared || _children.Count != 0)
        {
            throw new InvalidOperationException(
                "The exact work-memory tree cannot accept a resealed closure in its current state.");
        }
        _children = children;
    }

    internal void CompleteRootRename()
    {
        if (!_rootRenamePrepared)
        {
            throw new InvalidOperationException(
                "The exact work-memory tree has no prepared root rename to complete.");
        }
        _rootRenamePrepared = false;
    }

    internal void ReleaseChildrenForFailedReseal()
    {
        DisposeChildren();
        _rootRenamePrepared = true;
    }

    public void Dispose()
    {
        DisposeChildren();
    }

    private void DisposeChildren()
    {
        foreach (var child in _children)
        {
            child.Dispose();
        }
        _children = Array.Empty<AliWorkMemoryTreeClosureNode>();
    }
}

internal sealed class AliWorkMemoryTreeClosureNode(
    string name,
    AliWorkMemoryBoundEntry entry,
    IReadOnlyList<AliWorkMemoryTreeClosureNode> children) : IDisposable
{
    internal string Name { get; } = name;

    internal AliWorkMemoryBoundEntry Entry { get; } = entry;

    internal IReadOnlyList<AliWorkMemoryTreeClosureNode> Children { get; } = children;

    public void Dispose()
    {
        foreach (var child in Children)
        {
            child.Dispose();
        }
        Entry.Dispose();
    }
}

internal sealed class AliWorkMemoryTargetStateAdapter(
    string rootPath,
    Func<AgentWorkMemoryScope?> scopeAccessor) : IActionTargetStateAdapter
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);
    private readonly Func<AgentWorkMemoryScope?> _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.WorkMemoryWriteName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName,
        AliCapabilityCatalog.WorkMemoryDeleteName
    ];

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        var scope = _scopeAccessor()
            ?? throw new InvalidOperationException(
                "Work-memory target state requires an active conversation scope.");
        return CaptureExact(toolName, arguments, scope, includeMainContent: false).TargetState;
    }

    internal AliWorkMemoryCapturedTarget CaptureExact(
        string toolName,
        JsonElement arguments,
        AgentWorkMemoryScope scope,
        bool includeMainContent)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var fileName = AliExactWorkMemoryArguments.ReadFileName(toolName, arguments);
        var workspace = ResolveBeneath(_rootPath, scope.RelativePath);
        var workspaceFirst = AliFileTreeSnapshotter.CaptureStable(workspace);
        if (workspaceFirst.Exists
            && !string.Equals(workspaceFirst.Kind, "directory", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact work-memory workspace is not a regular directory.");
        }
        var mainFile = ResolveBeneath(workspace, fileName);
        var mainBefore = AliFileTreeSnapshotter.CaptureStable(mainFile);
        RequireRegularOptionalFile(mainBefore, "main file");
        if (mainBefore.Exists)
        {
            _ = AliWorkMemoryWindowsFileIdentity.RequireOptionalLiteralSingleLinkFile(
                mainFile,
                fileName,
                "The exact work-memory main file is an alias or multiply linked file.");
        }
        var mainContent = includeMainContent
            ? ReadOptionalText(mainFile, mainBefore)
            : null;
        var descriptionFileName = AliExactWorkMemoryArguments.DescriptionFileName(fileName);
        var descriptionFile = ResolveBeneath(workspace, descriptionFileName);
        var descriptionBefore = AliFileTreeSnapshotter.CaptureStable(descriptionFile);
        RequireRegularOptionalFile(descriptionBefore, "description file");
        if (descriptionBefore.Exists)
        {
            _ = AliWorkMemoryWindowsFileIdentity.RequireOptionalLiteralSingleLinkFile(
                descriptionFile,
                descriptionFileName,
                "The exact work-memory description file is an alias or multiply linked file.");
        }
        var workspaceSecond = AliFileTreeSnapshotter.CaptureStable(workspace);
        if (workspaceSecond != workspaceFirst)
        {
            throw new IOException(
                "The exact work-memory workspace changed while its target state was captured.");
        }
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace:" + scope.RelativePath.Replace('\\', '/')] =
                Version(workspaceSecond),
            ["file:" + fileName] = Version(mainBefore),
            ["description:" + descriptionFileName] = Version(descriptionBefore)
        };
        return new AliWorkMemoryCapturedTarget(
            scope,
            fileName,
            workspace,
            mainFile,
            descriptionFileName,
            descriptionFile,
            workspaceSecond,
            mainBefore,
            descriptionBefore,
            mainContent,
            new TargetStateSnapshot(
                versions,
                versions,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private static void RequireRegularOptionalFile(
        AliFileTreeItemSnapshot snapshot,
        string label)
    {
        if (snapshot.Exists && !string.Equals(snapshot.Kind, "file", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The exact work-memory {label} is not a regular file.");
        }
    }

    private static string? ReadOptionalText(
        string path,
        AliFileTreeItemSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            return null;
        }
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The exact work-memory target is not a regular file.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        var content = reader.ReadToEnd();
        if (AliFileTreeSnapshotter.CaptureStable(path) != snapshot)
        {
            throw new IOException(
                "The exact work-memory main file changed while its content was captured.");
        }
        return content;
    }

    private static string Version(AliFileTreeItemSnapshot snapshot) =>
        snapshot.Exists ? snapshot.Kind + ":sha256:" + snapshot.Digest : "absent";

    internal static string ResolveBeneath(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact work-memory target escaped its conversation workspace.");
        }
        return candidate;
    }
}

internal sealed class AliWorkMemoryDomainPlanStore
{
    private const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    internal AliWorkMemoryDomainPlanStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal async Task<string> WriteAsync(
        AliWorkMemoryDomainPlan plan,
        CancellationToken cancellationToken)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(plan));
        try
        {
            if (bytes.Length is < 1 or > MaximumBytes)
            {
                throw new InvalidDataException(
                    "The exact work-memory domain plan has an invalid size.");
            }
            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                _root,
                "The exact work-memory domain-plan root is not a regular local directory.");
            var destination = PathFor(plan.DomainId);
            var temporary = Path.Combine(_root, "." + plan.DomainId + ".tmp");
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The exact work-memory domain-plan temporary is not a regular file."))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporary,
                destination,
                replaceExisting: false,
                "The exact work-memory domain plan could not be committed safely.");
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task<AliWorkMemoryDomainPlan> ReadAsync(
        string domainId,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        AliDurableInvocationValidation.RequireId(domainId, nameof(domainId));
        TurnStateIntegrity.RequireDigest(expectedDigest, nameof(expectedDigest));
        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            PathFor(domainId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The exact work-memory domain plan is not a regular file.");
        if (stream.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException(
                "The exact work-memory domain plan has an invalid size.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    TurnStateIntegrity.Digest(bytes),
                    expectedDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exact work-memory domain plan digest is invalid.");
            }
            var plan = JsonSerializer.Deserialize<AliWorkMemoryDomainPlan>(bytes, JsonOptions)
                ?? throw new InvalidDataException(
                    "The exact work-memory domain plan is empty.");
            if (!string.Equals(plan.DomainId, domainId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exact work-memory domain-plan identity is invalid.");
            }
            return plan;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string PathFor(string domainId) =>
        Path.Combine(_root, domainId + ".work-memory-plan.json");
}

internal sealed record AliWorkMemoryProtectedExecutionBindingEnvelope(
    int FormatVersion,
    string PlanId,
    string DomainId,
    string DomainDigest,
    string AuthorizationDigest,
    string ProtectedPayload);

/// <summary>
/// A write-once DPAPI receipt binds the namespace object created only after the invocation has a
/// durable Started receipt. Recovery never infers that dynamic binding from path text.
/// </summary>
internal sealed class AliWorkMemoryExecutionBindingStore
{
    private const int FormatVersion = 1;
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _profileBinding;

    internal AliWorkMemoryExecutionBindingStore(string root, string profileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBinding);
        _root = Path.GetFullPath(root);
        _profileBinding = profileBinding;
    }

    internal void WriteOnce(AliWorkMemoryExecutionBinding binding)
    {
        Validate(binding);
        var plaintext = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(binding, JsonOptions));
        byte[]? protectedBytes = null;
        byte[]? envelopeBytes = null;
        try
        {
            var entropy = Entropy(
                binding.PlanId,
                binding.DomainId,
                binding.DomainDigest,
                binding.AuthorizationDigest);
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plaintext,
                    entropy,
                    DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
            var envelope = new AliWorkMemoryProtectedExecutionBindingEnvelope(
                binding.FormatVersion,
                binding.PlanId,
                binding.DomainId,
                binding.DomainDigest,
                binding.AuthorizationDigest,
                Convert.ToBase64String(protectedBytes));
            envelopeBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
                JsonSerializer.SerializeToElement(envelope, JsonOptions));
            if (envelopeBytes.Length is < 1 or > MaximumBytes)
            {
                throw new IOException(
                    "The protected work-memory execution binding has an invalid size.");
            }
            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                _root,
                "The work-memory execution-binding root is not a regular local directory.");
            using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                PathFor(binding.PlanId),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                writeThrough: true,
                "The work-memory execution binding is not a regular local file.");
            stream.Write(envelopeBytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            if (envelopeBytes is not null)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
    }

    internal AliWorkMemoryExecutionBinding? TryRead(
        string planId,
        string domainId,
        string expectedDomainDigest,
        string expectedAuthorizationDigest)
    {
        AliDurableInvocationValidation.RequireId(planId, nameof(planId));
        AliDurableInvocationValidation.RequireId(domainId, nameof(domainId));
        TurnStateIntegrity.RequireDigest(expectedDomainDigest, nameof(expectedDomainDigest));
        TurnStateIntegrity.RequireDigest(
            expectedAuthorizationDigest,
            nameof(expectedAuthorizationDigest));
        var path = PathFor(planId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] envelopeBytes;
        using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   writeThrough: false,
                   "The work-memory execution binding is not a regular local file."))
        {
            if (stream.Length is < 1 or > MaximumBytes)
            {
                throw new InvalidDataException(
                    "The protected work-memory execution binding has an invalid size.");
            }
            envelopeBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(envelopeBytes);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<AliWorkMemoryProtectedExecutionBindingEnvelope>(
                    envelopeBytes,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "The protected work-memory execution binding is empty.");
            if (envelope.FormatVersion != FormatVersion
                || !string.Equals(envelope.PlanId, planId, StringComparison.Ordinal)
                || !string.Equals(envelope.DomainId, domainId, StringComparison.Ordinal)
                || !string.Equals(
                    envelope.DomainDigest,
                    expectedDomainDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    envelope.AuthorizationDigest,
                    expectedAuthorizationDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The protected work-memory execution binding does not match its exact started invocation.");
            }
            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(envelope.ProtectedPayload);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The protected work-memory execution binding payload is malformed.",
                    exception);
            }
            try
            {
                var entropy = Entropy(
                    planId,
                    domainId,
                    expectedDomainDigest,
                    expectedAuthorizationDigest);
                byte[] plaintext;
                try
                {
                    plaintext = ProtectedData.Unprotect(
                        protectedBytes,
                        entropy,
                        DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidDataException(
                        "The work-memory execution binding failed its current-user integrity check.",
                        exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(entropy);
                }
                try
                {
                    var binding = JsonSerializer.Deserialize<AliWorkMemoryExecutionBinding>(
                            plaintext,
                            JsonOptions)
                        ?? throw new InvalidDataException(
                            "The protected work-memory execution binding payload is empty.");
                    Validate(binding);
                    if (!string.Equals(binding.PlanId, planId, StringComparison.Ordinal)
                        || !string.Equals(binding.DomainId, domainId, StringComparison.Ordinal)
                        || !string.Equals(
                            binding.DomainDigest,
                            expectedDomainDigest,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            binding.AuthorizationDigest,
                            expectedAuthorizationDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The protected work-memory execution binding payload changed identity.");
                    }
                    return binding;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private static void Validate(AliWorkMemoryExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                "The protected work-memory execution binding format is unsupported.");
        }
        AliDurableInvocationValidation.RequireId(binding.PlanId, nameof(binding.PlanId));
        AliDurableInvocationValidation.RequireId(binding.DomainId, nameof(binding.DomainId));
        TurnStateIntegrity.RequireDigest(binding.DomainDigest, nameof(binding.DomainDigest));
        TurnStateIntegrity.RequireDigest(
            binding.AuthorizationDigest,
            nameof(binding.AuthorizationDigest));
        ArgumentNullException.ThrowIfNull(binding.BackupParentSpine);
        if (binding.BackupParentSpine.Count < 2)
        {
            throw new InvalidDataException(
                "The protected work-memory execution binding has no complete backup-parent spine.");
        }
        RequireIdentity(binding.BackupWorkspaceIdentity, nameof(binding.BackupWorkspaceIdentity));
        RequireIdentity(binding.StagingWorkspaceIdentity, nameof(binding.StagingWorkspaceIdentity));
        if (binding.CanonicalWorkspaceIdentity is not null)
        {
            RequireIdentity(
                binding.CanonicalWorkspaceIdentity,
                nameof(binding.CanonicalWorkspaceIdentity));
        }
    }

    private static void RequireIdentity(string identity, string name)
    {
        var parts = identity.Split(':');
        if (parts.Length != 3
            || !string.Equals(parts[0], "directory", StringComparison.Ordinal)
            || parts[1].Length != 16
            || parts[2].Length != 32
            || !parts[1].All(Uri.IsHexDigit)
            || !parts[2].All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"The protected work-memory execution binding has an invalid {name}.");
        }
    }

    private byte[] Entropy(
        string planId,
        string domainId,
        string domainDigest,
        string authorizationDigest) => SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
        "\0",
        "Ali.WorkMemory.ExecutionBinding.v1",
        _profileBinding,
        planId,
        domainId,
        domainDigest,
        authorizationDigest)));

    private string PathFor(string planId) => Path.Combine(
        _root,
        planId + ".work-memory-execution-binding.protected");
}

internal sealed class AliAgentWorkMemoryExecutionCoordinator
{
    private const int MaximumUnresolvedStagingTransactions = 64;
    private const int MaximumUnresolvedStagingEntries = 100_000;
    private const long MaximumUnresolvedStagingBytes = 1024L * 1024 * 1024;
    private readonly ScopedAgentWorkMemoryStore _canonicalStore;
    private readonly Func<AgentWorkMemoryScope?> _scopeAccessor;
    private readonly AliWorkMemoryTargetStateAdapter _targetStates;
    private readonly AliDurableInvocationStore _invocations;
    private readonly AliWorkMemoryDomainPlanStore _domainPlans;
    private readonly AliWorkMemoryExecutionBindingStore _executionBindings;
    private readonly EvidenceLedger _evidence;
    private readonly string _rootPath;
    private readonly string _trashPath;
    private readonly string _stagingRoot;
    private readonly Action<AliWorkMemoryPreparationCheckpoint>? _preparationFaultHook;
    private readonly Action<AliWorkMemoryPublicationCheckpoint>? _publicationFaultHook;
    private readonly ConcurrentDictionary<string, ActiveInvocation> _active =
        new(StringComparer.Ordinal);

    internal AliAgentWorkMemoryExecutionCoordinator(
        ScopedAgentWorkMemoryStore canonicalStore,
        string rootPath,
        string trashPath,
        Func<AgentWorkMemoryScope?> scopeAccessor,
        string durableOrchestrationRoot,
        string assistantProfileBinding,
        EvidenceLedger? evidence = null,
        Action<AliWorkMemoryPreparationCheckpoint>? preparationFaultHook = null,
        Action<AliWorkMemoryPublicationCheckpoint>? publicationFaultHook = null)
    {
        _canonicalStore = canonicalStore ?? throw new ArgumentNullException(nameof(canonicalStore));
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
        _rootPath = Path.GetFullPath(rootPath);
        _trashPath = Path.GetFullPath(trashPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(durableOrchestrationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        var durableRoot = Path.GetFullPath(durableOrchestrationRoot);
        var root = Path.Combine(durableRoot, "AgentWorkMemoryInvocations");
        _stagingRoot = Path.Combine(root, "Staging");
        _targetStates = new AliWorkMemoryTargetStateAdapter(_rootPath, _scopeAccessor);
        _invocations = new AliDurableInvocationStore(
            Path.Combine(root, "Kernel"),
            assistantProfileBinding);
        _domainPlans = new AliWorkMemoryDomainPlanStore(Path.Combine(root, "Domain"));
        _executionBindings = new AliWorkMemoryExecutionBindingStore(
            Path.Combine(root, "ExecutionBindings"),
            assistantProfileBinding);
        _evidence = evidence ?? new EvidenceLedger(durableRoot, assistantProfileBinding);
        _preparationFaultHook = preparationFaultHook;
        _publicationFaultHook = publicationFaultHook;
        TargetStateAdapters = [_targetStates];
        ExecutionEffectAdapters =
        [
            new AliWorkMemoryWriteExecutionAdapter(this),
            new AliWorkMemoryReplaceExecutionAdapter(this),
            new AliWorkMemoryReplaceLinesExecutionAdapter(this),
            new AliWorkMemoryDeleteExecutionAdapter(this)
        ];
    }

    internal IReadOnlyList<IActionTargetStateAdapter> TargetStateAdapters { get; }

    internal IReadOnlyList<IAliExecutionEffectAdapter> ExecutionEffectAdapters { get; }

    internal ValueTask<AliExecutionPreparation> PrepareWriteAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliWorkMemoryMutation.Write,
            AliCapabilityCatalog.WorkMemoryWriteName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareReplaceAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliWorkMemoryMutation.Replace,
            AliCapabilityCatalog.WorkMemoryReplaceName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareReplaceLinesAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliWorkMemoryMutation.ReplaceLines,
            AliCapabilityCatalog.WorkMemoryReplaceLinesName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareDeleteAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliWorkMemoryMutation.Delete,
            AliCapabilityCatalog.WorkMemoryDeleteName,
            cancellationToken);

    private async ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        AliWorkMemoryMutation mutation,
        string exactToolName,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var identity = Identity(exactToolName);
        if (!identity.Matches(
                request.ToolName,
                request.CapabilityId,
                request.ReconcilerId))
        {
            throw new AliExecutionPreparationException(
                "The exact work-memory adapter received a mismatched execution identity.");
        }
        var scope = _scopeAccessor()
            ?? throw new AliExecutionPreparationException(
                "An exact work-memory mutation requires an active conversation scope.");
        var captured = _targetStates.CaptureExact(
            exactToolName,
            request.Arguments,
            scope,
            includeMainContent: true);
        var targetDigest = WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            captured.TargetState.TargetVersions);
        if (!string.Equals(targetDigest, request.TargetVersionDigest, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The exact work-memory target changed after the accepted decision.");
        }

        if (mutation == AliWorkMemoryMutation.Delete && !captured.MainFileBefore.Exists)
        {
            throw new FileNotFoundException(
                "The exact work-memory delete target does not exist.",
                captured.MainFilePath);
        }
        var mainAfterContent = mutation == AliWorkMemoryMutation.Delete
            ? null
            : AliExactWorkMemoryArguments.ComputePostContent(
                exactToolName,
                request.Arguments,
                captured.MainFileContent);
        var mainAfter = mainAfterContent is null
            ? AliFileTreeItemSnapshot.Absent
            : SnapshotForText(mainAfterContent);
        var descriptionAfterContent = mutation == AliWorkMemoryMutation.Write
            ? AliExactWorkMemoryArguments.ReadDescription(request.Arguments)
            : null;
        var descriptionAfter = mutation switch
        {
            AliWorkMemoryMutation.Write =>
                descriptionAfterContent is { } description
                    ? SnapshotForText(description)
                    : AliFileTreeItemSnapshot.Absent,
            AliWorkMemoryMutation.Delete => AliFileTreeItemSnapshot.Absent,
            _ => captured.DescriptionFileBefore
        };
        _preparationFaultHook?.Invoke(AliWorkMemoryPreparationCheckpoint.ExactTargetCaptured);
        RequireCapturedTargetStillCurrent(captured);

        var domainId = Guid.NewGuid().ToString("N");
        var transactionStaging = Path.Combine(_stagingRoot, domainId);
        var staging = Path.Combine(transactionStaging, "workspace");
        var backupParentSeed = Path.Combine(transactionStaging, "backup-parent-seed");
        var emptyBackupSeed = Path.Combine(transactionStaging, "empty-backup-seed");
        var durableTransactions = Path.Combine(_trashPath, "DurableTransactions");
        var backup = Path.Combine(
            durableTransactions,
            domainId,
            "workspace");
        var canonicalParent = Path.GetDirectoryName(captured.WorkspacePath)
            ?? throw new InvalidDataException(
                "The exact work-memory workspace has no canonical parent.");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            canonicalParent,
            "The exact work-memory canonical parent is not a regular local directory.");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _stagingRoot,
            "The exact work-memory staging root is not a regular local directory.");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            durableTransactions,
            "The exact work-memory durable-transaction anchor is not a regular local directory.");
        RequireStagingAdmission();
        var canonicalParentSpine = AliWorkMemoryWindowsFileIdentity.CaptureDirectorySpine(
            canonicalParent);
        var stagingRootSpine = AliWorkMemoryWindowsFileIdentity.CaptureDirectorySpine(
            _stagingRoot);
        var durableTransactionsSpine = AliWorkMemoryWindowsFileIdentity.CaptureDirectorySpine(
            durableTransactions);
        var canonicalWorkspaceIdentity = captured.WorkspaceBefore.Exists
            ? AliWorkMemoryWindowsFileIdentity.CaptureExistingDirectoryIdentity(
                captured.WorkspacePath)
            : null;
        _preparationFaultHook?.Invoke(
            AliWorkMemoryPreparationCheckpoint.CanonicalSourceIdentityBound);
        var backupAfter = captured.WorkspaceBefore.Exists
            ? captured.WorkspaceBefore
            : AliFileTreeSnapshotter.EmptyDirectory;
        string? preparedTransactionIdentity = null;
        try
        {
            ExpectedWorkspaceSnapshots expected;
            string stagingWorkspaceIdentity;
            string backupParentSeedIdentity;
            string emptyBackupSeedIdentity;
            using (var stagingRoot = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                       stagingRootSpine))
            using (var transaction = AliWorkMemoryWindowsFileIdentity.CreateBoundChildDirectory(
                       stagingRoot,
                       domainId))
            using (var stagedWorkspace = AliWorkMemoryWindowsFileIdentity
                       .CreateBoundChildDirectory(transaction, "workspace"))
            using (var preparedBackupParent = AliWorkMemoryWindowsFileIdentity
                       .CreateBoundChildDirectory(transaction, "backup-parent-seed"))
            using (var preparedEmptyBackup = AliWorkMemoryWindowsFileIdentity
                       .CreateBoundChildDirectory(transaction, "empty-backup-seed"))
            {
                preparedTransactionIdentity = transaction.Identity;
                StageWorkspace(
                    captured.WorkspacePath,
                    staging,
                    captured.WorkspaceBefore,
                    cancellationToken,
                    _preparationFaultHook);
                RequireStagedWorkspaceIdentities(
                    staging,
                    captured.FileName,
                    captured.DescriptionFileName);
                using var expectedWorkspaceDirectory = AliWorkMemoryWindowsFileIdentity
                    .CreateBoundChildDirectory(transaction, "expected-workspace");
                var expectedWorkspace = expectedWorkspaceDirectory.Path;
                try
                {
                    expected = await BuildExpectedWorkspaceAsync(
                            staging,
                            expectedWorkspace,
                            captured.WorkspaceBefore,
                            mutation,
                            captured.FileName,
                            captured.DescriptionFileName,
                            mainAfterContent,
                            descriptionAfterContent,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    AliWorkMemoryWindowsFileIdentity.DeleteBoundDirectoryTree(
                        expectedWorkspaceDirectory);
                }
                stagingWorkspaceIdentity = stagedWorkspace.Identity;
                backupParentSeedIdentity = preparedBackupParent.Identity;
                emptyBackupSeedIdentity = preparedEmptyBackup.Identity;
            }
            var stagingContainerSpine = AliWorkMemoryWindowsFileIdentity.CaptureDirectorySpine(
                transactionStaging);
            if (!AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                    staging,
                    stagingWorkspaceIdentity)
                || !AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                    backupParentSeed,
                    backupParentSeedIdentity)
                || !AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                    emptyBackupSeed,
                    emptyBackupSeedIdentity))
            {
                throw new IOException(
                    "An exact work-memory preparation object changed before its durable plan was bound.");
            }
            _preparationFaultHook?.Invoke(
                AliWorkMemoryPreparationCheckpoint.StagingSourceIdentityBound);
            RequireCapturedWorkspaceIdentity(
                captured,
                canonicalParentSpine,
                canonicalWorkspaceIdentity);
            _preparationFaultHook?.Invoke(
                AliWorkMemoryPreparationCheckpoint.BeforeDomainPlanPersisted);
            RequireCapturedWorkspaceIdentity(
                captured,
                canonicalParentSpine,
                canonicalWorkspaceIdentity);
            using (var stagingContainer = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                       stagingContainerSpine))
            using (var stagedWorkspace = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                       stagingContainer,
                       "workspace",
                       stagingWorkspaceIdentity))
            {
                var authenticatedStagedPreimage = captured.WorkspaceBefore.Exists
                    ? captured.WorkspaceBefore
                    : AliFileTreeSnapshotter.EmptyDirectory;
                if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                        stagedWorkspace,
                        staging)
                    != authenticatedStagedPreimage)
                {
                    throw new IOException(
                        "The exact staged work-memory source changed before durable preparation.");
                }
            }
            var domain = new AliWorkMemoryDomainPlan(
                domainId,
                mutation,
                exactToolName,
                captured.Scope,
                captured.FileName,
                captured.WorkspacePath,
                staging,
                backup,
                captured.WorkspaceBefore,
                expected.WorkspaceAfter,
                captured.MainFileBefore,
                mainAfter,
                captured.DescriptionFileName,
                captured.DescriptionFileBefore,
                descriptionAfter,
                expected.MemoryIndexFileAfter,
                AliFileTreeItemSnapshot.Absent,
                backupAfter,
                canonicalParentSpine,
                canonicalWorkspaceIdentity,
                stagingContainerSpine,
                stagingWorkspaceIdentity,
                backupParentSeed,
                backupParentSeedIdentity,
                emptyBackupSeed,
                emptyBackupSeedIdentity,
                durableTransactionsSpine);
            var domainDigest = await _domainPlans.WriteAsync(domain, cancellationToken)
                .ConfigureAwait(false);
            var rootBinding = RootBinding(domain);
            var invocation = AliDurableInvocationPlan.Create(
                request,
                rootBinding,
                domainId,
                domainDigest);
            await _invocations.PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            return new AliExecutionPreparation(
                invocation.Id,
                rootBinding,
                request.TargetVersionDigest);
        }
        catch
        {
            TryDeletePreparedTransaction(
                stagingRootSpine,
                domainId,
                preparedTransactionIdentity);
            throw;
        }
    }

    internal async ValueTask<AgentFileStore?> GetInvocationStoreAsync(
        CancellationToken cancellationToken)
    {
        if (TryCurrentBinding(
                Identity(AliCapabilityCatalog.WorkMemoryWriteName),
                out var identity,
                out var preparationIdentity,
                out var rootBinding)
            || TryCurrentBinding(
                Identity(AliCapabilityCatalog.WorkMemoryReplaceName),
                out identity,
                out preparationIdentity,
                out rootBinding)
            || TryCurrentBinding(
                Identity(AliCapabilityCatalog.WorkMemoryReplaceLinesName),
                out identity,
                out preparationIdentity,
                out rootBinding)
            || TryCurrentBinding(
                Identity(AliCapabilityCatalog.WorkMemoryDeleteName),
                out identity,
                out preparationIdentity,
                out rootBinding))
        {
            return await GetOrStartAsync(
                    identity,
                    preparationIdentity!,
                    rootBinding!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return null;
    }

    private async ValueTask<AgentFileStore> GetOrStartAsync(
        AliExactExecutionAdapterIdentity exactIdentity,
        string preparationIdentity,
        string rootBinding,
        CancellationToken cancellationToken)
    {
        if (_active.TryGetValue(preparationIdentity, out var current))
        {
            RequireActiveScope(current.Domain);
            if (!string.Equals(
                    current.Domain.ToolName,
                    exactIdentity.ToolName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RootBinding(current.Domain),
                    rootBinding,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The active work-memory invocation does not match the current exact binding.");
            }
            return current.Store;
        }

        var snapshot = await _invocations.LoadAsync(preparationIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(snapshot.Plan.RootBinding, rootBinding, StringComparison.Ordinal)
            || snapshot.Plan.ExactIdentity != exactIdentity)
        {
            throw new InvalidOperationException(
                "The current work-memory binding does not match its protected plan.");
        }
        if (snapshot.State == AliDurableInvocationState.Prepared)
        {
            snapshot = await AliDurableInvocationGrantConsumer.ConsumeCurrentAndStartAsync(
                    _invocations,
                    exactIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (snapshot.State != AliDurableInvocationState.Started)
        {
            throw new InvalidOperationException(
                "Only a started exact work-memory invocation can access its staging store.");
        }
        var domain = await _domainPlans.ReadAsync(
                snapshot.Plan.DomainPreparationIdentity,
                snapshot.Plan.DomainPreparationDigest,
                cancellationToken)
            .ConfigureAwait(false);
        RequireActiveScope(domain);
        if (!string.Equals(domain.ToolName, exactIdentity.ToolName, StringComparison.Ordinal)
            || !string.Equals(RootBinding(domain), snapshot.Plan.RootBinding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact work-memory domain plan does not match its invocation.");
        }
        var authorizationDigest = snapshot.Receipt?.AuthorizationDigest
            ?? throw new InvalidDataException(
                "The started work-memory invocation has no protected authorization digest.");
        var executionBinding = EnsureStartedExecutionBinding(
            snapshot.Plan,
            domain,
            authorizationDigest);
        var executionLease = OpenExecutionLease(domain, executionBinding);
        ActiveInvocation active;
        try
        {
            active = new ActiveInvocation(
                domain,
                executionBinding,
                executionLease,
                new AliExactAgentWorkMemoryInvocationStore(
                    domain,
                    executionLease.Staging,
                    _publicationFaultHook));
        }
        catch
        {
            executionLease.Dispose();
            throw;
        }
        if (!_active.TryAdd(snapshot.Plan.Id, active))
        {
            executionLease.Dispose();
            throw new InvalidOperationException(
                "The exact work-memory invocation was activated concurrently.");
        }
        var participant = new CompletionParticipant(
            this,
            snapshot.Plan.Id,
            domain,
            executionBinding,
            executionLease);
        if (!AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                exactIdentity.ToolName,
                exactIdentity.CapabilityId,
                exactIdentity.ReconcilerId,
                snapshot.Plan.Id,
                snapshot.Plan.RootBinding,
                participant))
        {
            _active.TryRemove(snapshot.Plan.Id, out _);
            executionLease.Dispose();
            await _invocations.MarkInDoubtAsync(
                    snapshot.Plan.Id,
                    expectedRevision: 1,
                    "work-memory-completion-registration-failed",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact work-memory completion participant could not be registered.");
        }
        return active.Store;
    }

    private AliWorkMemoryExecutionBinding EnsureStartedExecutionBinding(
        AliDurableInvocationPlan invocation,
        AliWorkMemoryDomainPlan domain,
        string authorizationDigest)
    {
        var existing = _executionBindings.TryRead(
            invocation.Id,
            domain.DomainId,
            invocation.DomainPreparationDigest,
            authorizationDigest);
        if (existing is not null)
        {
            RequireExecutionBinding(domain, invocation, existing, authorizationDigest);
            using var heldBackupParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                existing.BackupParentSpine);
            return existing;
        }

        using var stagingContainer = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            domain.StagingContainerSpine);
        if (!string.Equals(
                NormalizePhysical(stagingContainer.ParentPath),
                NormalizePhysical(Path.GetDirectoryName(domain.BackupParentSeedPath)!),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact work-memory backup-parent seed escaped its prepared container.");
        }
        using var seed = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
            stagingContainer,
            Path.GetFileName(domain.BackupParentSeedPath),
            domain.BackupParentSeedIdentity);
        if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                seed,
                domain.BackupParentSeedPath)
            != AliFileTreeSnapshotter.EmptyDirectory)
        {
            throw new InvalidDataException(
                "The exact work-memory backup-parent seed is not empty.");
        }
        using var durableTransactions = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            domain.DurableTransactionsSpine);
        var expectedBackupParent = Path.GetDirectoryName(domain.BackupWorkspacePath)
            ?? throw new InvalidDataException(
                "The exact work-memory backup workspace has no transaction parent.");
        if (!string.Equals(
                NormalizePhysical(durableTransactions.ParentPath),
                NormalizePhysical(Path.GetDirectoryName(expectedBackupParent)!),
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(expectedBackupParent),
                domain.DomainId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact work-memory backup transaction parent escaped its durable anchor.");
        }

        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.BeforeBackupParentCreate);
        using (var unexpected = AliWorkMemoryWindowsFileIdentity.TryOpenBoundChildDirectory(
                   durableTransactions,
                   domain.DomainId))
        {
            if (unexpected is not null)
            {
                throw new IOException(
                    "The exact work-memory backup transaction parent already exists without its protected binding.");
            }
        }
        AliWorkMemoryWindowsFileIdentity.RenameNoReplace(
            seed,
            durableTransactions,
            domain.DomainId);
        if (!AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                expectedBackupParent,
                seed.Identity)
            || AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                domain.BackupParentSeedPath,
                seed.Identity))
        {
            throw new IOException(
                "The exact work-memory backup parent could not be recaptured after its handle rename.");
        }
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.AfterBackupParentCreate);
        var backupParentSpine = AliWorkMemoryWindowsFileIdentity.ExtendSpine(
            durableTransactions,
            domain.DomainId,
            seed.Identity);
        var binding = new AliWorkMemoryExecutionBinding(
            1,
            invocation.Id,
            domain.DomainId,
            invocation.DomainPreparationDigest,
            authorizationDigest,
            backupParentSpine,
            domain.WorkspaceBefore.Exists
                ? domain.CanonicalWorkspaceIdentity
                    ?? throw new InvalidDataException(
                        "The exact work-memory preimage has no prepared directory identity.")
                : domain.EmptyBackupSeedIdentity,
            domain.StagingWorkspaceIdentity,
            domain.CanonicalWorkspaceIdentity);
        _executionBindings.WriteOnce(binding);
        return _executionBindings.TryRead(
                   invocation.Id,
                   domain.DomainId,
                   invocation.DomainPreparationDigest,
                   authorizationDigest)
               ?? throw new IOException(
                   "The exact work-memory execution binding was not durable after its write-once commit.");
    }

    private static void RequireExecutionBinding(
        AliWorkMemoryDomainPlan domain,
        AliDurableInvocationPlan invocation,
        AliWorkMemoryExecutionBinding binding,
        string authorizationDigest)
    {
        var expectedBackupParent = Path.GetDirectoryName(domain.BackupWorkspacePath)
            ?? throw new InvalidDataException(
                "The exact work-memory backup workspace has no transaction parent.");
        var expectedBackupIdentity = domain.WorkspaceBefore.Exists
            ? domain.CanonicalWorkspaceIdentity
            : domain.EmptyBackupSeedIdentity;
        if (!string.Equals(binding.PlanId, invocation.Id, StringComparison.Ordinal)
            || !string.Equals(binding.DomainId, domain.DomainId, StringComparison.Ordinal)
            || !string.Equals(
                binding.DomainDigest,
                invocation.DomainPreparationDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.AuthorizationDigest,
                authorizationDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                NormalizePhysical(binding.BackupParentSpine[^1].PhysicalPath),
                NormalizePhysical(expectedBackupParent),
                StringComparison.Ordinal)
            || !string.Equals(
                binding.BackupWorkspaceIdentity,
                expectedBackupIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.StagingWorkspaceIdentity,
                domain.StagingWorkspaceIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.CanonicalWorkspaceIdentity,
                domain.CanonicalWorkspaceIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected work-memory execution binding does not match its durable domain plan.");
        }
    }

    private static AliWorkMemoryExecutionLease OpenExecutionLease(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding)
    {
        AliWorkMemoryDirectorySpine? canonicalParent = null;
        AliWorkMemoryDirectorySpine? stagingContainer = null;
        AliWorkMemoryDirectorySpine? backupParent = null;
        AliWorkMemoryBoundDirectory? staging = null;
        AliWorkMemoryBoundDirectory? canonical = null;
        AliWorkMemoryBoundDirectory? emptyBackupSeed = null;
        try
        {
            canonicalParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                domain.CanonicalParentSpine);
            stagingContainer = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                domain.StagingContainerSpine);
            backupParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                executionBinding.BackupParentSpine);
            staging = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                stagingContainer,
                Path.GetFileName(domain.StagingWorkspacePath),
                domain.StagingWorkspaceIdentity,
                writable: true);
            if (domain.WorkspaceBefore.Exists)
            {
                canonical = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                    canonicalParent,
                    Path.GetFileName(domain.CanonicalWorkspacePath),
                    domain.CanonicalWorkspaceIdentity
                    ?? throw new InvalidDataException(
                        "The exact canonical work-memory source has no prepared identity."));
            }
            else
            {
                RequireChildAbsent(
                    canonicalParent,
                    Path.GetFileName(domain.CanonicalWorkspacePath),
                    "canonical workspace at execution start");
                emptyBackupSeed = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                    stagingContainer,
                    Path.GetFileName(domain.EmptyBackupSeedPath),
                    domain.EmptyBackupSeedIdentity);
            }
            return new AliWorkMemoryExecutionLease(
                canonicalParent,
                stagingContainer,
                backupParent,
                staging,
                canonical,
                emptyBackupSeed);
        }
        catch
        {
            emptyBackupSeed?.Dispose();
            canonical?.Dispose();
            staging?.Dispose();
            backupParent?.Dispose();
            stagingContainer?.Dispose();
            canonicalParent?.Dispose();
            throw;
        }
    }

    private static bool TryCurrentBinding(
        AliExactExecutionAdapterIdentity candidate,
        out AliExactExecutionAdapterIdentity identity,
        out string? preparationIdentity,
        out string? rootBinding)
    {
        identity = candidate;
        return AliExecutionGrantContext.TryGetCurrentBinding(
            candidate.ToolName,
            candidate.CapabilityId,
            candidate.ReconcilerId,
            out preparationIdentity,
            out rootBinding);
    }

    internal async ValueTask<ActionReconciliationResult> ReconcileAsync(
        AliExactExecutionAdapterIdentity exactIdentity,
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (!exactIdentity.Matches(
                intent.ToolName,
                intent.CapabilityId,
                intent.ReconcilerId)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return ActionReconciliationResult.Unknown(
                "work-memory-adapter-identity-mismatch");
        }
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliDurableInvocationStore.AuthorizationDomain,
                intent,
                out var authorizationDigest))
        {
            return ActionReconciliationResult.Unknown(
                "work-memory-authorization-identity-missing");
        }
        try
        {
            var recovery = await new AliDurableInvocationReconciler(
                    _invocations,
                    exactIdentity,
                    new StartedDomainReconciler(this, exactIdentity))
                .ReconcileAsync(
                    intent.PreparationIdentity!,
                    authorizationDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Disposition is AliDurableInvocationRecoveryDisposition.Applied
                or AliDurableInvocationRecoveryDisposition.Absent
                or AliDurableInvocationRecoveryDisposition.Failed)
            {
                await TryCleanupTerminalStagingAsync(
                        intent.PreparationIdentity!,
                        recovery.Disposition == AliDurableInvocationRecoveryDisposition.Applied
                            ? AliWorkMemoryClassification.Applied
                            : AliWorkMemoryClassification.Absent)
                    .ConfigureAwait(false);
            }
            return recovery.Disposition switch
            {
                AliDurableInvocationRecoveryDisposition.Applied =>
                    ActionReconciliationResult.Applied(
                        recovery.OutcomeCode,
                        await AppendEvidenceAsync(
                                identity,
                                intent,
                                recovery.OutcomeCode,
                                cancellationToken)
                            .ConfigureAwait(false)),
                AliDurableInvocationRecoveryDisposition.Absent =>
                    ActionReconciliationResult.Absent(recovery.OutcomeCode),
                AliDurableInvocationRecoveryDisposition.Failed =>
                    ActionReconciliationResult.Absent(recovery.OutcomeCode),
                _ => ActionReconciliationResult.Unknown(recovery.OutcomeCode)
            };
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "work-memory-reconcile-" + StableExceptionCode(exception));
        }
    }

    private async Task CommitAsync(
        string planId,
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding,
        AliWorkMemoryExecutionLease executionLease,
        object? result,
        CancellationToken cancellationToken)
    {
        if (domain.Mutation == AliWorkMemoryMutation.Delete && result is false)
        {
            await _invocations.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    "work-memory-delete-reported-no-effect",
                    cancellationToken)
                .ConfigureAwait(false);
            _canonicalStore.ReportExactDurableOutcome(
                domain.ToolName,
                AliFrameworkToolOutcomeSignal.Failed);
            throw new InvalidDataException(
                "The Agent Framework work-memory delete reported that no file was deleted.");
        }
        try
        {
            PublishHandleBound(domain, executionBinding, executionLease);
            _publicationFaultHook?.Invoke(
                AliWorkMemoryPublicationCheckpoint.BeforeDurableCompletion);
            RequireSealedPublishedState(domain, executionLease);
        }
        catch (AliWorkMemorySimulatedInterruptionException)
        {
            executionLease.Dispose();
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            executionLease.Dispose();
            var classification = AliWorkMemoryClassification.Unknown;
            try
            {
                classification = RecoverPublication(domain, executionBinding);
            }
            catch (Exception recoveryException) when (IsRecoverableFailure(recoveryException))
            {
                classification = AliWorkMemoryClassification.Unknown;
            }
            await RecordPublicationFailureAsync(
                    planId,
                    domain,
                    executionBinding,
                    classification)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await _invocations.CompleteAsync(
                    planId,
                    expectedRevision: 1,
                    "work-memory-effect-applied",
                    ResultDigest(result),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Release the sealed trees only after the durable terminal receipt is committed.
            executionLease.Dispose();
        }
        TryCleanupTerminalStaging(domain, AliWorkMemoryClassification.Applied);
        _canonicalStore.ReportExactDurableOutcome(
            domain.ToolName,
            AliFrameworkToolOutcomeSignal.Completed);
        await _canonicalStore.AppendExactDurableAuditAsync(
                domain.Scope,
                domain.Mutation.ToString().ToLowerInvariant(),
                domain.FileName,
                succeeded: true,
                "committed",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void PublishHandleBound(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding,
        AliWorkMemoryExecutionLease executionLease)
    {
        _ = executionBinding;
        executionLease.SealForPublication(domain);
        var canonicalParent = executionLease.CanonicalParent;
        var stagingContainer = executionLease.StagingContainer;
        var backupParent = executionLease.BackupParent;
        var staging = executionLease.Staging;
        var canonical = executionLease.Canonical;
        var stagingClosure = executionLease.StagingClosure
            ?? throw new InvalidOperationException(
                "The exact staged work-memory tree was not sealed for publication.");
        var canonicalClosure = executionLease.CanonicalClosure;
        if (!domain.WorkspaceBefore.Exists)
        {
            RequireChildAbsent(
                canonicalParent,
                Path.GetFileName(domain.CanonicalWorkspacePath),
                "canonical workspace");
        }
        RequireChildAbsent(
            backupParent,
            Path.GetFileName(domain.BackupWorkspacePath),
            "backup workspace");
        RequireBoundSnapshot(
            stagingClosure,
            domain.StagingWorkspacePath,
            domain.WorkspaceAfter,
            "staged postimage");
        RequireExactStagedFiles(domain, stagingClosure, domain.StagingWorkspacePath);
        if (canonical is not null)
        {
            RequireBoundSnapshot(
                canonicalClosure
                    ?? throw new InvalidOperationException(
                        "The exact canonical work-memory tree was not sealed for publication."),
                domain.CanonicalWorkspacePath,
                domain.WorkspaceBefore,
                "canonical preimage");
        }

        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup);
        RequireBoundSnapshot(
            stagingClosure,
            domain.StagingWorkspacePath,
            domain.WorkspaceAfter,
            "staged postimage");
        AliWorkMemoryBoundDirectory backupSource;
        if (canonical is not null)
        {
            RequireBoundSnapshot(
                canonicalClosure!,
                domain.CanonicalWorkspacePath,
                domain.WorkspaceBefore,
                "canonical preimage");
            backupSource = canonical;
        }
        else
        {
            backupSource = executionLease.EmptyBackupSeed
                ?? throw new InvalidDataException(
                    "The exact absent work-memory preimage has no retained empty backup seed.");
        }
        var backupSourceClosure = canonical is not null
            ? canonicalClosure!
            : executionLease.EmptyBackupSeedClosure
                ?? throw new InvalidOperationException(
                    "The exact empty backup seed was not sealed for publication.");
        RequireBoundSnapshot(
            backupSourceClosure,
            backupSource.Path,
            domain.BackupAfter,
            "prepared backup");
        RenameAndReseal(
            backupSource,
            backupSourceClosure,
            backupSource.Path,
            backupParent,
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "backup publication");
        RequireBoundSnapshot(
            backupSourceClosure,
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "renamed backup");
        RequireChildAbsent(
            canonicalParent,
            Path.GetFileName(domain.CanonicalWorkspacePath),
            "canonical workspace after backup rename");
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup);

        RequireBoundSnapshot(
            backupSourceClosure,
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "renamed backup");
        RequireChildAbsent(
            canonicalParent,
            Path.GetFileName(domain.CanonicalWorkspacePath),
            "canonical workspace before publication");
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.BeforeStagingToCanonical);
        RequireBoundSnapshot(
            stagingClosure,
            domain.StagingWorkspacePath,
            domain.WorkspaceAfter,
            "staged postimage immediately before publication");
        RequireExactStagedFiles(domain, stagingClosure, domain.StagingWorkspacePath);
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.AfterFinalStagingCheckBeforeRename);
        RequireBoundSnapshot(
            stagingClosure,
            domain.StagingWorkspacePath,
            domain.WorkspaceAfter,
            "sealed staged postimage after the final publication seam");
        RequireBoundSnapshot(
            backupSourceClosure,
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "sealed publication backup after the final publication seam");
        RenameAndReseal(
            staging,
            stagingClosure,
            domain.StagingWorkspacePath,
            canonicalParent,
            domain.CanonicalWorkspacePath,
            domain.WorkspaceAfter,
            "canonical publication");
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical);
        RequireBoundSnapshot(
            stagingClosure,
            domain.CanonicalWorkspacePath,
            domain.WorkspaceAfter,
            "published canonical postimage");
        RequireChildAbsent(
            stagingContainer,
            Path.GetFileName(domain.StagingWorkspacePath),
            "staging workspace after publication");
        RequireBoundSnapshot(
            backupSourceClosure,
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "authenticated publication backup");
    }

    private void RenameAndReseal(
        AliWorkMemoryBoundDirectory source,
        AliWorkMemoryTreeClosure closure,
        string originalPath,
        AliWorkMemoryDirectorySpine destinationParent,
        string destinationPath,
        AliFileTreeItemSnapshot expected,
        string operation)
    {
        AliWorkMemoryWindowsFileIdentity.PrepareTreeClosureForRootRename(
            closure,
            originalPath,
            expected);
        try
        {
            AliWorkMemoryWindowsFileIdentity.RenameNoReplace(
                source,
                destinationParent,
                Path.GetFileName(destinationPath));
        }
        catch (Exception renameException)
        {
            try
            {
                AliWorkMemoryWindowsFileIdentity.ResealTreeClosureAfterRootRename(
                    closure,
                    originalPath,
                    expected);
            }
            catch (Exception resealException)
            {
                throw new IOException(
                    $"The exact work-memory {operation} rename failed and its original tree could not be resealed.",
                    new AggregateException(renameException, resealException));
            }
            throw;
        }

        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.AfterRootRenameBeforeReseal);
        try
        {
            AliWorkMemoryWindowsFileIdentity.ResealTreeClosureAfterRootRename(
                closure,
                destinationPath,
                expected);
        }
        catch (Exception resealException)
        {
            throw new IOException(
                $"The exact work-memory {operation} moved its held root but could not authenticate and reseal the destination tree; durable reconciliation is required.",
                resealException);
        }
    }

    private static void RequireSealedPublishedState(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionLease executionLease)
    {
        var publishedClosure = executionLease.StagingClosure
            ?? throw new InvalidOperationException(
                "The exact published work-memory tree has no retained seal.");
        var backupClosure = domain.WorkspaceBefore.Exists
            ? executionLease.CanonicalClosure
            : executionLease.EmptyBackupSeedClosure;
        RequireBoundSnapshot(
            publishedClosure,
            domain.CanonicalWorkspacePath,
            domain.WorkspaceAfter,
            "published canonical state before durable completion");
        RequireExactStagedFiles(
            domain,
            publishedClosure,
            domain.CanonicalWorkspacePath);
        RequireBoundSnapshot(
            backupClosure
                ?? throw new InvalidOperationException(
                    "The exact publication backup has no retained seal."),
            domain.BackupWorkspacePath,
            domain.BackupAfter,
            "publication backup before durable completion");
    }

    private AliWorkMemoryClassification Classify(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding)
    {
        try
        {
            using var canonicalParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                domain.CanonicalParentSpine);
            using var stagingContainer = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                domain.StagingContainerSpine);
            using var backupParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                executionBinding.BackupParentSpine);
            using var canonical = ObserveChild(
                canonicalParent,
                Path.GetFileName(domain.CanonicalWorkspacePath));
            using var staging = ObserveChild(
                stagingContainer,
                Path.GetFileName(domain.StagingWorkspacePath));
            using var backup = ObserveChild(
                backupParent,
                Path.GetFileName(domain.BackupWorkspacePath));
            _publicationFaultHook?.Invoke(
                AliWorkMemoryPublicationCheckpoint.AfterClassificationSnapshot);
            canonical?.RequireUnchanged();
            staging?.RequireUnchanged();
            backup?.RequireUnchanged();
            if (canonical is not null
                && string.Equals(
                    canonical.Identity,
                    domain.StagingWorkspaceIdentity,
                    StringComparison.Ordinal)
                && canonical.Snapshot == domain.WorkspaceAfter
                && staging is null
                && backup is not null
                && string.Equals(
                    backup.Identity,
                    executionBinding.BackupWorkspaceIdentity,
                    StringComparison.Ordinal)
                && backup.Snapshot == domain.BackupAfter)
            {
                return AliWorkMemoryClassification.Applied;
            }
            var canonicalAbsent = domain.WorkspaceBefore.Exists
                ? canonical is not null
                  && string.Equals(
                      canonical.Identity,
                      domain.CanonicalWorkspaceIdentity,
                      StringComparison.Ordinal)
                  && canonical.Snapshot == domain.WorkspaceBefore
                : canonical is null;
            var stagingRecognized = staging is null
                || string.Equals(
                    staging.Identity,
                    domain.StagingWorkspaceIdentity,
                    StringComparison.Ordinal);
            if (canonicalAbsent && backup is null && stagingRecognized)
            {
                return AliWorkMemoryClassification.Absent;
            }
            return AliWorkMemoryClassification.Unknown;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliWorkMemoryClassification.Unknown;
        }
    }

    private AliWorkMemoryClassification RecoverPublication(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding)
    {
        var classification = Classify(domain, executionBinding);
        if (classification != AliWorkMemoryClassification.Unknown)
        {
            return classification;
        }
        using var canonicalParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            domain.CanonicalParentSpine);
        using var stagingContainer = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            domain.StagingContainerSpine);
        using var backupParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            executionBinding.BackupParentSpine);
        using var backup = AliWorkMemoryWindowsFileIdentity.TryOpenSealedBoundChildDirectory(
            backupParent,
            Path.GetFileName(domain.BackupWorkspacePath),
            executionBinding.BackupWorkspaceIdentity);
        if (backup is null)
        {
            return AliWorkMemoryClassification.Unknown;
        }
        using var backupClosure = AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
            backup,
            domain.BackupWorkspacePath);
        if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                backupClosure,
                domain.BackupWorkspacePath) != domain.BackupAfter)
        {
            return AliWorkMemoryClassification.Unknown;
        }
        using var canonical = AliWorkMemoryWindowsFileIdentity.TryOpenSealedBoundChildDirectory(
            canonicalParent,
            Path.GetFileName(domain.CanonicalWorkspacePath));
        using var canonicalClosure = canonical is null
            ? null
            : AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
                canonical,
                domain.CanonicalWorkspacePath);
        if (canonical is not null)
        {
            const string quarantineLeaf = "quarantine-workspace";
            using (var occupiedQuarantine = AliWorkMemoryWindowsFileIdentity
                       .TryOpenBoundChildDirectory(backupParent, quarantineLeaf))
            {
                if (occupiedQuarantine is not null)
                {
                    return AliWorkMemoryClassification.Unknown;
                }
            }
            _publicationFaultHook?.Invoke(
                AliWorkMemoryPublicationCheckpoint.BeforeRecoveryQuarantine);
            if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    canonicalClosure!,
                    domain.CanonicalWorkspacePath) != canonicalClosure!.InitialSnapshot)
            {
                return AliWorkMemoryClassification.Unknown;
            }
            var canonicalIdentity = canonical.Identity;
            RenameAndReseal(
                canonical,
                canonicalClosure!,
                domain.CanonicalWorkspacePath,
                backupParent,
                Path.Combine(backupParent.ParentPath, quarantineLeaf),
                canonicalClosure.InitialSnapshot,
                "recovery quarantine");
            var quarantinePath = Path.Combine(backupParent.ParentPath, quarantineLeaf);
            if (!AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                    quarantinePath,
                    canonicalIdentity)
                || AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    canonicalClosure,
                    quarantinePath) != canonicalClosure.InitialSnapshot)
            {
                return AliWorkMemoryClassification.Unknown;
            }
        }
        RequireChildAbsent(
            canonicalParent,
            Path.GetFileName(domain.CanonicalWorkspacePath),
            "canonical recovery destination");
        _publicationFaultHook?.Invoke(
            AliWorkMemoryPublicationCheckpoint.BeforeRecoveryRestore);
        if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                backupClosure,
                domain.BackupWorkspacePath) != domain.BackupAfter)
        {
            return AliWorkMemoryClassification.Unknown;
        }
        if (domain.WorkspaceBefore.Exists)
        {
            RenameAndReseal(
                backup,
                backupClosure,
                domain.BackupWorkspacePath,
                canonicalParent,
                domain.CanonicalWorkspacePath,
                domain.BackupAfter,
                "recovery restore");
            if (!AliWorkMemoryWindowsFileIdentity.PathMatchesDirectoryIdentity(
                    domain.CanonicalWorkspacePath,
                    executionBinding.BackupWorkspaceIdentity)
                || AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    backupClosure,
                    domain.CanonicalWorkspacePath) != domain.BackupAfter)
            {
                return AliWorkMemoryClassification.Unknown;
            }
        }
        else
        {
            AliWorkMemoryWindowsFileIdentity.DeleteEmptyBoundDirectory(backup);
        }
        canonicalClosure?.Dispose();
        canonical?.Dispose();
        backupClosure.Dispose();
        backup.Dispose();
        return Classify(domain, executionBinding);
    }

    private static AliWorkMemoryObservedDirectory? ObserveChild(
        AliWorkMemoryDirectorySpine parent,
        string leaf)
    {
        var child = AliWorkMemoryWindowsFileIdentity.TryOpenSealedBoundChildDirectory(
            parent,
            leaf);
        if (child is null)
        {
            return null;
        }
        AliWorkMemoryTreeClosure? closure = null;
        try
        {
            closure = AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
                child,
                child.Path);
            return new AliWorkMemoryObservedDirectory(
                child,
                closure,
                AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    closure,
                    child.Path));
        }
        catch
        {
            closure?.Dispose();
            child.Dispose();
            throw;
        }
    }

    private static void RequireChildAbsent(
        AliWorkMemoryDirectorySpine parent,
        string leaf,
        string label)
    {
        using var child = AliWorkMemoryWindowsFileIdentity.TryOpenBoundChildDirectory(
            parent,
            leaf);
        if (child is not null)
        {
            throw new IOException($"The exact work-memory {label} is no longer absent.");
        }
    }

    private static void RequireBoundSnapshot(
        AliWorkMemoryBoundDirectory directory,
        string path,
        AliFileTreeItemSnapshot expected,
        string label)
    {
        if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(directory, path) != expected)
        {
            throw new IOException(
                $"The exact held work-memory {label} does not match its authenticated state.");
        }
    }

    private static void RequireBoundSnapshot(
        AliWorkMemoryTreeClosure closure,
        string path,
        AliFileTreeItemSnapshot expected,
        string label)
    {
        if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(closure, path) != expected)
        {
            throw new IOException(
                $"The exact sealed work-memory {label} does not match its authenticated state.");
        }
    }

    private static void RequireExactStagedFiles(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryTreeClosure stagingClosure,
        string currentStagingPath)
    {
        AliWorkMemoryWindowsFileIdentity.RequireTreeClosureUnchanged(
            stagingClosure,
            currentStagingPath);
        if (AliFileTreeSnapshotter.CaptureStable(Path.Combine(
                currentStagingPath,
                AliExactAgentWorkMemoryInvocationStore.MemoryIndexFileName))
            != domain.MemoryIndexFileAfter)
        {
            throw new InvalidDataException(
                "The exact staged work-memory index changed before publication.");
        }
        AliWorkMemoryWindowsFileIdentity.RequireTreeClosureUnchanged(
            stagingClosure,
            currentStagingPath);
    }

    private async Task RecordPublicationFailureAsync(
        string planId,
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding,
        AliWorkMemoryClassification classification)
    {
        try
        {
            if (classification == AliWorkMemoryClassification.Absent)
            {
                await _invocations.FailAsync(
                        planId,
                        expectedRevision: 1,
                        "work-memory-publication-compensated",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                TryCleanupTerminalStaging(domain, AliWorkMemoryClassification.Absent);
                TryCleanupEmptyBackupParent(domain, executionBinding);
            }
            else if (classification == AliWorkMemoryClassification.Unknown)
            {
                await _invocations.MarkInDoubtAsync(
                        planId,
                        expectedRevision: 1,
                        "work-memory-publication-state-ambiguous",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            // The authenticated filesystem classification remains available to a started receipt.
        }
    }

    private async Task TryCleanupTerminalStagingAsync(
        string preparationIdentity,
        AliWorkMemoryClassification classification)
    {
        try
        {
            var invocation = await _invocations.LoadAsync(
                    preparationIdentity,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var domain = await _domainPlans.ReadAsync(
                    invocation.Plan.DomainPreparationIdentity,
                    invocation.Plan.DomainPreparationDigest,
                    CancellationToken.None)
                .ConfigureAwait(false);
            TryCleanupTerminalStaging(domain, classification);
            if (invocation.Receipt is { } receipt)
            {
                var executionBinding = _executionBindings.TryRead(
                    invocation.Plan.Id,
                    domain.DomainId,
                    invocation.Plan.DomainPreparationDigest,
                    receipt.AuthorizationDigest);
                if (executionBinding is not null)
                {
                    AliAgentWorkMemoryExecutionCoordinator.TryCleanupEmptyBackupParent(
                        domain,
                        executionBinding);
                }
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            // Cleanup is conservative and best-effort after the terminal receipt is durable.
        }
    }

    private static void TryDeletePreparedTransaction(
        IReadOnlyList<AliWorkMemoryNamespaceBinding> stagingRootSpine,
        string domainId,
        string? expectedIdentity)
    {
        if (expectedIdentity is null)
        {
            return;
        }
        try
        {
            using var stagingRoot = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                stagingRootSpine);
            using var transaction = AliWorkMemoryWindowsFileIdentity.TryOpenBoundChildDirectory(
                stagingRoot,
                domainId,
                expectedIdentity);
            if (transaction is not null)
            {
                AliWorkMemoryWindowsFileIdentity.DeleteBoundDirectoryTree(transaction);
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            // Unrecognized or raced residue is intentionally retained for bounded admission.
        }
    }

    private void TryCleanupTerminalStaging(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryClassification classification)
    {
        try
        {
            var expectedStaging = Path.Combine(
                _stagingRoot,
                domain.DomainId,
                "workspace");
            if (!string.Equals(
                    NormalizePhysical(domain.StagingWorkspacePath),
                    NormalizePhysical(expectedStaging),
                    StringComparison.Ordinal))
            {
                return;
            }

            if (classification is not (
                    AliWorkMemoryClassification.Applied
                    or AliWorkMemoryClassification.Absent))
            {
                return;
            }
            using (var transaction = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                       domain.StagingContainerSpine))
            {
                using var staging = AliWorkMemoryWindowsFileIdentity.TryOpenBoundChildDirectory(
                    transaction,
                    Path.GetFileName(expectedStaging),
                    domain.StagingWorkspaceIdentity);
                if (classification == AliWorkMemoryClassification.Applied)
                {
                    if (staging is not null)
                    {
                        return;
                    }
                }
                else if (staging is not null)
                {
                    var snapshot = AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                        staging,
                        expectedStaging);
                    var authenticatedPrestage = domain.WorkspaceBefore.Exists
                        ? domain.WorkspaceBefore
                        : AliFileTreeSnapshotter.EmptyDirectory;
                    if (snapshot != authenticatedPrestage
                        && snapshot != domain.WorkspaceAfter)
                    {
                        return;
                    }
                    AliWorkMemoryWindowsFileIdentity.DeleteBoundDirectoryTree(staging);
                }
                using var emptySeed = AliWorkMemoryWindowsFileIdentity
                    .TryOpenBoundChildDirectory(
                        transaction,
                        Path.GetFileName(domain.EmptyBackupSeedPath),
                        domain.EmptyBackupSeedIdentity);
                if (emptySeed is not null)
                {
                    if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                            emptySeed,
                            domain.EmptyBackupSeedPath)
                        != AliFileTreeSnapshotter.EmptyDirectory)
                    {
                        return;
                    }
                    AliWorkMemoryWindowsFileIdentity.DeleteEmptyBoundDirectory(emptySeed);
                }
                using var staleParentSeed = AliWorkMemoryWindowsFileIdentity
                    .TryOpenBoundChildDirectory(
                        transaction,
                        Path.GetFileName(domain.BackupParentSeedPath),
                        domain.BackupParentSeedIdentity);
                if (staleParentSeed is not null)
                {
                    if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                            staleParentSeed,
                            domain.BackupParentSeedPath)
                        != AliFileTreeSnapshotter.EmptyDirectory)
                    {
                        return;
                    }
                    AliWorkMemoryWindowsFileIdentity.DeleteEmptyBoundDirectory(
                        staleParentSeed);
                }
            }

            var transactionParentBindings = domain.StagingContainerSpine
                .Take(domain.StagingContainerSpine.Count - 1)
                .ToArray();
            using var transactionParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                transactionParentBindings);
            using var transactionObject = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                transactionParent,
                domain.DomainId,
                domain.StagingContainerSpine[^1].Identity);
            AliWorkMemoryWindowsFileIdentity.DeleteEmptyBoundDirectory(transactionObject);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            // Retain anything that cannot be re-authenticated and removed without following links.
        }
    }

    private static void TryCleanupEmptyBackupParent(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding)
    {
        try
        {
            using (var backupParent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
                       executionBinding.BackupParentSpine))
            {
                if (Directory.EnumerateFileSystemEntries(backupParent.ParentPath).Any())
                {
                    return;
                }
            }
            var parentBindings = executionBinding.BackupParentSpine
                .Take(executionBinding.BackupParentSpine.Count - 1)
                .ToArray();
            using var parent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(parentBindings);
            using var transaction = AliWorkMemoryWindowsFileIdentity.OpenBoundChildDirectory(
                parent,
                domain.DomainId,
                executionBinding.BackupParentSpine[^1].Identity);
            if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    transaction,
                    transaction.Path)
                != AliFileTreeSnapshotter.EmptyDirectory)
            {
                return;
            }
            AliWorkMemoryWindowsFileIdentity.DeleteEmptyBoundDirectory(transaction);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            // Any non-empty, rebound, or concurrently changed transaction is retained intact.
        }
    }

    private async Task<CommittedEvidenceReference> AppendEvidenceAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        var invocation = await _invocations.LoadAsync(
                intent.PreparationIdentity!,
                cancellationToken)
            .ConfigureAwait(false);
        var domain = await _domainPlans.ReadAsync(
                invocation.Plan.DomainPreparationIdentity,
                invocation.Plan.DomainPreparationDigest,
                cancellationToken)
            .ConfigureAwait(false);
        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(
                    "ali-work-memory-reconciliation-evidence-v1\0"
                    + identity.StorageKey + "\0" + intent.IdempotencyKey),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = intent.ToolName,
                CapabilityGroup = "work-memory",
                ProviderId = "ali-agent-framework-file-memory",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = domain.Mutation switch
                {
                    AliWorkMemoryMutation.Write when !domain.MainFileBefore.Exists => "create",
                    AliWorkMemoryMutation.Write
                        or AliWorkMemoryMutation.Replace
                        or AliWorkMemoryMutation.ReplaceLines => "update",
                    AliWorkMemoryMutation.Delete => "delete",
                    _ => throw new ArgumentOutOfRangeException(nameof(domain.Mutation))
                },
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    intent.CanonicalArgumentsDigest,
                    intent.PreparationIdentity
                }),
                Result = JsonSerializer.SerializeToElement(new { outcomeCode }),
                NormalizedTarget = JsonSerializer.SerializeToElement(new
                {
                    domain.Scope.UserStableId,
                    domain.Scope.ConversationId,
                    domain.FileName,
                    intent.TargetVersionDigest
                }),
                NormalizedEffectResult = JsonSerializer.SerializeToElement(new
                {
                    outcomeCode,
                    domain.MainFileAfter
                }),
                Outcome = ToolInvocationOutcome.Returned(resultBytes, reportedSuccess: true),
                StableOutcomeCode = outcomeCode,
                StartedAtUtc = invocation.Receipt?.StartedAtUtc ?? invocation.Plan.CreatedAtUtc,
                CompletedAtUtc = invocation.Receipt?.TerminalAtUtc
                    ?? invocation.Receipt?.StartedAtUtc
                    ?? invocation.Plan.CreatedAtUtc,
                Artifacts =
                [
                    new EvidenceArtifactDraft(
                        domain.FileName,
                        "file",
                        domain.MainFileBefore.Exists ? domain.MainFileBefore.Digest : null,
                        domain.MainFileAfter.Exists ? domain.MainFileAfter.Digest : null)
                ],
                Permission = new EvidencePermissionMetadata("unknown", "unknown"),
                ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new
                {
                    intent.PermissionReceiptDigest,
                    intent.RequiresApproval
                }),
                Source = new EvidenceSourceMetadata(
                    "file",
                    "ali-agent-framework-file-memory",
                    "trusted-local",
                    FreshAtUtc: null,
                    intent.RegistryRevisionDigest),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    reconciler = intent.ReconcilerId,
                    domain.DomainId,
                    replayedEffect = false
                })
            };
            await _evidence.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _evidence.AppendAsync(identity, draft, cancellationToken)
                .ConfigureAwait(false);
            return new CommittedEvidenceReference(
                committed.Evidence.EvidenceId,
                committed.Cursor,
                committed.Checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private void RequireActiveScope(AliWorkMemoryDomainPlan domain)
    {
        var current = _scopeAccessor()
            ?? throw new InvalidOperationException(
                "The exact work-memory invocation lost its active conversation scope.");
        if (current != domain.Scope)
        {
            throw new InvalidOperationException(
                "The exact work-memory invocation scope does not match its prepared user and conversation.");
        }
    }

    private static void RequireCapturedTargetStillCurrent(
        AliWorkMemoryCapturedTarget captured)
    {
        if (AliFileTreeSnapshotter.CaptureStable(captured.WorkspacePath)
            != captured.WorkspaceBefore)
        {
            throw new AliExecutionPreparationException(
                "The exact work-memory workspace changed during preparation.");
        }
    }

    private static void RequireCapturedWorkspaceIdentity(
        AliWorkMemoryCapturedTarget captured,
        IReadOnlyList<AliWorkMemoryNamespaceBinding> canonicalParentSpine,
        string? canonicalWorkspaceIdentity)
    {
        using var parent = AliWorkMemoryWindowsFileIdentity.OpenDirectorySpine(
            canonicalParentSpine);
        using var workspace = AliWorkMemoryWindowsFileIdentity
            .TryOpenBoundChildDirectory(
                parent,
                Path.GetFileName(captured.WorkspacePath),
                canonicalWorkspaceIdentity);
        if (!captured.WorkspaceBefore.Exists)
        {
            if (workspace is not null)
            {
                throw new AliExecutionPreparationException(
                    "The exact work-memory workspace appeared during preparation.");
            }
            return;
        }
        if (workspace is null
            || canonicalWorkspaceIdentity is null
            || AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                workspace,
                captured.WorkspacePath) != captured.WorkspaceBefore)
        {
            throw new AliExecutionPreparationException(
                "The exact work-memory workspace identity changed during preparation.");
        }
    }

    private void RequireStagingAdmission()
    {
        var immediate = Directory.EnumerateFileSystemEntries(_stagingRoot)
            .Take(MaximumUnresolvedStagingTransactions + 1)
            .ToArray();
        if (immediate.Length > MaximumUnresolvedStagingTransactions)
        {
            throw new IOException(
                $"Work-memory staging admission rejected unresolved residue: transactions={immediate.Length}, limit={MaximumUnresolvedStagingTransactions}.");
        }

        var pending = new Stack<string>();
        pending.Push(_stagingRoot);
        var entries = 0;
        long bytes = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "Work-memory staging admission encountered a non-regular directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumUnresolvedStagingEntries)
                {
                    throw new IOException(
                        $"Work-memory staging admission rejected unresolved residue: entries>{MaximumUnresolvedStagingEntries}, bytes={bytes}.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException(
                        $"Work-memory staging admission rejected non-regular residue after entries={entries}, bytes={bytes}.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    "Work-memory staging admission encountered a non-regular file.");
                bytes = checked(bytes + stream.Length);
                if (bytes > MaximumUnresolvedStagingBytes)
                {
                    throw new IOException(
                        $"Work-memory staging admission rejected unresolved residue: entries={entries}, bytes={bytes}, byteLimit={MaximumUnresolvedStagingBytes}.");
                }
            }
        }
    }

    internal static void RequireStagedWorkspaceIdentities(
        string workspace,
        string mainFileName,
        string descriptionFileName)
    {
        AliWorkMemoryWindowsFileIdentity.RequireLiteralSingleLinkTree(
            workspace,
            "The work-memory staging tree contains an alias or multiply linked file.");
        _ = AliWorkMemoryWindowsFileIdentity.RequireOptionalLiteralSingleLinkFile(
            Path.Combine(workspace, mainFileName),
            mainFileName,
            "The work-memory staged main file is an alias or multiply linked file.");
        _ = AliWorkMemoryWindowsFileIdentity.RequireOptionalLiteralSingleLinkFile(
            Path.Combine(workspace, descriptionFileName),
            descriptionFileName,
            "The work-memory staged description file is an alias or multiply linked file.");
        _ = AliWorkMemoryWindowsFileIdentity.RequireOptionalLiteralSingleLinkFile(
            Path.Combine(
                workspace,
                AliExactAgentWorkMemoryInvocationStore.MemoryIndexFileName),
            AliExactAgentWorkMemoryInvocationStore.MemoryIndexFileName,
            "The work-memory staged index file is an alias or multiply linked file.");
    }

    private static void StageWorkspace(
        string workspace,
        string staging,
        AliFileTreeItemSnapshot before,
        CancellationToken cancellationToken,
        Action<AliWorkMemoryPreparationCheckpoint>? preparationFaultHook)
    {
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            staging,
            "The work-memory staging path is not a regular local directory.");
        if (!before.Exists)
        {
            return;
        }
        CopyDirectoryContents(
            workspace,
            staging,
            cancellationToken,
            preparationFaultHook);
        if (AliFileTreeSnapshotter.CaptureStable(staging) != before)
        {
            throw new IOException(
                "The exact work-memory staging copy does not match its preimage.");
        }
    }

    private static async Task<ExpectedWorkspaceSnapshots> BuildExpectedWorkspaceAsync(
        string stagedPreimage,
        string expectedWorkspace,
        AliFileTreeItemSnapshot workspaceBefore,
        AliWorkMemoryMutation mutation,
        string fileName,
        string descriptionFileName,
        string? mainAfterContent,
        string? descriptionAfterContent,
        CancellationToken cancellationToken)
    {
        StageWorkspace(
            stagedPreimage,
            expectedWorkspace,
            workspaceBefore,
            cancellationToken,
            preparationFaultHook: null);
        RequireStagedWorkspaceIdentities(
            expectedWorkspace,
            fileName,
            descriptionFileName);
        var store = new FileSystemAgentFileStore(expectedWorkspace);
        if (mutation == AliWorkMemoryMutation.Delete)
        {
            if (!await store.DeleteAsync(fileName, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException(
                    "The expected work-memory workspace did not contain its delete target.");
            }
            _ = await store.DeleteAsync(descriptionFileName, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await store.WriteAsync(
                    fileName,
                    mainAfterContent
                        ?? throw new InvalidDataException(
                            "The expected work-memory workspace has no main-file postimage."),
                    cancellationToken)
                .ConfigureAwait(false);
            if (mutation == AliWorkMemoryMutation.Write)
            {
                if (descriptionAfterContent is null)
                {
                    _ = await store.DeleteAsync(descriptionFileName, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await store.WriteAsync(
                            descriptionFileName,
                            descriptionAfterContent,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (mutation is AliWorkMemoryMutation.Write or AliWorkMemoryMutation.Delete)
        {
            var memoryIndex = await AliExactAgentWorkMemoryInvocationStore
                .BuildExpectedIndexAsync(store, cancellationToken)
                .ConfigureAwait(false);
            await store.WriteAsync(
                    AliExactAgentWorkMemoryInvocationStore.MemoryIndexFileName,
                    memoryIndex,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        RequireStagedWorkspaceIdentities(
            expectedWorkspace,
            fileName,
            descriptionFileName);

        return new ExpectedWorkspaceSnapshots(
            AliFileTreeSnapshotter.CaptureStable(expectedWorkspace),
            AliFileTreeSnapshotter.CaptureStable(Path.Combine(
                expectedWorkspace,
                AliExactAgentWorkMemoryInvocationStore.MemoryIndexFileName)));
    }

    private static void CopyDirectoryContents(
        string source,
        string destination,
        CancellationToken cancellationToken,
        Action<AliWorkMemoryPreparationCheckpoint>? preparationFaultHook)
    {
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            source,
            "The work-memory source contains a reparse point or non-regular directory.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)
                     .OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "The work-memory source contains a reparse point or device entry.");
            }
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                    target,
                    "The work-memory staging target is not a regular local directory.");
                preparationFaultHook?.Invoke(
                    AliWorkMemoryPreparationCheckpoint.StagingEntryCopied);
                CopyDirectoryContents(
                    entry,
                    target,
                    cancellationToken,
                    preparationFaultHook);
                continue;
            }
            using var input = WindowsOrchestrationFileBoundary.OpenRegularFile(
                entry,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                writeThrough: false,
                "The work-memory source contains a non-regular file.");
            AliWorkMemoryWindowsFileIdentity.RequireOpenedLiteralSingleLinkFile(
                input,
                entry,
                Path.GetFileName(entry),
                "The work-memory source contains an aliased or multiply linked file.");
            using var output = WindowsOrchestrationFileBoundary.OpenRegularFile(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                writeThrough: true,
                "The work-memory staging target is not a regular file.");
            const int copyBufferSize = 128 * 1024;
            var buffer = ArrayPool<byte>.Shared.Rent(copyBufferSize);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, copyBufferSize);
                    if (read == 0)
                    {
                        break;
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
            output.Flush(flushToDisk: true);
            AliWorkMemoryWindowsFileIdentity.RequireOpenedLiteralSingleLinkFile(
                output,
                target,
                Path.GetFileName(target),
                "The work-memory staging target contains an aliased or multiply linked file.");
            preparationFaultHook?.Invoke(
                AliWorkMemoryPreparationCheckpoint.StagingEntryCopied);
        }
    }

    internal static AliFileTreeItemSnapshot SnapshotForText(string content)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = Encoding.UTF8.GetBytes(content);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(preamble);
            hash.AppendData(bytes);
            return new AliFileTreeItemSnapshot(
                "file",
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preamble);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string RootBinding(AliWorkMemoryDomainPlan domain) =>
        WorkIdentityCanonicalizer.MapDigest(
            "work-memory-root-binding-v1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workspace"] = NormalizePhysical(domain.CanonicalWorkspacePath),
                ["staging"] = NormalizePhysical(domain.StagingWorkspacePath),
                ["backup"] = NormalizePhysical(domain.BackupWorkspacePath)
            });

    private static string NormalizePhysical(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static AliExactExecutionAdapterIdentity Identity(string toolName) =>
        new(toolName, CapabilityIdFor(toolName), ReconcilerIdFor(toolName));

    internal static string CapabilityIdFor(string toolName) => "ali.tool." + toolName;

    internal static string ReconcilerIdFor(string toolName) => "ali.reconcile." + toolName;

    private static string ResultDigest(object? result)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object)));
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var filtered = new string(exception.GetType().Name
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered)
            ? "failed"
            : filtered[..Math.Min(filtered.Length, 72)];
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class AliWorkMemoryExecutionLease(
        AliWorkMemoryDirectorySpine canonicalParent,
        AliWorkMemoryDirectorySpine stagingContainer,
        AliWorkMemoryDirectorySpine backupParent,
        AliWorkMemoryBoundDirectory staging,
        AliWorkMemoryBoundDirectory? canonical,
        AliWorkMemoryBoundDirectory? emptyBackupSeed) : IDisposable
    {
        private int _disposed;
        private AliWorkMemoryBoundDirectory _staging = staging;
        private AliWorkMemoryBoundDirectory? _canonical = canonical;
        private AliWorkMemoryBoundDirectory? _emptyBackupSeed = emptyBackupSeed;

        internal AliWorkMemoryDirectorySpine CanonicalParent { get; } = canonicalParent;

        internal AliWorkMemoryDirectorySpine StagingContainer { get; } = stagingContainer;

        internal AliWorkMemoryDirectorySpine BackupParent { get; } = backupParent;

        internal AliWorkMemoryBoundDirectory Staging => _staging;

        internal AliWorkMemoryBoundDirectory? Canonical => _canonical;

        internal AliWorkMemoryBoundDirectory? EmptyBackupSeed => _emptyBackupSeed;

        internal AliWorkMemoryTreeClosure? StagingClosure { get; private set; }

        internal AliWorkMemoryTreeClosure? CanonicalClosure { get; private set; }

        internal AliWorkMemoryTreeClosure? EmptyBackupSeedClosure { get; private set; }

        internal void SealForPublication(AliWorkMemoryDomainPlan domain)
        {
            ArgumentNullException.ThrowIfNull(domain);
            if (StagingClosure is not null)
            {
                return;
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AliWorkMemoryExecutionLease));
            }

            var stagingIdentity = _staging.Identity;
            _staging.Dispose();
            _staging = AliWorkMemoryWindowsFileIdentity.OpenSealedBoundChildDirectory(
                StagingContainer,
                Path.GetFileName(domain.StagingWorkspacePath),
                stagingIdentity);
            if (_canonical is not null)
            {
                var canonicalIdentity = _canonical.Identity;
                _canonical.Dispose();
                _canonical = AliWorkMemoryWindowsFileIdentity.OpenSealedBoundChildDirectory(
                    CanonicalParent,
                    Path.GetFileName(domain.CanonicalWorkspacePath),
                    canonicalIdentity);
            }
            if (_emptyBackupSeed is not null)
            {
                var emptySeedIdentity = _emptyBackupSeed.Identity;
                _emptyBackupSeed.Dispose();
                _emptyBackupSeed = AliWorkMemoryWindowsFileIdentity
                    .OpenSealedBoundChildDirectory(
                        StagingContainer,
                        Path.GetFileName(domain.EmptyBackupSeedPath),
                        emptySeedIdentity);
            }
            AliWorkMemoryTreeClosure? stagedClosure = null;
            AliWorkMemoryTreeClosure? canonicalClosure = null;
            AliWorkMemoryTreeClosure? emptySeedClosure = null;
            try
            {
                stagedClosure = AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
                    _staging,
                    domain.StagingWorkspacePath);
                if (Canonical is not null)
                {
                    canonicalClosure = AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
                        Canonical,
                        domain.CanonicalWorkspacePath);
                }
                if (EmptyBackupSeed is not null)
                {
                    emptySeedClosure = AliWorkMemoryWindowsFileIdentity.CaptureBoundTreeClosure(
                        EmptyBackupSeed,
                        domain.EmptyBackupSeedPath);
                }
                StagingClosure = stagedClosure;
                CanonicalClosure = canonicalClosure;
                EmptyBackupSeedClosure = emptySeedClosure;
            }
            catch
            {
                emptySeedClosure?.Dispose();
                canonicalClosure?.Dispose();
                stagedClosure?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            EmptyBackupSeedClosure?.Dispose();
            CanonicalClosure?.Dispose();
            StagingClosure?.Dispose();
            _emptyBackupSeed?.Dispose();
            _canonical?.Dispose();
            _staging.Dispose();
            BackupParent.Dispose();
            StagingContainer.Dispose();
            CanonicalParent.Dispose();
        }
    }

    private sealed record ActiveInvocation(
        AliWorkMemoryDomainPlan Domain,
        AliWorkMemoryExecutionBinding ExecutionBinding,
        AliWorkMemoryExecutionLease ExecutionLease,
        AgentFileStore Store);

    private sealed record ExpectedWorkspaceSnapshots(
        AliFileTreeItemSnapshot WorkspaceAfter,
        AliFileTreeItemSnapshot MemoryIndexFileAfter);

    private sealed class AliWorkMemoryObservedDirectory(
        AliWorkMemoryBoundDirectory directory,
        AliWorkMemoryTreeClosure closure,
        AliFileTreeItemSnapshot snapshot) : IDisposable
    {
        internal string Identity => directory.Identity;

        internal AliFileTreeItemSnapshot Snapshot { get; } = snapshot;

        internal void RequireUnchanged()
        {
            if (AliWorkMemoryWindowsFileIdentity.CaptureBoundSnapshot(
                    closure,
                    directory.Path) != Snapshot)
            {
                throw new IOException(
                    "The exact observed work-memory tree changed before classification.");
            }
        }

        public void Dispose()
        {
            closure.Dispose();
            directory.Dispose();
        }
    }

    private enum AliWorkMemoryClassification
    {
        Applied,
        Absent,
        Unknown
    }

    private sealed class CompletionParticipant(
        AliAgentWorkMemoryExecutionCoordinator owner,
        string planId,
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryExecutionBinding executionBinding,
        AliWorkMemoryExecutionLease executionLease) : IAliInvocationCompletionParticipant
    {
        public async ValueTask CompleteAsync(
            object? result,
            CancellationToken cancellationToken)
        {
            try
            {
                await owner.CommitAsync(
                        planId,
                        domain,
                        executionBinding,
                        executionLease,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                executionLease.Dispose();
                owner._active.TryRemove(planId, out _);
            }
        }

        public async ValueTask FailAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exception);
            try
            {
                executionLease.Dispose();
                if (owner.Classify(domain, executionBinding)
                    == AliWorkMemoryClassification.Absent)
                {
                    await owner._invocations.FailAsync(
                            planId,
                            expectedRevision: 1,
                            "work-memory-inner-invocation-failed",
                            cancellationToken)
                        .ConfigureAwait(false);
                    owner.TryCleanupTerminalStaging(
                        domain,
                        AliWorkMemoryClassification.Absent);
                    AliAgentWorkMemoryExecutionCoordinator.TryCleanupEmptyBackupParent(
                        domain,
                        executionBinding);
                }
                else
                {
                    await owner._invocations.MarkInDoubtAsync(
                            planId,
                            expectedRevision: 1,
                            "work-memory-failure-state-ambiguous",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                owner._canonicalStore.ReportExactDurableOutcome(
                    domain.ToolName,
                    AliFrameworkToolOutcomeSignal.Failed);
                await owner._canonicalStore.AppendExactDurableAuditAsync(
                        domain.Scope,
                        domain.Mutation.ToString().ToLowerInvariant(),
                        domain.FileName,
                        succeeded: false,
                        exception.GetType().Name,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                executionLease.Dispose();
                owner._active.TryRemove(planId, out _);
            }
        }

        public async ValueTask MarkInDoubtAsync(
            string reasonCode,
            CancellationToken cancellationToken)
        {
            try
            {
                executionLease.Dispose();
                await owner._invocations.MarkInDoubtAsync(
                        planId,
                        expectedRevision: 1,
                        reasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                executionLease.Dispose();
                owner._active.TryRemove(planId, out _);
            }
        }
    }

    private sealed class StartedDomainReconciler(
        AliAgentWorkMemoryExecutionCoordinator owner,
        AliExactExecutionAdapterIdentity exactIdentity) : IAliStartedInvocationDomainReconciler
    {
        public AliExactExecutionAdapterIdentity ExactIdentity { get; } = exactIdentity;

        public async ValueTask<AliDurableInvocationRecoveryResult> ReconcileStartedAsync(
            AliDurableInvocationPlan plan,
            AliDurableInvocationReceipt startedReceipt,
            CancellationToken cancellationToken)
        {
            var domain = await owner._domainPlans.ReadAsync(
                    plan.DomainPreparationIdentity,
                    plan.DomainPreparationDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            var executionBinding = owner._executionBindings.TryRead(
                plan.Id,
                domain.DomainId,
                plan.DomainPreparationDigest,
                startedReceipt.AuthorizationDigest);
            if (executionBinding is null)
            {
                return AliDurableInvocationRecoveryResult.Unknown(
                    "work-memory-started-binding-missing");
            }
            AliAgentWorkMemoryExecutionCoordinator.RequireExecutionBinding(
                domain,
                plan,
                executionBinding,
                startedReceipt.AuthorizationDigest);
            return owner.RecoverPublication(domain, executionBinding) switch
            {
                AliWorkMemoryClassification.Applied =>
                    AliDurableInvocationRecoveryResult.Applied(
                        "work-memory-post-state-proved-applied"),
                AliWorkMemoryClassification.Absent =>
                    AliDurableInvocationRecoveryResult.Absent(
                        "work-memory-pre-state-proved-absent"),
                _ => AliDurableInvocationRecoveryResult.Unknown(
                    "work-memory-state-ambiguous")
            };
        }
    }
}

/// <summary>
/// Restricts Agent Framework's multi-call file-memory facade to the concrete mutation that
/// owns the current durable grant. Framework-maintained description and index files are
/// permitted only where that concrete provider operation requires them.
/// </summary>
internal sealed class AliExactAgentWorkMemoryInvocationStore : AgentFileStore
{
    internal const string MemoryIndexFileName = "memories.md";
    private readonly AliWorkMemoryDomainPlan _domain;
    private readonly AliWorkMemoryBoundDirectory _staging;
    private readonly Action<AliWorkMemoryPublicationCheckpoint>? _publicationFaultHook;

    internal AliExactAgentWorkMemoryInvocationStore(
        AliWorkMemoryDomainPlan domain,
        AliWorkMemoryBoundDirectory staging,
        Action<AliWorkMemoryPublicationCheckpoint>? publicationFaultHook)
    {
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _publicationFaultHook = publicationFaultHook;
        if (!string.Equals(
                Path.GetFullPath(staging.Path),
                Path.GetFullPath(domain.StagingWorkspacePath),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                staging.Identity,
                domain.StagingWorkspaceIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact Framework store is not backed by its authenticated staging handle.");
        }
    }

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (string.Equals(normalized, _domain.FileName, StringComparison.Ordinal))
        {
            if (_domain.Mutation == AliWorkMemoryMutation.Delete
                || AliAgentWorkMemoryExecutionCoordinator.SnapshotForText(content)
                    != _domain.MainFileAfter)
            {
                throw new InvalidDataException(
                    "The Framework attempted a main-file write outside the exact work-memory postimage.");
            }
        }
        else if (string.Equals(
                     normalized,
                     _domain.DescriptionFileName,
                     StringComparison.Ordinal))
        {
            if (_domain.Mutation != AliWorkMemoryMutation.Write
                || !_domain.DescriptionFileAfter.Exists
                || AliAgentWorkMemoryExecutionCoordinator.SnapshotForText(content)
                    != _domain.DescriptionFileAfter)
            {
                throw new InvalidDataException(
                    "The Framework attempted a description write outside the exact work-memory postimage.");
            }
        }
        else if (string.Equals(normalized, MemoryIndexFileName, StringComparison.Ordinal))
        {
            if (_domain.Mutation is not (
                    AliWorkMemoryMutation.Write or AliWorkMemoryMutation.Delete))
            {
                throw new InvalidDataException(
                    "This exact work-memory mutation does not own the Framework memory index.");
            }
            var expected = await BuildExpectedIndexAsync(this, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(content, expected, StringComparison.Ordinal)
                || AliAgentWorkMemoryExecutionCoordinator.SnapshotForText(content)
                    != _domain.MemoryIndexFileAfter)
            {
                throw new InvalidDataException(
                    "The Framework memory-index write does not match its authenticated staged state.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                "The Framework attempted a write outside the exact work-memory mutation boundary.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        AliWorkMemoryWindowsFileIdentity.WriteBoundText(
            _staging,
            normalized,
            content,
            () => _publicationFaultHook?.Invoke(
                AliWorkMemoryPublicationCheckpoint.BeforeStagedFileWriteSwap));
    }

    public override Task<string?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AliWorkMemoryWindowsFileIdentity.ReadOptionalBoundText(
            _staging,
            Normalize(path)));
    }

    public override Task<bool> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        var mainDelete = _domain.Mutation == AliWorkMemoryMutation.Delete
            && string.Equals(normalized, _domain.FileName, StringComparison.Ordinal);
        var descriptionDelete = _domain.Mutation is (
                AliWorkMemoryMutation.Write or AliWorkMemoryMutation.Delete)
            && string.Equals(
                normalized,
                _domain.DescriptionFileName,
                StringComparison.Ordinal);
        if (!mainDelete && !descriptionDelete)
        {
            throw new InvalidOperationException(
                "The Framework attempted a delete outside the exact work-memory mutation boundary.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AliWorkMemoryWindowsFileIdentity.DeleteBoundFile(
            _staging,
            normalized,
            () => _publicationFaultHook?.Invoke(
                AliWorkMemoryPublicationCheckpoint.BeforeStagedFileDeleteDisposition)));
    }

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The exact work-memory store cannot enumerate nested directories.");
        }
        return Task.FromResult(AliWorkMemoryWindowsFileIdentity.ListBoundChildren(_staging));
    }

    public override Task<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var entry = AliWorkMemoryWindowsFileIdentity.TryOpenBoundChildEntry(
            _staging,
            Normalize(path),
            deleteAccess: false);
        if (entry is null)
        {
            return Task.FromResult(false);
        }
        entry.RequireFile();
        return Task.FromResult(true);
    }

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "The exact work-memory mutation boundary does not expose filesystem search.");
    }

    public override Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "The exact work-memory mutation cannot create nested directories.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal static async Task<string> BuildExpectedIndexAsync(
        AgentFileStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var children = await store.ListChildrenAsync(string.Empty, cancellationToken)
            .ConfigureAwait(false);
        var files = children
            .Where(entry => string.Equals(
                entry.Type,
                FileStoreEntry.File,
                StringComparison.Ordinal))
            .Select(entry => entry.Name)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# Memory Index");
        builder.AppendLine();
        var count = 0;
        foreach (var file in files)
        {
            if (IsInternalFile(file))
            {
                continue;
            }
            if (count >= 50)
            {
                break;
            }
            var description = await store.ReadAsync(
                    AliExactWorkMemoryArguments.DescriptionFileName(file),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.AppendLine($"- **{file}**: {description}");
            }
            else
            {
                builder.AppendLine($"- **{file}**");
            }
            count++;
        }
        return builder.ToString();
    }

    private static bool IsInternalFile(string fileName) =>
        fileName.EndsWith("_description.md", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, MemoryIndexFileName, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return AliExactWorkMemoryArguments.RequireSafeFlatFileName(
            path,
            allowExactFrameworkReservedName: true);
    }
}

internal sealed class AliBrokeredAgentWorkMemoryStore(
    ScopedAgentWorkMemoryStore canonical,
    AliAgentWorkMemoryExecutionCoordinator coordinator) : AgentFileStore
{
    private readonly ScopedAgentWorkMemoryStore _canonical = canonical
        ?? throw new ArgumentNullException(nameof(canonical));
    private readonly AliAgentWorkMemoryExecutionCoordinator _coordinator = coordinator
        ?? throw new ArgumentNullException(nameof(coordinator));

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveMutationAsync(cancellationToken).ConfigureAwait(false);
        await store.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<string?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveReadAsync(cancellationToken).ConfigureAwait(false);
        return await store.ReadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveMutationAsync(cancellationToken).ConfigureAwait(false);
        return await store.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveReadAsync(cancellationToken).ConfigureAwait(false);
        return await store.ListChildrenAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveReadAsync(cancellationToken).ConfigureAwait(false);
        return await store.FileExistsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveReadAsync(cancellationToken).ConfigureAwait(false);
        return await store.SearchAsync(
                directory,
                regexPattern,
                globPattern,
                recursive,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = await ResolveMutationAsync(cancellationToken).ConfigureAwait(false);
        await store.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentFileStore> ResolveReadAsync(CancellationToken cancellationToken) =>
        await _coordinator.GetInvocationStoreAsync(cancellationToken).ConfigureAwait(false)
        ?? (AliExecutionGrantContext.HasCurrentActiveGrant
            ? throw new InvalidOperationException(
                "An active durable grant does not match an exact work-memory mutation adapter.")
            : _canonical);

    private async ValueTask<AgentFileStore> ResolveMutationAsync(
        CancellationToken cancellationToken) =>
        await _coordinator.GetInvocationStoreAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            AliExecutionGrantContext.HasCurrentActiveGrant
                ? "An active durable grant does not match an exact work-memory mutation adapter."
                : "A work-memory mutation requires an exact durable execution grant.");
}

internal abstract class AliExactWorkMemoryExecutionAdapter : IAliExecutionEffectAdapter
{
    protected AliExactWorkMemoryExecutionAdapter(
        string toolName,
        AliAgentWorkMemoryExecutionCoordinator coordinator)
    {
        ToolName = toolName;
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    protected AliAgentWorkMemoryExecutionCoordinator Coordinator { get; }

    public string ToolName { get; }

    public string CapabilityId =>
        AliAgentWorkMemoryExecutionCoordinator.CapabilityIdFor(ToolName);

    public string ReconcilerId =>
        AliAgentWorkMemoryExecutionCoordinator.ReconcilerIdFor(ToolName);

    public abstract ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken);

    public ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken) =>
        Coordinator.ReconcileAsync(
            AliAgentWorkMemoryExecutionCoordinator.Identity(ToolName),
            identity,
            intent,
            cancellationToken);
}

internal sealed class AliWorkMemoryWriteExecutionAdapter(
    AliAgentWorkMemoryExecutionCoordinator coordinator) :
    AliExactWorkMemoryExecutionAdapter(AliCapabilityCatalog.WorkMemoryWriteName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareWriteAsync(request, cancellationToken);
}

internal sealed class AliWorkMemoryReplaceExecutionAdapter(
    AliAgentWorkMemoryExecutionCoordinator coordinator) :
    AliExactWorkMemoryExecutionAdapter(AliCapabilityCatalog.WorkMemoryReplaceName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareReplaceAsync(request, cancellationToken);
}

internal sealed class AliWorkMemoryReplaceLinesExecutionAdapter(
    AliAgentWorkMemoryExecutionCoordinator coordinator) :
    AliExactWorkMemoryExecutionAdapter(
        AliCapabilityCatalog.WorkMemoryReplaceLinesName,
        coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareReplaceLinesAsync(request, cancellationToken);
}

internal sealed class AliWorkMemoryDeleteExecutionAdapter(
    AliAgentWorkMemoryExecutionCoordinator coordinator) :
    AliExactWorkMemoryExecutionAdapter(AliCapabilityCatalog.WorkMemoryDeleteName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareDeleteAsync(request, cancellationToken);
}
