namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record TextureNativeRecordingResult(bool Success, string Path, int SamplesWritten, long BytesWritten, string DeviceMode, string MediaSubtype, int Width, int Height, double FramesPerSecond, string Status, string RecordingPipeline = "Media Foundation texture-native raw camera samples", bool RecordingDenoiseApplied = false, bool RecordingMatchesPreviewDenoise = false, bool RecordingColorPolishApplied = false, bool RecordingMatchesPreviewColor = false);
