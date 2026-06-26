using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ali.App.VisualStudioExtension;

public sealed class AliCompanionToolWindowControl : UserControl
{
    private static readonly Uri HelperUri = new("http://127.0.0.1:8765/");
    private static readonly Uri CodingStatusUri = new("http://127.0.0.1:8765/api/coding/status");
    private static readonly Uri CodingCommandUri = new("http://127.0.0.1:8765/api/coding/command");

    private readonly HttpClient _http = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _command = new();
    private readonly TextBox _output = new();
    private readonly Button _runButton = new();

    public AliCompanionToolWindowControl()
    {
        _http.Timeout = TimeSpan.FromSeconds(30);
        Content = BuildLayout();
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel
        {
            LastChildFill = true,
            Background = Brush(17, 22, 29)
        };

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var content = new Grid { Margin = new Thickness(10) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var commandGroups = BuildCommandGroups();
        Grid.SetRow(commandGroups, 0);
        content.Children.Add(commandGroups);

        _command.MinHeight = 58;
        _command.Margin = new Thickness(0, 10, 0, 8);
        _command.Text = "show visual studio integration";
        _command.AcceptsReturn = true;
        _command.TextWrapping = TextWrapping.Wrap;
        _command.Background = Brush(15, 23, 42);
        _command.Foreground = Brushes.White;
        _command.BorderBrush = Brush(71, 85, 105);
        Grid.SetRow(_command, 1);
        content.Children.Add(_command);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _runButton.Content = "Run";
        _runButton.MinWidth = 86;
        _runButton.Height = 30;
        _runButton.Margin = new Thickness(0, 0, 8, 0);
        _runButton.Click += async (_, _) => await RunCommandAsync();
        actions.Children.Add(_runButton);
        actions.Children.Add(Button("Clear", () => _command.Clear()));
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);

        _output.IsReadOnly = true;
        _output.AcceptsReturn = true;
        _output.TextWrapping = TextWrapping.Wrap;
        _output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.Background = Brush(8, 13, 22);
        _output.Foreground = Brush(203, 213, 225);
        _output.BorderBrush = Brush(51, 65, 85);
        _output.Text = "Ali Companion ready. Commands still use Ali's normal approval gates.";
        Grid.SetRow(_output, 3);
        content.Children.Add(_output);

        root.Children.Add(content);
        return root;
    }

    private UIElement BuildToolbar()
    {
        var toolbar = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(10)
        };

        var refresh = Button("Status", async () => await RefreshStatusAsync());
        DockPanel.SetDock(refresh, Dock.Left);
        toolbar.Children.Add(refresh);

        var open = Button("Open Helper", OpenHelperInBrowser);
        open.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(open, Dock.Left);
        toolbar.Children.Add(open);

        _status.Text = "Ali helper: http://127.0.0.1:8765/";
        _status.Foreground = Brush(203, 213, 225);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        toolbar.Children.Add(_status);
        return toolbar;
    }

    private UIElement BuildCommandGroups()
    {
        var panel = new StackPanel();
        AddGroup(panel, "Awareness", ("VS Status", "show visual studio integration"), ("Architecture", "analyze solution architecture"));
        AddGroup(panel, "Plan", ("Active Step", "show active roadmap step"), ("Recovery", "show crash recovery status"));
        AddGroup(panel, "Build", ("Build", "confirm dotnet build \"path\""), ("Diagnose", "diagnose last build failure"));
        AddGroup(panel, "Git and Reports", ("Git Status", "git status"), ("Report", "generate coding report"));
        return panel;
    }

    private void AddGroup(StackPanel parent, string title, params (string Label, string Command)[] commands)
    {
        parent.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(226, 232, 240),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4)
        });

        var wrap = new WrapPanel();
        foreach (var item in commands)
        {
            var button = Button(item.Label, () =>
            {
                _command.Text = item.Command;
                _command.Focus();
                _command.Select(_command.Text.Length, 0);
            });
            button.Margin = new Thickness(0, 0, 6, 6);
            wrap.Children.Add(button);
        }

        parent.Children.Add(wrap);
    }

    private async Task RefreshStatusAsync()
    {
        await SendAsync(CodingStatusUri, body: null, statusPrefix: "Reading Ali bridge status...");
    }

    private async Task RunCommandAsync()
    {
        var command = _command.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            _output.Text = "Enter an Ali coding command first.";
            return;
        }

        await SendAsync(
            CodingCommandUri,
            "{\"command\":\"" + EscapeJson(command) + "\"}",
            "Running Ali command...");
    }

    private async Task SendAsync(Uri uri, string? body, string statusPrefix)
    {
        _runButton.IsEnabled = false;
        _status.Text = statusPrefix;
        try
        {
            HttpResponseMessage response = body is null
                ? await _http.GetAsync(uri)
                : await _http.PostAsync(uri, new StringContent(body, Encoding.UTF8, "application/json"));
            var json = await response.Content.ReadAsStringAsync();
            _output.Text = ExtractJsonString(json, "message")
                ?? ExtractJsonString(json, "error")
                ?? json;
            _status.Text = response.IsSuccessStatusCode
                ? "Ali bridge responded."
                : $"Ali bridge returned HTTP {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _status.Text = "Ali bridge unavailable.";
            _output.Text = "Could not reach Ali WebHelper at http://127.0.0.1:8765/.\r\nStart it, then press Status.";
        }
        finally
        {
            _runButton.IsEnabled = true;
        }
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 82,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 82,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void OpenHelperInBrowser()
    {
        Process.Start(new ProcessStartInfo(HelperUri.ToString()) { UseShellExecute = true });
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string? ExtractJsonString(string json, string propertyName)
    {
        var match = Regex.Match(
            json,
            "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.Singleline);
        return match.Success
            ? Regex.Unescape(match.Groups["value"].Value)
            : null;
    }
}
