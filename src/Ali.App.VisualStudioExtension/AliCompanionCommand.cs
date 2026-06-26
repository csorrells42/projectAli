using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace Ali.App.VisualStudioExtension;

internal sealed class AliCompanionCommand
{
    public const int OpenCommandId = 0x0100;
    public const int ReadActiveFileCommandId = 0x0101;
    public const int BuildActiveSolutionCommandId = 0x0102;
    public const int SearchSelectionCommandId = 0x0103;
    public const int PlanSelectionCommandId = 0x0104;
    public const int PreviewReplaceSelectionCommandId = 0x0105;
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
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, commandId)));
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
}

internal enum AliCompanionAction
{
    Open,
    ReadActiveFile,
    BuildActiveSolution,
    SearchSelection,
    PlanSelection,
    PreviewReplaceSelection
}
