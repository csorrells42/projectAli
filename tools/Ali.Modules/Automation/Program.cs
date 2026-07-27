using Ali.Modules.Automation.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return UiAutomationProgram.Run(args);
    }
}
