using System;
using System.Threading;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Independent identity worker. It subscribes directly to immutable camera
/// outputs, processes at most one frame at a time, and never back-pressures
/// camera, MediaPipe, overlays, or the viewport.
/// </summary>
public sealed class IdentityModule :
	LatestValueModule<CameraOutput, IdentityOutput>,
	IPersonIdentityReviewService
{
	private readonly PersonIdentityMemory? _memory;

	private D3D12Nv12IdentityFrameReader? _textureReader;

	private string? _processingFailure;

	public bool IsAvailable => _memory?.IsAvailable ?? false;

	public string IdentityStatus =>
		Volatile.Read(ref _processingFailure)
		?? _memory?.Status
		?? "Identity module disabled";

	string IPersonIdentityReviewService.Status => IdentityStatus;

	public IdentityModule(
		IModuleOutputSource<CameraOutput> camera,
		string? outputFolder)
		: base(
			camera,
			"Identity independent latest-frame worker",
			ThreadPriority.BelowNormal)
	{
		if (!string.IsNullOrWhiteSpace(outputFolder))
		{
			_memory = new PersonIdentityMemory();
			_memory.ConfigureOutputFolder(outputFolder);
		}
	}

	public PersonIdentitySnapshot LatestIdentity =>
		_memory?.LatestSnapshot
		?? PersonIdentitySnapshot.Waiting;

	public IReadOnlyList<PersonIdentityReviewItem> GetIdentityReviewItems()
	{
		return _memory?.GetIdentityReviewItems()
			?? Array.Empty<PersonIdentityReviewItem>();
	}

	public IdentityReviewUpdateResult UpdateIdentityReview(
		IdentityReviewUpdate update)
	{
		return _memory?.UpdateIdentityReview(update)
			?? new IdentityReviewUpdateResult(
				false,
				"Identity module is disabled.");
	}

	public IdentityReviewUpdateResult ReplaceContextPhoto(
		string identityId,
		ReadOnlyMemory<byte> jpegBytes)
	{
		return _memory?.ReplaceContextPhoto(identityId, jpegBytes)
			?? new IdentityReviewUpdateResult(
				false,
				"Identity module is disabled.");
	}

	public IdentityReviewUpdateResult DeleteIdentity(string identityId)
	{
		return _memory?.DeleteIdentity(identityId)
			?? new IdentityReviewUpdateResult(
				false,
				"Identity module is disabled.");
	}

	public IdentityReviewUpdateResult BeginEnrollment(
		IdentityEnrollmentRequest request)
	{
		return _memory?.BeginEnrollment(request)
			?? new IdentityReviewUpdateResult(
				false,
				"Identity module is disabled.");
	}

	public IdentityReviewUpdateResult RequestEnrollmentCapture()
	{
		return _memory?.RequestEnrollmentCapture()
			?? new IdentityReviewUpdateResult(
				false,
				"Identity module is disabled.");
	}

	public IdentityEnrollmentState GetEnrollmentState()
	{
		return _memory?.GetEnrollmentState()
			?? IdentityEnrollmentState.Unavailable(
				"Identity module is disabled.");
	}

	public void CancelEnrollment()
	{
		_memory?.CancelEnrollment();
	}

	protected override IdentityOutput Process(CameraOutput input)
	{
		TextureNativeFrameLease source = input.OriginalFrame;
		PersonIdentitySnapshot identity =
			_memory?.LatestSnapshot
			?? PersonIdentitySnapshot.Waiting;
		if (_memory?.IsAvailable == true)
		{
			try
			{
				if (Observe(source))
				{
					Volatile.Write(ref _processingFailure, null);
					identity = _memory.LatestSnapshot;
				}
				else
				{
					const string status =
						"Identity waiting for a readable NV12 frame";
					Volatile.Write(ref _processingFailure, status);
					identity = WithoutCurrentPeople(
						identity,
						source.CapturedAtUtc,
						status);
				}
			}
			catch (Exception exception)
			{
				DisposeTextureReader();
				string status =
					"Identity skipped one frame: " + exception.Message;
				Volatile.Write(ref _processingFailure, status);
				identity = WithoutCurrentPeople(
					identity,
					source.CapturedAtUtc,
					status);
			}
		}

		return new IdentityOutput(input, identity);
	}

	protected override void OnProcessingFailure(Exception exception)
	{
		DisposeTextureReader();
		base.OnProcessingFailure(exception);
	}

	protected override void DisposeModule()
	{
		DisposeTextureReader();
		_memory?.Dispose();
	}

	private bool Observe(TextureNativeFrameLease frame)
	{
		PersonIdentityMemory memory = _memory
			?? throw new InvalidOperationException(
				"Identity memory is disabled.");
		if ((frame.Resource != 0
				&& frame.DeviceMode.StartsWith(
					"D3D12",
					StringComparison.OrdinalIgnoreCase))
			|| frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			if (_textureReader is null
				|| !_textureReader.CanRead(frame))
			{
				DisposeTextureReader();
				_textureReader =
					new D3D12Nv12IdentityFrameReader(frame);
			}
			using Mat bgr = _textureReader.ReadBgr(frame);
			memory.ObserveBgr(bgr, frame.CapturedAtUtc);
			return true;
		}

		byte[]? nv12 = frame.Nv12PreviewBytes;
		if (nv12 is null)
		{
			return false;
		}
		int chromaHeight = (frame.Height + 1) / 2;
		if (frame.Width <= 0
			|| frame.Height <= 0
			|| frame.Nv12PreviewStride < frame.Width
			|| nv12.Length < frame.Nv12PreviewStride
				* (frame.Height + chromaHeight))
		{
			return false;
		}
		using Mat nv12Frame = Mat.FromPixelData(
			frame.Height + chromaHeight,
			frame.Width,
			MatType.CV_8UC1,
			nv12,
			frame.Nv12PreviewStride);
		using Mat bgrFallback = new();
		Cv2.CvtColor(
			nv12Frame,
			bgrFallback,
			ColorConversionCodes.YUV2BGR_NV12);
		memory.ObserveBgr(bgrFallback, frame.CapturedAtUtc);
		return true;
	}

	private static PersonIdentitySnapshot WithoutCurrentPeople(
		PersonIdentitySnapshot latest,
		DateTime capturedAtUtc,
		string status)
	{
		return new PersonIdentitySnapshot(
			capturedAtUtc,
			Array.Empty<PersonIdentityObservation>(),
			latest.RememberedIdentityCount,
			latest.Backend,
			status);
	}

	private void DisposeTextureReader()
	{
		D3D12Nv12IdentityFrameReader? reader = _textureReader;
		_textureReader = null;
		reader?.Dispose();
	}
}
