using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Ali.UI.ViewModels;

public sealed class CommandExplorerNodeViewModel(
    string title,
    string summary,
    string? commandText = null,
    string? usage = null,
    IEnumerable<CommandExplorerNodeViewModel>? children = null)
{
    public string Title { get; } = title;

    public string Summary { get; } = summary;

    public string? CommandText { get; } = commandText;

    public string Usage { get; } = usage ?? commandText ?? "Select a command to see usage.";

    public ObservableCollection<CommandExplorerNodeViewModel> Children { get; } = new(children ?? []);

    public bool IsCommand => !string.IsNullOrWhiteSpace(CommandText);
}

