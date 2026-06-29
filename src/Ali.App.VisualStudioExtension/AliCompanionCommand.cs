using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Ali.App.VisualStudioExtension;

internal sealed class AliCompanionCommand
{
    public const int OpenCommandId = 0x0100;
    public const int ReadActiveFileCommandId = 0x0101;
    public const int BuildActiveSolutionCommandId = 0x0102;
    public const int SearchSelectionCommandId = 0x0103;
    public const int PlanSelectionCommandId = 0x0104;
    public const int PreviewReplaceSelectionCommandId = 0x0105;
    public const int ReadSelectedNodeCommandId = 0x0106;
    public const int BuildSelectedNodeCommandId = 0x0107;
    public const int PlanSelectedNodeCommandId = 0x0108;
    public static readonly Guid CommandSet = new("1c60cf1b-8401-43c2-baf1-1636f492dd04");

    private readonly AliCompanionPackage _package;
    private readonly AliCompanionAction _action;

    private AliCompanionCommand(
        AliCompanionPackage package,
        OleMenuCommandService commandService,
        int commandId,
        AliCompanionAction action)
    {
        _package = package;
        _action = action;
        var command = new OleMenuCommand(Execute, new CommandID(CommandSet, commandId));
        command.BeforeQueryStatus += BeforeQueryStatus;
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AliCompanionPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
        {
            _ = new AliCompanionCommand(package, commandService, OpenCommandId, AliCompanionAction.Open);
            _ = new AliCompanionCommand(package, commandService, ReadActiveFileCommandId, AliCompanionAction.ReadActiveFile);
            _ = new AliCompanionCommand(package, commandService, BuildActiveSolutionCommandId, AliCompanionAction.BuildActiveSolution);
            _ = new AliCompanionCommand(package, commandService, SearchSelectionCommandId, AliCompanionAction.SearchSelection);
            _ = new AliCompanionCommand(package, commandService, PlanSelectionCommandId, AliCompanionAction.PlanSelection);
            _ = new AliCompanionCommand(package, commandService, PreviewReplaceSelectionCommandId, AliCompanionAction.PreviewReplaceSelection);
            _ = new AliCompanionCommand(package, commandService, ReadSelectedNodeCommandId, AliCompanionAction.ReadSelectedNode);
            _ = new AliCompanionCommand(package, commandService, BuildSelectedNodeCommandId, AliCompanionAction.BuildSelectedNode);
            _ = new AliCompanionCommand(package, commandService, PlanSelectedNodeCommandId, AliCompanionAction.PlanSelectedNode);
        }
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = _package.JoinableTaskFactory.RunAsync(
            async () =>
            {
                if (_action == AliCompanionAction.Open)
                {
                    await _package.ShowToolWindowAsync(_package.DisposalToken);
                    return;
                }

                await _package.StageToolWindowCommandAsync(_action, _package.DisposalToken);
            });
    }

    private void BeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is OleMenuCommand command)
        {
            command.Enabled = IsAvailable();
        }
    }

    private bool IsAvailable()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _action switch
        {
            AliCompanionAction.Open => true,
            AliCompanionAction.ReadActiveFile => HasActiveDocumentPath(),
            AliCompanionAction.BuildActiveSolution => HasSolutionPath(),
            AliCompanionAction.SearchSelection => HasEnabledSelection(),
            AliCompanionAction.PlanSelection => HasEnabledSelection(),
            AliCompanionAction.PreviewReplaceSelection => HasActiveDocumentPath() && HasEnabledSelection(),
            AliCompanionAction.ReadSelectedNode => HasSelectedFileNode(),
            AliCompanionAction.BuildSelectedNode => HasSelectedBuildNode(),
            AliCompanionAction.PlanSelectedNode => HasSelectedSolutionExplorerNode(),
            _ => true
        };
    }

    private bool HasActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return !string.IsNullOrWhiteSpace(GetDte()?.ActiveDocument?.FullName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasSolutionPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return !string.IsNullOrWhiteSpace(GetDte()?.Solution?.FullName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasEnabledSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!_package.GetOptionsPage().UseSelectedTextInCommands)
            {
                return false;
            }

            return GetDte()?.ActiveDocument?.Selection is TextSelection selection
                && !string.IsNullOrWhiteSpace(selection.Text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasSelectedFileNode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var projectItem = GetSelectedItem()?.ProjectItem;
            return projectItem is not null && projectItem.FileCount > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasSelectedBuildNode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = GetDte();
            var selected = GetSelectedItem();
            if (selected?.Project is Project project)
            {
                return !string.IsNullOrWhiteSpace(project.FullName);
            }

            return selected?.ProjectItem is null
                && !string.IsNullOrWhiteSpace(dte?.Solution?.FullName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasSelectedSolutionExplorerNode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = GetDte();
            var selected = GetSelectedItem();
            return selected?.ProjectItem is not null
                || selected?.Project is not null
                || !string.IsNullOrWhiteSpace(dte?.Solution?.FullName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DTE2? GetDte()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Package.GetGlobalService(typeof(SDTE)) as DTE2;
    }

    private static SelectedItem? GetSelectedItem()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var selectedItems = GetDte()?.SelectedItems;
        return selectedItems is not null && selectedItems.Count > 0
            ? selectedItems.Item(1)
            : null;
    }
}

internal enum AliCompanionAction
{
    Open,
    ReadActiveFile,
    BuildActiveSolution,
    SearchSelection,
    PlanSelection,
    PreviewReplaceSelection,
    ReadSelectedNode,
    BuildSelectedNode,
    PlanSelectedNode
}
