using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Webcam;

public sealed class CameraOutput :
	ModuleOutput,
	ITextureFrameSnapshot,
	IVisionModuleOutput
{
	private TextureFrameReference? _textureReference;

	public long FrameId { get; }

	public long CapturedAtTimestamp { get; }

	public DateTime CapturedAtUtc { get; }

	public TextureFrameReference TextureReference =>
		Volatile.Read(ref _textureReference)
		?? throw new ObjectDisposedException(nameof(CameraOutput));

	public TextureNativeFrameLease OriginalFrame =>
		TextureReference.Frame;

	public bool HasOverlay()
	{
		return false;
	}

	public IVisionOverlay? GetOverlay()
	{
		return null;
	}

	public IVisionFrame GetFrame()
	{
		return OriginalFrame;
	}

	internal CameraOutput(TextureNativeFrameLease frame)
	{
		ArgumentNullException.ThrowIfNull(frame);
		_textureReference = new TextureFrameReference(frame);
		FrameId = frame.FrameNumber;
		CapturedAtTimestamp = frame.CapturedAtTimestamp;
		CapturedAtUtc = frame.CapturedAtUtc;
	}

	protected override void DisposeOwnedResources()
	{
		Interlocked.Exchange(
			ref _textureReference,
			null)?.Release();
	}
}
