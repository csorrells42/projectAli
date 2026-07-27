# Webcam DirectX12 Capture

Namespace: `AvatarBuilder.Modules.Webcam.DirectX12`

This folder owns texture-native camera capture and recording. It does not own
the viewport, overlays, MediaPipe, identity, or application composition.

Responsibilities:

- Open Media Foundation camera streams through D3D12 or the D3D11 shared-texture bridge.
- Publish immutable `TextureNativeFrameLease` values with monotonic frame IDs and capture timestamps.
- Keep a fixed bridge-slot lease alive until every downstream owner releases it.
- Provide pooled NV12 bytes only when native/shared texture presentation is unavailable.
- Record accepted camera samples on the recording lane.
- Reject arrivals before ownership when its one processing slot is busy; never queue incoming work.

Primary entry points:

- `TextureNativeCameraStream.cs`
- `TextureNativeFrameLease.cs`
- `TextureNativeCameraRecorder.cs`
- `TextureNativeCameraRecordingSession.cs`
- `Direct3D12DeviceManager.cs`
- `ITextureNativeDeviceManager.cs`

`Modules/Webcam/Producer/Dx12WebcamProducer.cs` is the public latest-frame producer.
It exposes camera snapshots and recording controls only.

Presentation lives in `Modules/Viewport/DirectX12`. The viewport retains a source
lease in the fixed DX12 frame-resource slot until that slot's GPU fence completes;
releasing a handoff after CPU submission alone is forbidden because capture could
overwrite the shared texture while the GPU is still sampling it.
