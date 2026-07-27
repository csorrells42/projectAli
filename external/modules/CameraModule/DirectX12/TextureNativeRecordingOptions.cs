using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record TextureNativeRecordingOptions(bool ProcessedOutputEnabled, bool DenoiseEnabled, double DenoiseStrength, VideoFrameColorSettings ColorSettings = default(VideoFrameColorSettings));
