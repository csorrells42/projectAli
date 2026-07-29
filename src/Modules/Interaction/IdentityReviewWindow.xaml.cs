using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.IdentityEnrollment;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.Microphone;
using Ali.UI;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Ali.Modules.Interaction;

public partial class IdentityReviewWindow : Window
{
	private readonly IPersonIdentityReviewService _service;

	private readonly FrameworkElement? _liveViewport;

	private readonly IdentityEnrollmentGuidanceModule? _guidance;

	private readonly ISpeakerEnrollmentService? _speakerEnrollment;

	private readonly IMicrophoneInputService? _microphoneInput;

	private readonly System.Windows.Controls.Panel? _originalViewportParent;

	private readonly int _originalViewportIndex = -1;

	private readonly DispatcherTimer _enrollmentTimer;

	private IReadOnlyList<PersonIdentityReviewItem> _items = [];

	private string _lastCompletedIdentityId = "";

	private IReadOnlyList<MicrophoneInputInfo> _microphoneInputs = [];

	private bool _updatingMicrophoneSelection;

	private bool _microphoneSwitchInProgress;

	private string _voiceEnrollmentResultSignature = "";

	public IdentityReviewWindow(
		IPersonIdentityReviewService service,
		FrameworkElement? liveViewport = null,
		IdentityEnrollmentGuidanceModule? guidance = null,
		ISpeakerEnrollmentService? speakerEnrollment = null,
		IMicrophoneInputService? microphoneInput = null)
	{
		NativeTitleBarTheme.ApplyDarkTitleBar(this);
		_service = service ?? throw new ArgumentNullException(nameof(service));
		_liveViewport = liveViewport;
		_guidance = guidance;
		_speakerEnrollment = speakerEnrollment;
		_microphoneInput = microphoneInput;
		if (liveViewport?.Parent is System.Windows.Controls.Panel parent)
		{
			_originalViewportParent = parent;
			_originalViewportIndex = parent.Children.IndexOf(liveViewport);
		}
		InitializeComponent();
		PermissionLevelComboBox.SelectedIndex = 0;
		RegisterAsUserCheckBox.IsChecked = true;
		RegisterAsUserCheckBox.IsEnabled = false;
		AttachLiveViewport();
		_enrollmentTimer = new DispatcherTimer(
			TimeSpan.FromMilliseconds(200),
			DispatcherPriority.Background,
			EnrollmentTimerTick,
			Dispatcher);
		_enrollmentTimer.Start();
		RefreshItems();
		UpdateEnrollmentUi(_service.GetEnrollmentState());
		_ = RefreshMicrophoneInputsAsync();
		UpdateVoiceEnrollmentUi();
	}

	private PersonIdentityReviewItem? Selected =>
		IdentityList.SelectedItem as PersonIdentityReviewItem;

	private void RefreshItems(string? selectIdentityId = null)
	{
		_items = _service.GetIdentityReviewItems()
			.Where(item => item.IsRegisteredUser)
			.ToArray();
		IdentityList.ItemsSource = _items;
		if (_items.Count == 0)
		{
			StatusText.Text =
				"No registered users yet. Enter the required details " +
				"and use the guided camera enrollment.";
			ClearEditor();
			return;
		}
		IdentityList.SelectedItem =
			_items.FirstOrDefault(item =>
				string.Equals(
					item.IdentityId,
					selectIdentityId,
					StringComparison.OrdinalIgnoreCase))
			?? _items[0];
		StatusText.Text =
			$"{_items.Count} registered " +
			(_items.Count == 1 ? "user" : "users") +
			" available for review. " + _service.Status + ".";
	}

	private void IdentitySelectionChanged(
		object sender,
		SelectionChangedEventArgs e)
	{
		PersonIdentityReviewItem? item = Selected;
		if (item is null)
		{
			ClearEditor();
			return;
		}
		FirstNameTextBox.Text = item.FirstName;
		LastNameTextBox.Text = item.LastName;
		UsernameTextBox.Text = item.Username;
		EmailTextBox.Text = item.Email;
		PhoneNumberTextBox.Text = item.PhoneNumber;
		AddressTextBox.Text = item.Address;
		RegisterAsUserCheckBox.IsChecked = item.IsRegisteredUser;
		RegisterAsUserCheckBox.IsEnabled = false;
		PermissionLevelComboBox.SelectedIndex =
			string.Equals(
				item.PermissionLevel,
				"Superuser",
				StringComparison.OrdinalIgnoreCase)
				? 1
				: 0;
		PermissionLevelComboBox.IsEnabled = item.IsRegisteredUser;
		IdentityDetailText.Text =
			$"First seen: {item.FirstSeenAtUtc.ToLocalTime():g}\n" +
			$"Last seen: {item.LastSeenAtUtc.ToLocalTime():g}\n" +
			$"Observations: {item.ObservationCount:n0}\n" +
			$"Encounters: {item.EncounterCount:n0}\n" +
			$"User ID: {item.IdentityId}";
		LoadPhoto(item.ContextPhotoPath);
		UpdateVoiceEnrollmentUi();
	}

	private void RegistrationChanged(
		object sender,
		RoutedEventArgs e)
	{
		if (PermissionLevelComboBox is not null)
		{
			RegisterAsUserCheckBox.IsChecked = true;
			PermissionLevelComboBox.IsEnabled = true;
		}
	}

	private void SaveIdentityClicked(
		object sender,
		RoutedEventArgs e)
	{
		PersonIdentityReviewItem? selected = Selected;
		if (selected is null)
		{
			StatusText.Text = "Select a learned identity first.";
			return;
		}
		string firstName = FirstNameTextBox.Text.Trim();
		string lastName = LastNameTextBox.Text.Trim();
		string username = UsernameTextBox.Text.Trim();
		const bool registerAsUser = true;
		if (string.IsNullOrWhiteSpace(firstName)
				|| string.IsNullOrWhiteSpace(lastName)
				|| string.IsNullOrWhiteSpace(username))
		{
			StatusText.Text =
				"Registered users require first name, last name, and username.";
			if (string.IsNullOrWhiteSpace(firstName))
			{
				FirstNameTextBox.Focus();
			}
			else if (string.IsNullOrWhiteSpace(lastName))
			{
				LastNameTextBox.Focus();
			}
			else
			{
				UsernameTextBox.Focus();
			}
			return;
		}
		string permission =
			(PermissionLevelComboBox.SelectedItem as ComboBoxItem)
				?.Content?.ToString()
			?? "Default User";
		IdentityReviewUpdateResult result =
			_service.UpdateIdentityReview(new IdentityReviewUpdate(
				selected.IdentityId,
				firstName,
				lastName,
				username,
				EmailTextBox.Text.Trim(),
				PhoneNumberTextBox.Text.Trim(),
				AddressTextBox.Text.Trim(),
				registerAsUser,
				permission));
		StatusText.Text = result.Status;
		if (result.Success)
		{
			RefreshItems(selected.IdentityId);
		}
	}

	private void RefreshClicked(object sender, RoutedEventArgs e)
	{
		string? identityId = Selected?.IdentityId;
		RefreshItems(identityId);
	}

	private void NewUserClicked(object sender, RoutedEventArgs e)
	{
		CancelEnrollment();
		_speakerEnrollment?.CancelSpeakerEnrollment();
		IdentityList.SelectedItem = null;
		ClearEditor();
		FirstNameTextBox.Focus();
		StatusText.Text =
			"Enter first name, last name, and a unique username, " +
			"then start enrollment.";
		UpdateEnrollmentUi(_service.GetEnrollmentState());
	}

	private void StartEnrollmentClicked(
		object sender,
		RoutedEventArgs e)
	{
		if (Selected is not null)
		{
			StatusText.Text =
				"Click New user before starting a new enrollment.";
			return;
		}
		string firstName = FirstNameTextBox.Text.Trim();
		string lastName = LastNameTextBox.Text.Trim();
		string username = UsernameTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(firstName)
			|| string.IsNullOrWhiteSpace(lastName)
			|| string.IsNullOrWhiteSpace(username))
		{
			StatusText.Text =
				"Enrollment requires first name, last name, and username.";
			return;
		}
		string permission =
			(PermissionLevelComboBox.SelectedItem as ComboBoxItem)
				?.Content?.ToString()
			?? "Default User";
		var request = new IdentityEnrollmentRequest(
				firstName,
				lastName,
				username,
				EmailTextBox.Text.Trim(),
				PhoneNumberTextBox.Text.Trim(),
				AddressTextBox.Text.Trim(),
				permission);
		IdentityReviewUpdateResult result = _guidance is null
			? new IdentityReviewUpdateResult(
				false,
				"Turn the camera on before starting guided enrollment.")
			: _guidance.BeginEnrollment(request);
		StatusText.Text = result.Status;
		UpdateEnrollmentUi(_service.GetEnrollmentState());
	}

	private void CreateUserWithoutCameraClicked(
		object sender,
		RoutedEventArgs e)
	{
		if (Selected is not null)
		{
			StatusText.Text = "Click New user before creating a separate profile.";
			return;
		}
		string firstName = FirstNameTextBox.Text.Trim();
		string lastName = LastNameTextBox.Text.Trim();
		string username = UsernameTextBox.Text.Trim();
		string permission =
			(PermissionLevelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
			?? "Default User";
		var request = new IdentityEnrollmentRequest(
			firstName,
			lastName,
			username,
			EmailTextBox.Text.Trim(),
			PhoneNumberTextBox.Text.Trim(),
			AddressTextBox.Text.Trim(),
			permission);
		IdentityReviewUpdateResult result = _service.CreateUserProfile(request);
		StatusText.Text = result.Status;
		if (result.Success)
		{
			RefreshItems();
		}
	}

	private void EnrollmentTimerTick(object? sender, EventArgs e)
	{
		UpdateMicrophoneLevel();
		UpdateVoiceEnrollmentUi();
		if (_guidance is not null)
		{
			UpdateGuidedEnrollmentUi(_guidance.GetState());
			return;
		}
		IdentityEnrollmentState state = _service.GetEnrollmentState();
		UpdateEnrollmentUi(state);
		if (!string.IsNullOrWhiteSpace(state.CompletedIdentityId)
			&& !string.Equals(
				state.CompletedIdentityId,
				_lastCompletedIdentityId,
				StringComparison.OrdinalIgnoreCase))
		{
			_lastCompletedIdentityId = state.CompletedIdentityId;
			RefreshItems(state.CompletedIdentityId);
			StatusText.Text = state.Status;
		}
	}

	private async Task RefreshMicrophoneInputsAsync()
	{
		_updatingMicrophoneSelection = true;
		VoiceMicrophoneComboBox.IsEnabled = false;
		VoiceMicrophoneStatusText.Text = "Loading microphone inputs...";
		try
		{
			_microphoneInputs = _microphoneInput is null
				? []
				: await Task.Run(_microphoneInput.GetAvailableInputs);
			VoiceMicrophoneComboBox.ItemsSource = _microphoneInputs;
			string selectedId = _microphoneInput?.SelectedInputId ?? "";
			VoiceMicrophoneComboBox.SelectedItem =
				_microphoneInputs.FirstOrDefault(input => string.Equals(
					input.Id,
					selectedId,
					StringComparison.OrdinalIgnoreCase))
				?? _microphoneInputs.FirstOrDefault(input => input.IsDefault)
				?? _microphoneInputs.FirstOrDefault();
			VoiceMicrophoneComboBox.IsEnabled = _microphoneInputs.Count > 0;
			VoiceMicrophoneStatusText.Text = _microphoneInput?.InputStatus
				?? "Microphone input unavailable";
		}
		catch (Exception exception)
		{
			VoiceMicrophoneComboBox.ItemsSource = null;
			VoiceMicrophoneComboBox.IsEnabled = false;
			VoiceMicrophoneStatusText.Text =
				"Microphone inputs unavailable: " + exception.Message;
		}
		finally
		{
			_updatingMicrophoneSelection = false;
		}
	}

	private async void VoiceMicrophoneSelectionChanged(
		object sender,
		SelectionChangedEventArgs e)
	{
		if (_updatingMicrophoneSelection
			|| _microphoneInput is null
			|| VoiceMicrophoneComboBox.SelectedItem
				is not MicrophoneInputInfo selected
			|| string.Equals(
				selected.Id,
				_microphoneInput.SelectedInputId,
				StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		VoiceMicrophoneComboBox.IsEnabled = false;
		_microphoneSwitchInProgress = true;
		VoiceMicrophoneStatusText.Text =
			"Switching to " + selected.Name + "...";
		try
		{
			await Task.Run(() => _microphoneInput.SelectInput(selected.Id));
			VoiceMicrophoneStatusText.Text = _microphoneInput.InputStatus;
		}
		catch (Exception exception)
		{
			VoiceMicrophoneStatusText.Text =
				"Microphone switch failed: " + exception.Message;
		}
		finally
		{
			_microphoneSwitchInProgress = false;
			VoiceMicrophoneComboBox.IsEnabled = _microphoneInputs.Count > 0;
		}
	}

	private void UpdateMicrophoneLevel()
	{
		double level = _microphoneInput?.GetInputLevel() ?? 0d;
		VoiceMicrophoneLevelMeter.Value = level * 100d;
		VoiceMicrophoneLevelText.Text = $"{level * 100d:0}%";
		if (_microphoneInput is not null && !_microphoneSwitchInProgress)
		{
			VoiceMicrophoneStatusText.Text = _microphoneInput.InputStatus;
		}
	}

	private void StartVoiceEnrollmentClicked(
		object sender,
		RoutedEventArgs e)
	{
		PersonIdentityReviewItem? selected = Selected;
		if (selected is null)
		{
			StatusText.Text =
				"Complete face enrollment and select the registered user first.";
			return;
		}
		if (_speakerEnrollment is null)
		{
			StatusText.Text = "Speaker recognition is not running.";
			return;
		}
		SpeakerEnrollmentResult result =
			_speakerEnrollment.BeginSpeakerEnrollment(
				selected.IdentityId,
				selected.DisplayName);
		StatusText.Text = result.Status;
		UpdateVoiceEnrollmentUi();
	}

	private void CancelVoiceEnrollmentClicked(
		object sender,
		RoutedEventArgs e)
	{
		_speakerEnrollment?.CancelSpeakerEnrollment();
		UpdateVoiceEnrollmentUi();
	}

	private void UpdateVoiceEnrollmentUi()
	{
		if (_speakerEnrollment is null)
		{
			VoiceEnrollmentPromptText.Text =
				"Speaker recognition is not running.";
			VoiceEnrollmentProgressText.Text =
				"Voice enrollment unavailable";
			SetVoiceEnrollmentResult(
				"VOICE ENROLLMENT UNAVAILABLE",
				"#3a171d",
				"#a94a55",
				"#ff9aa5");
			StartVoiceEnrollmentButton.IsEnabled = false;
			CancelVoiceEnrollmentButton.IsEnabled = false;
			return;
		}
		SpeakerEnrollmentState state =
			_speakerEnrollment.GetSpeakerEnrollmentState();
		VoiceEnrollmentPromptText.Text = state.Prompt;
		VoiceEnrollmentProgressText.Text = state.IsAvailable
			? $"{(state.Outcome == SpeakerEnrollmentOutcome.Accepted
				? state.RequiredSampleCount
				: state.CapturedSampleCount)} of "
				+ $"{state.RequiredSampleCount} voice samples captured"
			: state.Status;
		switch (state.Outcome)
		{
			case SpeakerEnrollmentOutcome.Capturing:
				SetVoiceEnrollmentResult(
					"VOICE ENROLLMENT LISTENING — " + state.Status,
					"#3a3014",
					"#a88731",
					"#f0c96d");
				break;
			case SpeakerEnrollmentOutcome.Accepted:
				SetVoiceEnrollmentResult(
					"VOICE ENROLLMENT ACCEPTED AND SAVED FOR "
						+ state.DisplayName.ToUpperInvariant(),
					"#123322",
					"#47c97d",
					"#80e0a4");
				break;
			case SpeakerEnrollmentOutcome.Rejected:
				SetVoiceEnrollmentResult(
					"VOICE ENROLLMENT NOT ACCEPTED — " + state.Status,
					"#3a171d",
					"#a94a55",
					"#ff9aa5");
				break;
			case SpeakerEnrollmentOutcome.Canceled:
				SetVoiceEnrollmentResult(
					"VOICE ENROLLMENT CANCELED",
					"#101820",
					"#37506a",
					"#b9d7ef");
				break;
			default:
				SetVoiceEnrollmentResult(
					"VOICE ENROLLMENT NOT STARTED",
					"#101820",
					"#37506a",
					"#b9d7ef");
				break;
		}
		StartVoiceEnrollmentButton.IsEnabled =
			state.IsAvailable && !state.IsActive && Selected is not null;
		CancelVoiceEnrollmentButton.IsEnabled = state.IsActive;
	}

	private void SetVoiceEnrollmentResult(
		string text,
		string background,
		string border,
		string foreground)
	{
		string signature = string.Join(
			'|',
			text,
			background,
			border,
			foreground);
		if (string.Equals(
			_voiceEnrollmentResultSignature,
			signature,
			StringComparison.Ordinal))
		{
			return;
		}
		_voiceEnrollmentResultSignature = signature;
		VoiceEnrollmentResultText.Text = text;
		VoiceEnrollmentResultBorder.Background =
			(Brush)new BrushConverter().ConvertFromString(background)!;
		VoiceEnrollmentResultBorder.BorderBrush =
			(Brush)new BrushConverter().ConvertFromString(border)!;
		VoiceEnrollmentResultText.Foreground =
			(Brush)new BrushConverter().ConvertFromString(foreground)!;
	}

	private void UpdateEnrollmentUi(IdentityEnrollmentState state)
	{
		EnrollmentPromptText.Text = state.IsActive
			? state.Prompt
			: state.IsAvailable
				? "Enter a new user's details, then start enrollment."
				: state.Status;
		EnrollmentProgressText.Text = state.RequiredPoseCount > 0
			? $"{state.CapturedPoseCount} of " +
				$"{state.RequiredPoseCount} angles captured"
			: "Camera enrollment unavailable";
		StartEnrollmentButton.IsEnabled =
			state.IsAvailable && !state.IsActive;
		if (state.CapturePending)
		{
			StatusText.Text = state.Status;
		}
	}

	private void UpdateGuidedEnrollmentUi(
		IdentityEnrollmentGuidanceState state)
	{
		EnrollmentPromptText.Text = state.IsActive
			? state.Prompt
			: string.IsNullOrWhiteSpace(state.Status)
				? "Enter a new user's details, then start enrollment."
				: state.Status;
		EnrollmentProgressText.Text = state.RequiredPoseCount > 0
			? $"{state.CapturedPoseCount} of "
				+ $"{state.RequiredPoseCount} angles captured"
			: "Camera enrollment unavailable";
		StartEnrollmentButton.IsEnabled = !state.IsActive;
		if (state.IsActive)
		{
			StatusText.Text = state.Status;
		}
		if (!string.IsNullOrWhiteSpace(state.CompletedIdentityId)
			&& !string.Equals(
				state.CompletedIdentityId,
				_lastCompletedIdentityId,
				StringComparison.OrdinalIgnoreCase))
		{
			_lastCompletedIdentityId = state.CompletedIdentityId;
			RefreshItems(state.CompletedIdentityId);
			StatusText.Text = state.Status;
		}
	}

	private async void ReplacePhotoClicked(
		object sender,
		RoutedEventArgs e)
	{
		PersonIdentityReviewItem? selected = Selected;
		if (selected is null)
		{
			StatusText.Text = "Select a learned identity first.";
			return;
		}
		var dialog = new OpenFileDialog
		{
			Title = "Choose a replacement identity photo",
			Filter =
				"Image files|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff"
				+ "|All files|*.*",
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		ReplacePhotoButton.IsEnabled = false;
		try
		{
			byte[] jpegBytes = await Task.Run(
				() => EncodePhotoAsJpeg(dialog.FileName));
			IdentityReviewUpdateResult result =
				await Task.Run(() =>
					_service.ReplaceContextPhoto(
						selected.IdentityId,
						jpegBytes));
			StatusText.Text = result.Status;
			if (result.Success)
			{
				RefreshItems(selected.IdentityId);
			}
		}
		catch (Exception exception)
		{
			StatusText.Text =
				"Replacement photo could not be read: "
				+ exception.Message;
		}
		finally
		{
			ReplacePhotoButton.IsEnabled = true;
		}
	}

	private async void DeleteUserClicked(
		object sender,
		RoutedEventArgs e)
	{
		PersonIdentityReviewItem? selected = Selected;
		if (selected is null)
		{
			StatusText.Text = "Select a user first.";
			return;
		}
		MessageBoxResult confirmation = MessageBox.Show(
			this,
			$"Delete {selected.DisplayName}?\n\n"
			+ "This permanently removes the user record, face "
			+ "enrollment, voice enrollment, and identity photo. Linked avatar data is "
			+ "not deleted.",
			"Delete user?",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No);
		if (confirmation != MessageBoxResult.Yes)
		{
			return;
		}

		DeleteUserButton.IsEnabled = false;
		try
		{
			IdentityReviewUpdateResult result = await Task.Run(() =>
			{
				IdentityReviewUpdateResult identityResult =
					_service.DeleteIdentity(selected.IdentityId);
				if (identityResult.Success)
				{
					_speakerEnrollment?.DeleteSpeakerEnrollment(
						selected.IdentityId);
				}
				return identityResult;
			});
			StatusText.Text = result.Status;
			if (result.Success)
			{
				RefreshItems();
			}
		}
		catch (Exception exception)
		{
			StatusText.Text =
				"Identity could not be deleted: "
				+ exception.Message;
		}
		finally
		{
			DeleteUserButton.IsEnabled = true;
		}
	}

	private void CloseClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void LoadPhoto(string path)
	{
		ContextPhoto.Source = null;
		bool available =
			!string.IsNullOrWhiteSpace(path) && File.Exists(path);
		NoPhotoText.Visibility =
			available ? Visibility.Collapsed : Visibility.Visible;
		if (!available)
		{
			return;
		}
		try
		{
			using FileStream stream = new(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite);
			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.StreamSource = stream;
			image.EndInit();
			image.Freeze();
			ContextPhoto.Source = image;
		}
		catch
		{
			NoPhotoText.Visibility = Visibility.Visible;
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		_enrollmentTimer.Stop();
		try
		{
			CancelEnrollment();
			_speakerEnrollment?.CancelSpeakerEnrollment();
			_guidance?.Dispose();
		}
		finally
		{
			DetachLiveViewport();
			base.OnClosed(e);
		}
	}

	private void CancelEnrollment()
	{
		if (_guidance is not null)
		{
			_guidance.CancelEnrollment();
		}
		else
		{
			_service.CancelEnrollment();
		}
	}

	private void AttachLiveViewport()
	{
		if (_liveViewport is null)
		{
			NoLiveCameraText.Visibility = Visibility.Visible;
			return;
		}
		if (_liveViewport.Parent is System.Windows.Controls.Panel currentParent)
		{
			currentParent.Children.Remove(_liveViewport);
		}
		EnrollmentViewportHost.Children.Add(_liveViewport);
		NoLiveCameraText.Visibility = Visibility.Collapsed;
	}

	private void DetachLiveViewport()
	{
		if (_liveViewport is null)
		{
			return;
		}
		EnrollmentViewportHost.Children.Remove(_liveViewport);
		if (_originalViewportParent is not null)
		{
			int index = Math.Clamp(
				_originalViewportIndex,
				0,
				_originalViewportParent.Children.Count);
			_originalViewportParent.Children.Insert(index, _liveViewport);
		}
	}

	private static byte[] EncodePhotoAsJpeg(string path)
	{
		using FileStream source = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		BitmapDecoder decoder = BitmapDecoder.Create(
			source,
			BitmapCreateOptions.PreservePixelFormat,
			BitmapCacheOption.OnLoad);
		if (decoder.Frames.Count == 0)
		{
			throw new InvalidDataException(
				"The selected file does not contain an image.");
		}
		var encoder = new JpegBitmapEncoder
		{
			QualityLevel = 94
		};
		encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
		using var destination = new MemoryStream();
		encoder.Save(destination);
		return destination.ToArray();
	}

	private void ClearEditor()
	{
		FirstNameTextBox.Text = "";
		LastNameTextBox.Text = "";
		UsernameTextBox.Text = "";
		EmailTextBox.Text = "";
		PhoneNumberTextBox.Text = "";
		AddressTextBox.Text = "";
		RegisterAsUserCheckBox.IsChecked = true;
		RegisterAsUserCheckBox.IsEnabled = false;
		PermissionLevelComboBox.SelectedIndex = 0;
		PermissionLevelComboBox.IsEnabled = true;
		IdentityDetailText.Text = "";
		ContextPhoto.Source = null;
		NoPhotoText.Visibility = Visibility.Visible;
	}
}
