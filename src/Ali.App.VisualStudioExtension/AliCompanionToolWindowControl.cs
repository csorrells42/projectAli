using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

#pragma warning disable VSTHRD010, VSTHRD101

namespace Ali.App.VisualStudioExtension;

public sealed class AliCompanionToolWindowControl : UserControl
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ali",
        "VisualStudioCompanion",
        "command-history.txt");

    private readonly HttpClient _http = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _context = new();
    private readonly TextBlock _state = new();
    private readonly TextBlock _approval = new();
    private readonly Button _approvalCommandButton = new();
    private readonly ComboBox _history = new();
    private readonly TextBox _command = new();
    private readonly TextBox _output = new();
    private readonly ListBox _diagnostics = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _runButton = new();
    private string? _pendingConfirmationCommand;
    private VsContext _lastContext = VsContext.Empty;

    public AliCompanionToolWindowControl()
    {
        _http.Timeout = TimeSpan.FromSeconds(30);
        Content = BuildLayout();
        LoadCommandHistory();
        Loaded += async (_, _) =>
        {
            RefreshVisualStudioContext();
            await RefreshStatusAsync();
        };
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
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _context.Foreground = Brush(203, 213, 225);
        _context.Background = Brush(15, 23, 42);
        _context.Padding = new Thickness(8);
        _context.TextWrapping = TextWrapping.Wrap;
        _context.Text = "VS context: refresh to read the active solution and document.";
        Grid.SetRow(_context, 0);
        content.Children.Add(_context);

        var commandGroups = BuildCommandGroups();
        Grid.SetRow(commandGroups, 1);
        content.Children.Add(commandGroups);

        var historyPanel = BuildHistoryPanel();
        Grid.SetRow(historyPanel, 2);
        content.Children.Add(historyPanel);

        _command.MinHeight = 58;
        _command.Margin = new Thickness(0, 10, 0, 8);
        _command.Text = "show visual studio integration";
        _command.AcceptsReturn = true;
        _command.TextWrapping = TextWrapping.Wrap;
        _command.Background = Brush(15, 23, 42);
        _command.Foreground = Brushes.White;
        _command.BorderBrush = Brush(71, 85, 105);
        Grid.SetRow(_command, 3);
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
        Grid.SetRow(actions, 4);
        content.Children.Add(actions);

        var statePanel = BuildStatePanel();
        Grid.SetRow(statePanel, 5);
        content.Children.Add(statePanel);

        var approvalPanel = BuildApprovalPanel();
        Grid.SetRow(approvalPanel, 6);
        content.Children.Add(approvalPanel);

        _output.IsReadOnly = true;
        _output.AcceptsReturn = true;
        _output.TextWrapping = TextWrapping.Wrap;
        _output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.Background = Brush(8, 13, 22);
        _output.Foreground = Brush(203, 213, 225);
        _output.BorderBrush = Brush(51, 65, 85);
        _output.Text = "Ali Companion ready. Commands still use Ali's normal approval gates.";
        Grid.SetRow(_output, 7);
        content.Children.Add(_output);

        var diagnosticsPanel = BuildDiagnosticsPanel();
        Grid.SetRow(diagnosticsPanel, 8);
        content.Children.Add(diagnosticsPanel);

        root.Children.Add(content);
        return root;
    }

    private UIElement BuildHistoryPanel()
    {
        var panel = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 8, 0, 0)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "History",
            Foreground = Brush(226, 232, 240),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        _history.MinHeight = 28;
        _history.Background = Brush(15, 23, 42);
        _history.Foreground = Brushes.White;
        _history.BorderBrush = Brush(71, 85, 105);
        _history.SelectionChanged += (_, _) =>
        {
            if (_history.SelectedItem is string command)
            {
                SetCommand(command);
            }
        };
        panel.Children.Add(_history);
        return panel;
    }

    private UIElement BuildStatePanel()
    {
        var panel = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _progress.Width = 96;
        _progress.Height = 14;
        _progress.IsIndeterminate = true;
        _progress.Visibility = Visibility.Collapsed;
        _progress.Margin = new Thickness(0, 3, 8, 0);
        DockPanel.SetDock(_progress, Dock.Left);
        panel.Children.Add(_progress);

        _state.Text = "State: idle.";
        _state.Foreground = Brush(148, 163, 184);
        _state.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_state);
        return panel;
    }

    private UIElement BuildApprovalPanel()
    {
        var panel = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _approvalCommandButton.Content = "Use Confirm";
        _approvalCommandButton.MinWidth = 94;
        _approvalCommandButton.Height = 28;
        _approvalCommandButton.Margin = new Thickness(0, 0, 8, 0);
        _approvalCommandButton.Visibility = Visibility.Collapsed;
        _approvalCommandButton.Click += (_, _) =>
        {
            var confirmationCommand = _pendingConfirmationCommand;
            if (confirmationCommand is not null && !string.IsNullOrWhiteSpace(confirmationCommand))
            {
                SetCommand(confirmationCommand);
            }
        };
        DockPanel.SetDock(_approvalCommandButton, Dock.Left);
        panel.Children.Add(_approvalCommandButton);

        _approval.Text = "Approval: none pending.";
        _approval.Foreground = Brush(148, 163, 184);
        _approval.Background = Brush(15, 23, 42);
        _approval.Padding = new Thickness(8);
        _approval.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_approval);
        return panel;
    }

    private UIElement BuildDiagnosticsPanel()
    {
        var panel = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var label = new TextBlock
        {
            Text = "Diagnostics",
            Foreground = Brush(226, 232, 240),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(label, Dock.Left);
        panel.Children.Add(label);

        _diagnostics.Height = 64;
        _diagnostics.Background = Brush(8, 13, 22);
        _diagnostics.Foreground = Brush(203, 213, 225);
        _diagnostics.BorderBrush = Brush(51, 65, 85);
        _diagnostics.MouseDoubleClick += (_, _) => OpenSelectedDiagnostic();
        panel.Children.Add(_diagnostics);
        return panel;
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

        var context = Button("VS Context", RefreshVisualStudioContext);
        DockPanel.SetDock(context, Dock.Left);
        toolbar.Children.Add(context);

        var open = Button("Open Helper", OpenHelperInBrowser);
        open.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(open, Dock.Left);
        toolbar.Children.Add(open);

        _status.Text = "Ali helper: " + GetHelperUri();
        _status.Foreground = Brush(203, 213, 225);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        toolbar.Children.Add(_status);
        return toolbar;
    }

    private UIElement BuildCommandGroups()
    {
        var panel = new StackPanel();
        AddContextGroup(panel);
        AddGroup(panel, "Awareness", ("VS Status", "show visual studio integration"), ("Architecture", "analyze solution architecture"));
        AddGroup(panel, "Plan", ("Active Step", "show active roadmap step"), ("Recovery", "show crash recovery status"));
        AddGroup(panel, "Build", ("Build", "confirm dotnet build \"path\""), ("Diagnose", "diagnose last build failure"));
        AddGroup(panel, "Git and Reports", ("Git Status", "git status"), ("Report", "generate coding report"));
        return panel;
    }

    private void AddContextGroup(StackPanel parent)
    {
        parent.Children.Add(new TextBlock
        {
            Text = "Current VS Context",
            Foreground = Brush(226, 232, 240),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4)
        });

        var wrap = new WrapPanel();
        AddContextButton(wrap, "Read File", FillReadFileCommand);
        AddContextButton(wrap, "Build Solution", FillBuildSolutionCommand);
        AddContextButton(wrap, "Search Selection", FillSearchSelectionCommand);
        AddContextButton(wrap, "Plan Selection", FillPlanSelectionCommand);
        AddContextButton(wrap, "Patch Selection", FillPatchSelectionCommand);
        parent.Children.Add(wrap);
    }

    private void AddContextButton(WrapPanel parent, string label, Action action)
    {
        var button = Button(label, () =>
        {
            RefreshVisualStudioContext();
            action();
        });
        button.Margin = new Thickness(0, 0, 6, 6);
        parent.Children.Add(button);
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
        await SendAsync(GetEndpointUri("api/coding/status"), body: null, statusPrefix: "Reading Ali bridge status...");
    }

    private async Task RunCommandAsync()
    {
        var command = _command.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            _output.Text = "Enter an Ali coding command first.";
            return;
        }

        SaveCommandToHistory(command);
        await SendAsync(
            GetEndpointUri("api/coding/command"),
            "{\"command\":\"" + EscapeJson(command) + "\"}",
            "Running Ali command...");
    }

    private void RefreshVisualStudioContext()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            if (dte is null)
            {
                _lastContext = VsContext.Empty;
                _context.Text = "VS context: Visual Studio automation service is unavailable.";
                return;
            }

            var document = dte.ActiveDocument;
            var solutionPath = dte.Solution?.FullName;
            var documentPath = document?.FullName;
            var filePath = string.IsNullOrWhiteSpace(documentPath) ? null : documentPath;
            var selection = document?.Selection as TextSelection;
            int? lineNumber = null;
            if (selection is not null && selection.CurrentLine > 0)
            {
                lineNumber = selection.CurrentLine;
            }

            var selectionText = selection?.Text;
            var selectedText = GetOptions().UseSelectedTextInCommands && !string.IsNullOrWhiteSpace(selectionText)
                ? selectionText
                : null;
            _lastContext = new VsContext(solutionPath, filePath, lineNumber, selectedText);
            _context.Text = FormatContext(_lastContext, GetOptions().UseSelectedTextInCommands, selectionText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            _lastContext = VsContext.Empty;
            _context.Text = "VS context: unavailable. " + ex.Message;
        }
    }

    private void FillReadFileCommand()
    {
        var filePath = _lastContext.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ShowMissingContext("Open a file in Visual Studio first.");
            return;
        }

        var command = _lastContext.LineNumber is int line
            ? $"read file {Quote(filePath!)} at line {line}"
            : $"read file {Quote(filePath!)}";
        SetCommand(command);
    }

    private void FillBuildSolutionCommand()
    {
        var solutionPath = _lastContext.SolutionPath;
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            ShowMissingContext("Open a solution in Visual Studio first.");
            return;
        }

        SetCommand($"confirm dotnet build {Quote(solutionPath!)}");
    }

    private void FillSearchSelectionCommand()
    {
        if (!GetOptions().UseSelectedTextInCommands)
        {
            ShowMissingContext("Selection-based commands are disabled in Ali Companion options.");
            return;
        }

        var query = CompactSelection(_lastContext.SelectedText, maxLength: 80);
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowMissingContext("Select a symbol or phrase first.");
            return;
        }

        SetCommand("search workspace for " + query);
    }

    private void FillPlanSelectionCommand()
    {
        if (!GetOptions().UseSelectedTextInCommands)
        {
            ShowMissingContext("Selection-based commands are disabled in Ali Companion options.");
            return;
        }

        var selected = CompactSelection(_lastContext.SelectedText, maxLength: 160);
        if (string.IsNullOrWhiteSpace(selected))
        {
            ShowMissingContext("Select text that describes the change first.");
            return;
        }

        var location = string.IsNullOrWhiteSpace(_lastContext.FilePath)
            ? string.Empty
            : $" in {Path.GetFileName(_lastContext.FilePath)}";
        SetCommand($"plan coding task {selected}{location}");
    }

    private void FillPatchSelectionCommand()
    {
        if (!GetOptions().UseSelectedTextInCommands)
        {
            ShowMissingContext("Selection-based commands are disabled in Ali Companion options.");
            return;
        }

        var filePath = _lastContext.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ShowMissingContext("Open a file in Visual Studio first.");
            return;
        }

        var selected = TrimSelectionForPatch(_lastContext.SelectedText);
        if (string.IsNullOrWhiteSpace(selected))
        {
            ShowMissingContext("Select exact text to replace first.");
            return;
        }

        SetCommand($"preview replace in file {Quote(filePath!)} {Quote(selected!)} with \"replacement text\"");
    }

    private async Task SendAsync(Uri uri, string? body, string statusPrefix)
    {
        SetBusy(true, statusPrefix);
        _status.Text = statusPrefix;
        var command = body is null ? string.Empty : _command.Text.Trim();
        try
        {
            HttpResponseMessage response = body is null
                ? await _http.GetAsync(uri)
                : await _http.PostAsync(uri, new StringContent(body, Encoding.UTF8, "application/json"));
            var json = await response.Content.ReadAsStringAsync();
            var message = ExtractJsonString(json, "message")
                ?? ExtractJsonString(json, "error")
                ?? json;
            _output.Text = message;
            UpdateDiagnostics(message);
            UpdateCommandState(command, message, response.IsSuccessStatusCode);
            UpdateApprovalState(command, message, response.IsSuccessStatusCode);
            _status.Text = response.IsSuccessStatusCode
                ? "Ali bridge responded."
                : $"Ali bridge returned HTTP {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _status.Text = "Ali bridge unavailable.";
            _output.Text = "Could not reach Ali WebHelper at http://127.0.0.1:8765/.\r\nStart it, then press Status.";
            _state.Text = "State: helper unavailable.";
            ClearApprovalState();
            _diagnostics.Items.Clear();
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _runButton.IsEnabled = !busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(status))
        {
            _state.Text = "State: " + status;
        }
    }

    private void UpdateCommandState(string command, string message, bool succeeded)
    {
        if (!succeeded)
        {
            _state.Text = "State: command failed or was refused.";
            return;
        }

        var lowerCommand = command.ToLowerInvariant();
        var lowerMessage = message.ToLowerInvariant();
        if (lowerMessage.Contains("pending patch") || lowerMessage.Contains("confirm apply last patch preview"))
        {
            _state.Text = "State: patch preview pending owner approval.";
        }
        else if (lowerCommand.StartsWith("confirm ", StringComparison.OrdinalIgnoreCase))
        {
            _state.Text = "State: confirmed command completed through Ali's gate.";
        }
        else if (lowerMessage.Contains("requires confirmation") || lowerMessage.Contains("needs confirmation"))
        {
            _state.Text = "State: approval needed before Ali can continue.";
        }
        else if (lowerMessage.Contains("build succeeded") || lowerMessage.Contains("0 error"))
        {
            _state.Text = "State: build/test output looks successful.";
        }
        else
        {
            _state.Text = "State: command completed.";
        }
    }

    private void UpdateApprovalState(string command, string message, bool succeeded)
    {
        if (!succeeded)
        {
            ShowApproval(new ApprovalSummary(
                RequestedCommand: command,
                Risk: "Command refused or failed",
                Target: ExtractTarget(command, message),
                ConfirmationCommand: ExtractConfirmationCommand(message)));
            return;
        }

        if (command.StartsWith("confirm ", StringComparison.OrdinalIgnoreCase))
        {
            ClearApprovalState();
            return;
        }

        var summary = DetectApprovalSummary(command, message);
        if (summary is null)
        {
            ClearApprovalState();
            return;
        }

        ShowApproval(summary);
    }

    private void ShowApproval(ApprovalSummary summary)
    {
        _pendingConfirmationCommand = summary.ConfirmationCommand;
        _approval.Text =
            "Approval pending: " + Blank(summary.RequestedCommand, "unknown command") +
            "\r\nRisk: " + Blank(summary.Risk, "review required") +
            "\r\nTarget: " + Blank(summary.Target, "not identified") +
            "\r\nNext: " + Blank(summary.ConfirmationCommand, "review Ali output for the confirmation command");
        _approval.Foreground = Brush(253, 224, 71);
        _approvalCommandButton.Visibility = string.IsNullOrWhiteSpace(_pendingConfirmationCommand)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearApprovalState()
    {
        _pendingConfirmationCommand = null;
        _approval.Text = "Approval: none pending.";
        _approval.Foreground = Brush(148, 163, 184);
        _approvalCommandButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateDiagnostics(string message)
    {
        _diagnostics.Items.Clear();
        foreach (var diagnostic in ParseDiagnostics(message).Take(12))
        {
            _diagnostics.Items.Add(diagnostic);
        }
    }

    private void OpenSelectedDiagnostic()
    {
        if (_diagnostics.SelectedItem is not DiagnosticEntry diagnostic)
        {
            return;
        }

        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            if (dte is null)
            {
                _state.Text = "State: Visual Studio automation service is unavailable.";
                return;
            }

            dte.ItemOperations.OpenFile(diagnostic.FilePath);
            if (dte.ActiveDocument?.Selection is TextSelection selection)
            {
                selection.MoveToLineAndOffset(diagnostic.Line, Math.Max(1, diagnostic.Column), false);
            }

            _state.Text = "State: opened diagnostic location.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            _state.Text = "State: could not open diagnostic. " + ex.Message;
        }
    }

    private void LoadCommandHistory()
    {
        _history.Items.Clear();
        foreach (var command in ReadHistory().Take(GetOptions().ClampedHistoryLimit))
        {
            _history.Items.Add(command);
        }
    }

    private void SaveCommandToHistory(string command)
    {
        var limit = GetOptions().ClampedHistoryLimit;
        var history = new List<string> { command };
        history.AddRange(ReadHistory().Where(item => !string.Equals(item, command, StringComparison.OrdinalIgnoreCase)));
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllLines(HistoryPath, history.Take(limit), Encoding.UTF8);
        LoadCommandHistory();
    }

    private static IReadOnlyList<string> ReadHistory()
    {
        if (!File.Exists(HistoryPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(HistoryPath, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ApprovalSummary? DetectApprovalSummary(string command, string message)
    {
        var lower = message.ToLowerInvariant();
        var confirmationCommand = ExtractConfirmationCommand(message);
        var isPending =
            lower.Contains("requires confirmation") ||
            lower.Contains("needs confirmation") ||
            lower.Contains("pending patch") ||
            lower.Contains("confirm apply last patch preview") ||
            !string.IsNullOrWhiteSpace(confirmationCommand);
        if (!isPending)
        {
            return null;
        }

        var requested = string.IsNullOrWhiteSpace(command) ? ExtractRequestedCommand(message) : command;
        return new ApprovalSummary(
            RequestedCommand: requested,
            Risk: ClassifyRisk(requested, message),
            Target: ExtractTarget(requested, message),
            ConfirmationCommand: confirmationCommand);
    }

    private static string? ExtractConfirmationCommand(string message)
    {
        var patterns = new[]
        {
            "(?im)^\\s*-\\s*(?<command>confirm\\s+[^\\r\\n]+)",
            "(?im)^\\s*(?<command>confirm\\s+[^\\r\\n]+)",
            "(?<command>confirm\\s+apply\\s+last\\s+patch\\s+preview)",
            "(?<command>confirm\\s+dotnet\\s+(?:build|restore|add package|test|run)[^\\r\\n]*)",
            "(?<command>confirm\\s+git\\s+(?:add|commit|merge)[^\\r\\n]*)",
            "(?<command>confirm\\s+(?:create|append to|replace in)\\s+file[^\\r\\n]*)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return CleanCommand(match.Groups["command"].Value);
            }
        }

        return null;
    }

    private static string? ExtractRequestedCommand(string message)
    {
        var match = Regex.Match(message, "(?im)^\\s*(?:requested command|command)\\s*:\\s*(?<command>.+)$");
        return match.Success ? CleanCommand(match.Groups["command"].Value) : null;
    }

    private static string ClassifyRisk(string? command, string message)
    {
        var text = ((command ?? string.Empty) + "\n" + message).ToLowerInvariant();
        if (text.Contains("patch") || text.Contains("replace in file") || text.Contains("create file") || text.Contains("append to file"))
        {
            return "File edit";
        }

        if (text.Contains("dotnet add package") || text.Contains("dotnet restore") || text.Contains("package"))
        {
            return "Package/dependency change";
        }

        if (text.Contains("dotnet build") || text.Contains("dotnet test") || text.Contains("dotnet run"))
        {
            return "Build/test/run";
        }

        if (text.Contains("git commit") || text.Contains("git add") || text.Contains("git merge"))
        {
            return "Git write";
        }

        return "Owner confirmation";
    }

    private static string? ExtractTarget(string? command, string message)
    {
        var text = (command ?? string.Empty) + "\n" + message;
        var quotedPath = Regex.Match(text, "\"(?<path>[A-Za-z]:\\\\[^\"]+)\"");
        if (quotedPath.Success)
        {
            return quotedPath.Groups["path"].Value;
        }

        var barePath = Regex.Match(text, "(?<path>[A-Za-z]:\\\\[^\\r\\n\"']+)");
        if (barePath.Success)
        {
            return barePath.Groups["path"].Value.TrimEnd('.', ';', ',');
        }

        if (text.Contains("patch preview", StringComparison.OrdinalIgnoreCase))
        {
            return "pending patch preview";
        }

        return null;
    }

    private static string CleanCommand(string value) =>
        value.Trim().TrimEnd('.', ';');

    private static string Blank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value!;

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
        System.Diagnostics.Process.Start(new ProcessStartInfo(GetHelperUri().ToString()) { UseShellExecute = true });
    }

    private void SetCommand(string command)
    {
        _command.Text = command;
        _command.Focus();
        _command.Select(_command.Text.Length, 0);
    }

    private void ShowMissingContext(string message)
    {
        _output.Text = message;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));

    private static AliCompanionOptionsPage GetOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return AliCompanionPackage.Instance?.GetOptionsPage() ?? new AliCompanionOptionsPage();
    }

    private static Uri GetHelperUri()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Uri.TryCreate(GetOptions().NormalizedHelperUrl, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("http://127.0.0.1:8765/");
    }

    private static Uri GetEndpointUri(string relativePath) =>
        new(GetHelperUri(), relativePath);

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? CompactSelection(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value ?? string.Empty;
        var compact = Regex.Replace(text.Trim(), "\\s+", " ");
        return compact.Length <= maxLength ? compact : compact.Substring(0, maxLength).TrimEnd();
    }

    private static string? TrimSelectionForPatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value ?? string.Empty;
        var trimmed = text.Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed.Substring(0, 240);
    }

    private static string FormatContext(VsContext context, bool selectedTextEnabled, string? rawSelectedText)
    {
        var solution = string.IsNullOrWhiteSpace(context.SolutionPath)
            ? "no solution"
            : Path.GetFileName(context.SolutionPath);
        var file = string.IsNullOrWhiteSpace(context.FilePath)
            ? "no active file"
            : Path.GetFileName(context.FilePath);
        var line = context.LineNumber is int lineNumber ? lineNumber.ToString() : "n/a";
        var selection = !selectedTextEnabled && !string.IsNullOrWhiteSpace(rawSelectedText)
            ? "withheld by options"
            : string.IsNullOrWhiteSpace(context.SelectedText)
            ? "none"
            : context.SelectedText!.Length + " chars";
        return $"VS context: solution {solution}; file {file}; line {line}; selection {selection}.";
    }

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

    private static IEnumerable<DiagnosticEntry> ParseDiagnostics(string message)
    {
        var matches = Regex.Matches(
            message,
            "(?<file>[A-Za-z]:\\\\[^\\r\\n]+?)\\((?<line>\\d+)(?:,(?<column>\\d+))?\\):\\s*(?<kind>error|warning)\\s+(?<code>[A-Za-z]+\\d+)",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line))
            {
                continue;
            }

            var column = 1;
            if (match.Groups["column"].Success)
            {
                int.TryParse(match.Groups["column"].Value, out column);
                column = Math.Max(1, column);
            }

            var filePath = match.Groups["file"].Value;
            var kind = match.Groups["kind"].Value;
            var code = match.Groups["code"].Value;
            yield return new DiagnosticEntry(filePath, line, column, kind, code);
        }
    }

    private sealed class DiagnosticEntry
    {
        public DiagnosticEntry(string filePath, int line, int column, string kind, string code)
        {
            FilePath = filePath;
            Line = line;
            Column = column;
            Kind = kind;
            Code = code;
        }

        public string FilePath { get; }

        public int Line { get; }

        public int Column { get; }

        public string Kind { get; }

        public string Code { get; }

        public override string ToString() =>
            $"{Kind} {Code}: {Path.GetFileName(FilePath)}:{Line}";
    }

    private sealed class ApprovalSummary
    {
        public ApprovalSummary(
            string? RequestedCommand,
            string? Risk,
            string? Target,
            string? ConfirmationCommand)
        {
            this.RequestedCommand = RequestedCommand;
            this.Risk = Risk;
            this.Target = Target;
            this.ConfirmationCommand = ConfirmationCommand;
        }

        public string? RequestedCommand { get; }

        public string? Risk { get; }

        public string? Target { get; }

        public string? ConfirmationCommand { get; }
    }

    private sealed class VsContext
    {
        public static readonly VsContext Empty = new(null, null, null, null);

        public VsContext(string? solutionPath, string? filePath, int? lineNumber, string? selectedText)
        {
            SolutionPath = string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath;
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
            LineNumber = lineNumber;
            SelectedText = string.IsNullOrWhiteSpace(selectedText) ? null : selectedText;
        }

        public string? SolutionPath { get; }

        public string? FilePath { get; }

        public int? LineNumber { get; }

        public string? SelectedText { get; }
    }
}
