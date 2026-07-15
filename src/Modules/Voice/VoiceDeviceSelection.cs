namespace Ali.Modules.Voice;

public sealed record VoiceDeviceSelectionResult(
    int DeviceNumber,
    string DeviceName,
    bool RestoredSavedDevice,
    string? Warning)
{
    public string DisplayName => $"{DeviceNumber}: {DeviceName}";
}

public static class VoiceDeviceSelection
{
    public static VoiceDeviceSelectionResult ResolveInput(
        VoiceRuntimeSettings settings,
        IReadOnlyList<AudioInputDevice> devices)
    {
        if (devices.Count == 0)
        {
            return new VoiceDeviceSelectionResult(0, "Default microphone", false, "No microphone devices were reported by Windows.");
        }

        if (settings.SelectedInputDeviceNumber is int savedNumber)
        {
            var saved = devices.FirstOrDefault(device => device.DeviceNumber == savedNumber);
            if (saved is not null)
            {
                var warning = !string.IsNullOrWhiteSpace(settings.SelectedInputDeviceName)
                    && !string.Equals(settings.SelectedInputDeviceName, saved.Name, StringComparison.Ordinal)
                        ? $"Saved microphone number {savedNumber} now reports as {saved.Name}."
                        : null;
                return new VoiceDeviceSelectionResult(saved.DeviceNumber, saved.Name, true, warning);
            }

            var fallback = devices[0];
            return new VoiceDeviceSelectionResult(
                fallback.DeviceNumber,
                fallback.Name,
                false,
                $"Saved microphone {savedNumber}: {settings.SelectedInputDeviceName ?? "unknown"} is missing. Ali selected {fallback.Name} for now.");
        }

        var first = devices[0];
        return new VoiceDeviceSelectionResult(first.DeviceNumber, first.Name, false, null);
    }

    public static VoiceDeviceSelectionResult ResolveOutput(
        VoiceRuntimeSettings settings,
        IReadOnlyList<AudioOutputDevice> devices)
    {
        if (devices.Count == 0)
        {
            return new VoiceDeviceSelectionResult(-1, "Default playback device", false, "No playback devices were reported by Windows.");
        }

        if (settings.SelectedOutputDeviceNumber is int savedNumber)
        {
            var saved = devices.FirstOrDefault(device => device.DeviceNumber == savedNumber);
            if (saved is not null)
            {
                return new VoiceDeviceSelectionResult(saved.DeviceNumber, saved.Name, true, null);
            }

            var fallback = devices[0];
            return new VoiceDeviceSelectionResult(
                fallback.DeviceNumber,
                fallback.Name,
                false,
                $"Saved speaker {savedNumber}: {settings.SelectedOutputDeviceName ?? "unknown"} is missing. Ali selected {fallback.Name} for now.");
        }

        var first = devices[0];
        return new VoiceDeviceSelectionResult(first.DeviceNumber, first.Name, false, null);
    }
}
