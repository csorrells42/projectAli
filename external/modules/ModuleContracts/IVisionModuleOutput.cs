using System;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Contracts;

/// <summary>
/// Immutable frame data that any vision-module output can hand to a viewport.
/// The interface exposes the complete native-frame presentation contract
/// without coupling the viewport to a particular processing stage.
/// </summary>
public interface IVisionFrame : IDisposable
{
	nint Resource { get; }

	int Subresource { get; }

	int Width { get; }

	int Height { get; }

	double FramesPerSecond { get; }

	string DeviceMode { get; }

	string MediaSubtype { get; }

	long FrameNumber { get; }

	long CapturedAtTimestamp { get; }

	DateTime CapturedAtUtc { get; }

	nint D3D12SharedTextureHandle { get; }

	nint D3D11ProducerFenceHandle { get; }

	ulong D3D11ProducerFenceValue { get; }

	byte[]? Nv12PreviewBytes { get; }

	int Nv12PreviewStride { get; }

	bool IsValid { get; }

	TimeSpan Age { get; }

	IVisionFrame? Duplicate();
}

/// <summary>
/// Read-only overlay data bound to the same source frame as its output.
/// </summary>
public interface IVisionOverlay
{
	bool IsBoundToFrame(long frameId);
}

/// <summary>
/// Common display contract implemented by every vision-module output.
/// A viewport can render any stage without knowing which module produced it.
/// </summary>
public interface IVisionModuleOutput : IFramePipelineSnapshot
{
	bool HasOverlay();

	IVisionOverlay? GetOverlay();

	IVisionFrame GetFrame();
}
