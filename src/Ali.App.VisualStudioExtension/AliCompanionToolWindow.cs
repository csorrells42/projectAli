using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Ali.App.VisualStudioExtension;

[Guid("3c5f83de-39d2-4420-8c10-c02d03d1f746")]
public sealed class AliCompanionToolWindow : ToolWindowPane
{
    public AliCompanionToolWindow()
        : base(null)
    {
        Caption = "Ali Companion";
        Content = new AliCompanionToolWindowControl();
    }
}
