using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace Ali.App.VisualStudioExtension;

internal sealed class AliCompanionCommand
{
    public const int CommandId = 0x0100;
    public static readonly Guid CommandSet = new("1c60cf1b-8401-43c2-baf1-1636f492dd04");

    private readonly AliCompanionPackage _package;

    private AliCompanionCommand(AliCompanionPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        var commandId = new CommandID(CommandSet, CommandId);
        commandService.AddCommand(new MenuCommand(Execute, commandId));
    }

    public static async Task InitializeAsync(AliCompanionPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
        {
            _ = new AliCompanionCommand(package, commandService);
        }
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = _package.JoinableTaskFactory.RunAsync(
            async () => await _package.ShowToolWindowAsync(_package.DisposalToken));
    }
}
