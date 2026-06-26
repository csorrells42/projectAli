using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Ali.App.VisualStudioExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Ali Companion", "Visual Studio tool window for Ali's local programming companion.", "0.10.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(AliCompanionOptionsPage), "Ali", "Companion", 0, 0, true)]
[ProvideToolWindow(typeof(AliCompanionToolWindow))]
[Guid(PackageGuidString)]
public sealed class AliCompanionPackage : AsyncPackage
{
    public const string PackageGuidString = "af94ca1b-cbb3-4c03-bd44-9ed0654e6931";

    internal static AliCompanionPackage? Instance { get; private set; }

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Instance = this;
        await AliCompanionCommand.InitializeAsync(this);
    }

    internal AliCompanionOptionsPage GetOptionsPage()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (AliCompanionOptionsPage)GetDialogPage(typeof(AliCompanionOptionsPage));
    }

    internal async Task ShowToolWindowAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var window = FindToolWindow(typeof(AliCompanionToolWindow), id: 0, create: true)
            ?? throw new InvalidOperationException("Could not create Ali Companion tool window.");
        if (window.Frame is not IVsWindowFrame frame)
        {
            throw new InvalidOperationException("Could not create Ali Companion tool window frame.");
        }

        ErrorHandler.ThrowOnFailure(frame.Show());
    }
}
