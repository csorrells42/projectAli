using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Ali.Modules.Internet;
using Ali.Modules.RAG;
using Ali.UI;
using Ali;
using Forms = System.Windows.Forms;

namespace Ali.Modules.RAG;

public partial class LocalLibraryWindow : Window
{
    private readonly AliServices _services;
    private LocalVectorLibrarySettings _settings;
    private CancellationTokenSource? _scanCancellation;

    public LocalLibraryWindow(AliServices services)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        _services = services;
        Title = $"{_services.AssistantProfile.AssistantName} Local Library";
        _settings = _services.LoadLocalVectorLibrarySettings();
        LoadSettingsIntoView();
    }

    private void LoadSettingsIntoView()
    {
        FolderTextBox.Text = _settings.RootDirectory;
        EmbeddingModelText.Text = _settings.EmbeddingModel;
        SettingsPathText.Text = $"Settings: {_services.LocalVectorLibrarySettingsPath}";
        RefreshIndexSummary();
        StatusText.Text = $"{_services.AssistantProfile.AssistantName} will only index supported text documents from this approved local folder.";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromView();
    }

    private void ChooseFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"Choose {_services.AssistantProfile.AssistantName}'s approved local RAG folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FolderTextBox.Text)
                ? FolderTextBox.Text
                : LocalVectorLibrarySettings.DefaultRootDirectory()
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        FolderTextBox.Text = dialog.SelectedPath;
        SaveSettingsFromView();
    }

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var saved = SaveSettingsFromView();
        if (!saved)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.RootDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.RootDirectory,
                UseShellExecute = true
            });
            StatusText.Text = "Local library folder opened.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not open local library folder: {ex.Message}";
        }
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        var saved = SaveSettingsFromView();
        if (!saved)
        {
            return;
        }

        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning local library and updating vector index...";
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var retriever = _services.CreateLocalVectorLibraryRetriever();
            retriever.WriteExample();
            var result = await retriever.RetrieveAsync(
                    new SourceQueryPlan(
                        true,
                        true,
                        "local_documents",
                        "local library document folder scan",
                        ["local", "library", "document", "folder"],
                        ["local_documents"]),
                    _scanCancellation.Token)
                .ConfigureAwait(true);

            RefreshIndexSummary();
            StatusText.Text = result.Warnings.Count == 0
                ? $"Scan complete. Retrieved {result.Excerpts.Count} local library excerpt(s) for the scan probe."
                : $"Scan complete with note: {string.Join(" ", result.Warnings)}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Local library scan cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"Local library scan failed safely: {ex.Message}";
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = true;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;
        base.OnClosed(e);
    }

    private bool SaveSettingsFromView()
    {
        var folder = FolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText.Text = "Choose a local library folder before saving.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(folder);
            _settings = _services
                .SaveLocalVectorLibraryRootDirectory(fullPath)
                .Settings;
            Directory.CreateDirectory(fullPath);
            StatusText.Text = "Local library settings saved.";
            RefreshIndexSummary();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Local library folder could not be saved: {ex.Message}";
            return false;
        }
    }

    private async void RefreshIndexSummary()
    {
        try
        {
            var status = await _services.CreateLocalVectorLibraryRetriever().GetStatusAsync().ConfigureAwait(true);
            IndexSummaryText.Text = status.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Grpc.Core.RpcException)
        {
            IndexSummaryText.Text = $"Qdrant status unavailable: {ex.Message}";
        }
    }
}
