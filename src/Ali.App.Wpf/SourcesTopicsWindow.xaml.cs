using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Ali.Core.Sources;
using Ali.Infrastructure.Bootstrap;

namespace Ali.App.Wpf;

public partial class SourcesTopicsWindow : Window
{
    private static readonly Regex IdCharacterRegex = new("[^a-z0-9-]+", RegexOptions.CultureInvariant);
    private readonly AliServices _services;
    private readonly ObservableCollection<SourceEditorItem> _sources = new();
    private bool _loadingSelection;

    public SourcesTopicsWindow(AliServices services)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        _services = services;
        Title = $"{_services.AssistantProfile.AssistantName} Sources & Topics";
        DescriptionText.Text = $"Add approved sources and the topics {_services.AssistantProfile.AssistantName} should use them for.";
        TrustComboBox.ItemsSource = new[] { "standard", "official", "primary", "watch" };
        TrustComboBox.SelectedItem = "standard";
        SourcesListBox.ItemsSource = _sources;
        LoadSources();
    }

    private void LoadSources()
    {
        _sources.Clear();
        try
        {
            foreach (var source in _services.LoadCuratedSources().OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
            {
                _sources.Add(new SourceEditorItem(source));
            }

            SourcesListBox.SelectedIndex = _sources.Count > 0 ? 0 : -1;
            if (_sources.Count == 0)
            {
                ClearEditor();
            }

            StatusText.Text = $"{_sources.Count} approved source(s) loaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"Sources could not be loaded: {ex.Message}";
            ClearEditor();
        }
    }

    private void SourcesListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection)
        {
            return;
        }

        LoadSelectedSourceIntoEditor();
    }

    private void LoadSelectedSourceIntoEditor()
    {
        _loadingSelection = true;
        try
        {
            if (SourcesListBox.SelectedItem is not SourceEditorItem item)
            {
                ClearEditor();
                return;
            }

            var source = item.Source;
            NameTextBox.Text = source.Name;
            UrlTextBox.Text = source.Url;
            TopicsTextBox.Text = string.Join(", ", SourceTopics(source));
            NotesTextBox.Text = source.Notes ?? string.Empty;
            EnabledCheckBox.IsChecked = source.Enabled;
            TrustComboBox.SelectedItem = NormalizeTrust(source.TrustLevel);
            StatusText.Text = $"Editing {source.Name}.";
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    private void ClearEditor()
    {
        NameTextBox.Text = string.Empty;
        UrlTextBox.Text = string.Empty;
        TopicsTextBox.Text = string.Empty;
        NotesTextBox.Text = string.Empty;
        EnabledCheckBox.IsChecked = true;
        TrustComboBox.SelectedItem = "standard";
    }

    private void NewSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        var item = new SourceEditorItem(new SourceCatalogEntry(
            Id: string.Empty,
            Topic: string.Empty,
            Name: "New source",
            Url: string.Empty,
            Type: "web",
            TrustLevel: "standard",
            Topics: [],
            Enabled: true));
        _sources.Add(item);
        SourcesListBox.SelectedItem = item;
        LoadSelectedSourceIntoEditor();
        NameTextBox.Focus();
        NameTextBox.SelectAll();
        StatusText.Text = "New source ready.";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveSelectedSource();
    }

    private bool SaveSelectedSource()
    {
        if (SourcesListBox.SelectedItem is not SourceEditorItem selected)
        {
            StatusText.Text = "Choose or create a source before saving.";
            return false;
        }

        var name = NameTextBox.Text.Trim();
        var url = UrlTextBox.Text.Trim();
        var topics = ParseList(TopicsTextBox.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "Source name is required.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusText.Text = "Use a valid HTTP or HTTPS source address.";
            return false;
        }

        if (topics.Count == 0)
        {
            StatusText.Text = "Add at least one topic for this source.";
            return false;
        }

        var id = string.IsNullOrWhiteSpace(selected.Source.Id)
            ? CreateUniqueId(name, uri)
            : selected.Source.Id;
        var primaryTopic = NormalizeTopicForStorage(topics[0]);
        var existingKeywords = selected.Source.Keywords ?? Array.Empty<string>();
        var keywords = existingKeywords
            .Concat(topics)
            .Select(topic => topic.Trim())
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        selected.Source = selected.Source with
        {
            Id = id,
            Topic = primaryTopic,
            Name = name,
            Url = url,
            Type = string.IsNullOrWhiteSpace(selected.Source.Type) ? "web" : selected.Source.Type,
            TrustLevel = NormalizeTrust(TrustComboBox.SelectedItem?.ToString()),
            Keywords = keywords,
            Topics = topics,
            Notes = string.IsNullOrWhiteSpace(NotesTextBox.Text) ? null : NotesTextBox.Text.Trim(),
            Enabled = EnabledCheckBox.IsChecked == true
        };

        try
        {
            _services.SaveCuratedSources(_sources.Select(item => item.Source).Where(source => !string.IsNullOrWhiteSpace(source.Id)));
            selected.Refresh();
            SourcesListBox.Items.Refresh();
            StatusText.Text = $"{_sources.Count} source(s) saved.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"Sources could not be saved: {ex.Message}";
            return false;
        }
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SourcesListBox.SelectedItem is not SourceEditorItem selected)
        {
            StatusText.Text = "Choose a source before deleting.";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"Remove {selected.Source.Name} from approved sources?",
            "Delete Source",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = SourcesListBox.SelectedIndex;
        _sources.Remove(selected);
        _services.SaveCuratedSources(_sources.Select(item => item.Source).Where(source => !string.IsNullOrWhiteSpace(source.Id)));
        SourcesListBox.SelectedIndex = _sources.Count == 0 ? -1 : Math.Min(index, _sources.Count - 1);
        if (_sources.Count == 0)
        {
            ClearEditor();
        }

        StatusText.Text = $"{_sources.Count} source(s) saved.";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private string CreateUniqueId(string name, Uri uri)
    {
        var seed = string.IsNullOrWhiteSpace(name) ? uri.Host : name;
        var baseId = NormalizeId(seed);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "source";
        }

        var existing = _sources
            .Select(item => item.Source.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = baseId;
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static string NormalizeId(string value)
    {
        var lower = value.Trim().ToLowerInvariant().Replace(' ', '-');
        lower = IdCharacterRegex.Replace(lower, "-");
        return lower.Trim('-');
    }

    private static string NormalizeTopicForStorage(string topic)
    {
        var normalized = NormalizeId(topic);
        return string.IsNullOrWhiteSpace(normalized) ? "general" : normalized;
    }

    private static string NormalizeTrust(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "primary" => "primary",
            "official" => "official",
            "watch" => "watch",
            _ => "standard"
        };

    private static IReadOnlyList<string> ParseList(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

    private static IReadOnlyList<string> SourceTopics(SourceCatalogEntry source)
    {
        var topics = source.Topics is { Count: > 0 }
            ? source.Topics
            : [source.Topic];
        return topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class SourceEditorItem(SourceCatalogEntry source) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public SourceCatalogEntry Source { get; set; } = source;

        public string Name => string.IsNullOrWhiteSpace(Source.Name) ? "Unnamed source" : Source.Name;

        public string Summary
        {
            get
            {
                var topics = Source.Topics is { Count: > 0 }
                    ? string.Join(", ", Source.Topics)
                    : Source.Topic;
                var state = Source.Enabled ? "Enabled" : "Disabled";
                return string.IsNullOrWhiteSpace(topics) ? state : $"{state} - {topics}";
            }
        }

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }
}
