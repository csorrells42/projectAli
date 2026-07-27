using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Immutable identity result for exactly one immutable camera output.
/// Identity never owns or modifies camera pixels.
/// </summary>
public sealed class IdentityOutput :
	ModuleOutput,
	ITextureFrameSnapshot,
	IVisionModuleOutput
{
	public CameraOutput CameraOutput { get; }

	public long FrameId => CameraOutput.FrameId;

	public long CapturedAtTimestamp => CameraOutput.CapturedAtTimestamp;

	public DateTime CapturedAtUtc => CameraOutput.CapturedAtUtc;

	public TextureFrameReference TextureReference =>
		CameraOutput.TextureReference;

	public TextureNativeFrameLease OriginalFrame =>
		CameraOutput.OriginalFrame;

	public PersonIdentitySnapshot Identity { get; }

	public bool HasOverlay() => false;

	public IVisionOverlay? GetOverlay() => null;

	public IVisionFrame GetFrame() => OriginalFrame;

	internal IdentityOutput(
		CameraOutput cameraOutput,
		PersonIdentitySnapshot identity)
	{
		ArgumentNullException.ThrowIfNull(cameraOutput);
		Identity = identity
			?? throw new ArgumentNullException(nameof(identity));
		cameraOutput.RetainForDownstream();
		CameraOutput = cameraOutput;
	}

	protected override void DisposeOwnedResources()
	{
		CameraOutput.Dispose();
	}
}
