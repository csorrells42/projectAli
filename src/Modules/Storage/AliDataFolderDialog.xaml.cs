using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Ali.UI;
using Microsoft.Win32;

namespace Ali.Modules.Storage;

public partial class AliDataFolderDialog : Window, IComponentConnector
{
	public string SelectedFolder { get; private set; }

	public AliDataFolderDialog(string initialFolder)
	{
		InitializeComponent();
		SelectedFolder = initialFolder;
		UpdateFolderDisplay();
	}

	private void WindowLoaded(object sender, RoutedEventArgs e)
	{
		NativeTitleBarTheme.ApplyDarkTitleBar(this);
	}

	private void ChooseFolderClicked(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "Choose Ali data folder",
			InitialDirectory = (Directory.Exists(SelectedFolder) ? SelectedFolder : AppContext.BaseDirectory),
			Multiselect = false
		};
		if (openFolderDialog.ShowDialog(this) == true)
		{
			SelectedFolder = openFolderDialog.FolderName;
			UpdateFolderDisplay();
		}
	}

	private void OkClicked(object sender, RoutedEventArgs e)
	{
		base.DialogResult = true;
	}

	private void UpdateFolderDisplay()
	{
		FolderPathTextBox.Text = SelectedFolder;
		FolderPathTextBox.CaretIndex = FolderPathTextBox.Text.Length;
		StorageCapacityText.Text = GetStorageCapacityLabel(SelectedFolder);
	}

	private static string GetStorageCapacityLabel(string folder)
	{
		try
		{
			string? pathRoot = Path.GetPathRoot(Path.GetFullPath(folder));
			if (string.IsNullOrWhiteSpace(pathRoot))
			{
				return "Storage capacity unavailable: unknown drive.";
			}
			DriveInfo driveInfo = new DriveInfo(pathRoot);
			if (!driveInfo.IsReady)
			{
				return "Storage capacity unavailable: " + pathRoot + " is not ready.";
			}
			double value = (double)driveInfo.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
			double value2 = (double)driveInfo.TotalSize / 1024.0 / 1024.0 / 1024.0;
			string? pathRoot2 = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
			string value3 = (pathRoot.Equals(pathRoot2, StringComparison.OrdinalIgnoreCase) ? "Windows drive" : "off-system drive");
			return $"Storage: {driveInfo.Name} {value:0.0} GB free of {value2:0.0} GB ({value3}).";
		}
		catch (Exception ex)
		{
			return "Storage capacity unavailable: " + ex.Message;
		}
	}
}
