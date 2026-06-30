using System.Text.Json;
using Ali.Core.Coding;

namespace Ali.Infrastructure.Coding;

public sealed record CodingToolSettings
{
    public string WorkspaceRoot { get; init; } = CodingWorkspacePolicy.CreateDefault().WorkspaceRoot;

    public string CurrentSolutionOrProjectPath { get; init; } = string.Empty;

    public string PdfWorkspaceRoot { get; init; } = string.Empty;

    public bool AllowExplicitOutsideFileOpen { get; init; } = true;

    public string WorkspaceAccessMode { get; init; } = CodingPermissionModes.Allowed;

    public string ExplicitOutsideFileOpenMode { get; init; } = CodingPermissionModes.Allowed;

    public string SearchOutsideWorkspaceMode { get; init; } = CodingPermissionModes.AskFirst;

    public string EditInsideWorkspaceMode { get; init; } = CodingPermissionModes.ConfirmEachTime;

    public string BuildTestRunInsideWorkspaceMode { get; init; } = CodingPermissionModes.ConfirmEachTime;

    public string DestructiveActionMode { get; init; } = CodingPermissionModes.ExtraConfirmation;

    public string OutsideEditRunMode { get; init; } = CodingPermissionModes.Blocked;

    public string SystemAdminActionMode { get; init; } = CodingPermissionModes.Blocked;

    public string GitReadMode { get; init; } = CodingPermissionModes.Allowed;

    public string GitWriteMode { get; init; } = CodingPermissionModes.ConfirmEachTime;

    public string GitMergeMode { get; init; } = CodingPermissionModes.ExtraConfirmation;

    public string GitNetworkMode { get; init; } = CodingPermissionModes.Blocked;

    public string PdfReadMode { get; init; } = CodingPermissionModes.Allowed;

    public string PdfCreateMode { get; init; } = CodingPermissionModes.Allowed;

    public string PdfModifyMode { get; init; } = CodingPermissionModes.ConfirmEachTime;

    public string NotepadPlusPlusPath { get; init; } = string.Empty;

    public string VisualStudioPath { get; init; } = string.Empty;

    public string ResolvePdfWorkspaceRoot(string dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(PdfWorkspaceRoot))
        {
            return Path.GetFullPath(PdfWorkspaceRoot.Trim().Trim('"'));
        }

        return Path.Combine(dataRoot, "GeneratedDocuments");
    }

    public CodingWorkspacePolicy ToPolicy() =>
        new(
            WorkspaceRoot,
            AllowExplicitOutsideFileOpen && !CodingPermissionModes.IsDisabled(ExplicitOutsideFileOpenMode),
            !CodingPermissionModes.IsDisabled(BuildTestRunInsideWorkspaceMode),
            !CodingPermissionModes.IsDisabled(EditInsideWorkspaceMode),
            !CodingPermissionModes.IsDisabled(GitReadMode),
            !CodingPermissionModes.IsDisabled(GitWriteMode),
            !CodingPermissionModes.IsDisabled(GitMergeMode),
            !CodingPermissionModes.IsDisabled(GitNetworkMode),
            !CodingPermissionModes.IsDisabled(PdfReadMode),
            !CodingPermissionModes.IsDisabled(PdfCreateMode),
            !CodingPermissionModes.IsDisabled(PdfModifyMode),
            !CodingPermissionModes.IsDisabled(OutsideEditRunMode),
            !CodingPermissionModes.IsDisabled(SystemAdminActionMode));
}

public static class CodingPermissionModes
{
    public const string Allowed = "Allowed";
    public const string AskFirst = "Ask first";
    public const string ConfirmEachTime = "Confirm each time";
    public const string ExtraConfirmation = "Extra confirmation";
    public const string Disabled = "Disabled";
    public const string Blocked = "Blocked";

    public static bool IsDisabled(string? mode) =>
        string.Equals(mode, Disabled, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Blocked, StringComparison.OrdinalIgnoreCase);
}

public static class CodingToolSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "Coding", "coding_tool_settings.json");

    public static CodingToolSettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return new CodingToolSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<CodingToolSettings>(stream, JsonOptions)
                   ?? new CodingToolSettings();
        }
        catch (JsonException)
        {
            return new CodingToolSettings();
        }
        catch (IOException)
        {
            return new CodingToolSettings();
        }
    }

    public static void Save(string dataRoot, CodingToolSettings settings)
    {
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
    }

    public static void WriteExample(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        Save(dataRoot, new CodingToolSettings());
    }
}
