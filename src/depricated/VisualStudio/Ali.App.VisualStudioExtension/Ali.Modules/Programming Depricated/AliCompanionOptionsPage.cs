using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace Ali.App.VisualStudioExtension;

public sealed class AliCompanionOptionsPage : DialogPage
{
    [Category("Connection")]
    [DisplayName("Helper URL")]
    [Description("Loopback URL for Ali WebHelper. The default is http://127.0.0.1:8765/.")]
    public string HelperUrl { get; set; } = "http://127.0.0.1:8765/";

    [Category("History")]
    [DisplayName("Command history limit")]
    [Description("Maximum number of recent commands shown in the Ali Companion history list.")]
    public int CommandHistoryLimit { get; set; } = 20;

    [Category("Context")]
    [DisplayName("Use selected text in commands")]
    [Description("When enabled, selection-based buttons can place selected text into Ali commands. Commands still route through Ali's local approval gates.")]
    public bool UseSelectedTextInCommands { get; set; } = true;

    internal int ClampedHistoryLimit => CommandHistoryLimit < 1 ? 1 : CommandHistoryLimit > 100 ? 100 : CommandHistoryLimit;

    internal string NormalizedHelperUrl
    {
        get
        {
            var value = string.IsNullOrWhiteSpace(HelperUrl)
                ? "http://127.0.0.1:8765/"
                : HelperUrl.Trim();
            return value.EndsWith("/", System.StringComparison.Ordinal) ? value : value + "/";
        }
    }
}
