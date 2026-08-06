using System.Windows;
using System.Windows.Threading;
using Ali.Modules.Diagnostics;

namespace Ali.UI;

public partial class TransportDiagnosticsWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };

    public TransportDiagnosticsWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void Refresh()
    {
        var snapshot = AliTransportDiagnostics.Capture();
        SetIfChanged(ModelRequestTextBox, snapshot.ModelRequest);
        SetIfChanged(ModelResponseTextBox, snapshot.ModelResponse);
        SetIfChanged(SerenaRequestTextBox, snapshot.SerenaRequest);
        SetIfChanged(SerenaResponseTextBox, snapshot.SerenaResponse);
    }

    private static void SetIfChanged(System.Windows.Controls.TextBox textBox, string value)
    {
        if (!string.Equals(textBox.Text, value, StringComparison.Ordinal))
        {
            textBox.Text = value;
        }
    }
}
