using System;
using System.IO;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal sealed class MediaFoundationTextureVideoRecorder : IDisposable
{
	private readonly object _lock = new object();

	private readonly IMFDXGIDeviceManager _deviceManager;

	private IMFSinkWriter? _writer;

	private int _streamIndex;

	private bool _isFinalized;

	public string Path { get; }

	public int Width { get; }

	public int Height { get; }

	public double FramesPerSecond { get; }

	public Guid InputSubtype { get; }

	public int SamplesWritten { get; private set; }

	public MediaFoundationTextureVideoRecorder(string path, int width, int height, double framesPerSecond, Guid inputSubtype, IMFDXGIDeviceManager deviceManager)
	{
		Path = path;
		Width = width;
		Height = height;
		FramesPerSecond = Math.Clamp(framesPerSecond, 1.0, 120.0);
		InputSubtype = inputSubtype;
		_deviceManager = deviceManager;
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
		InitializeWriter();
	}

	public void WriteSample(IMFSample sample)
	{
		lock (_lock)
		{
			if (_writer != null && !_isFinalized)
			{
				MediaFoundationInterop.ThrowIfFailed(_writer.WriteSample(_streamIndex, sample));
				SamplesWritten++;
			}
		}
	}

	public void Stop()
	{
		lock (_lock)
		{
			if (_writer == null || _isFinalized)
			{
				return;
			}
			try
			{
				_isFinalized = true;
				int result = _writer.Finalize_();
				if (MediaFoundationInterop.Failed(result) && SamplesWritten > 0)
				{
					MediaFoundationInterop.ThrowIfFailed(result);
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(_writer);
				_writer = null;
			}
		}
	}

	public void Dispose()
	{
		Stop();
	}

	private void InitializeWriter()
	{
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 2));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
			MediaFoundationInterop.ThrowIfFailed(attributes.SetUnknown(in MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER, _deviceManager));
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSinkWriterFromURL(Path, IntPtr.Zero, attributes, out _writer));
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(attributes);
		}
		IMFMediaType? mediaType = null;
		IMFMediaType? mediaType2 = null;
		try
		{
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out mediaType));
			ConfigureVideoType(mediaType, MediaFoundationGuids.MFVideoFormat_H264, EstimateBitrate(), includePixelAspect: true);
			MediaFoundationInterop.ThrowIfFailed(_writer.AddStream(mediaType, out _streamIndex));
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out mediaType2));
			ConfigureVideoType(mediaType2, InputSubtype, null, includePixelAspect: true);
			MediaFoundationInterop.ThrowIfFailed(_writer.SetInputMediaType(_streamIndex, mediaType2, null));
			MediaFoundationInterop.ThrowIfFailed(_writer.BeginWriting());
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
			MediaFoundationInterop.ReleaseComObject(mediaType2);
		}
	}

	private void ConfigureVideoType(IMFMediaType mediaType, Guid subtype, int? bitrate, bool includePixelAspect)
	{
		var (numerator, denominator) = CreateFrameRateRatio(FramesPerSecond);
		MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_MAJOR_TYPE, in MediaFoundationGuids.MFMediaType_Video));
		MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, in subtype));
		MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_SIZE, MediaFoundationInterop.PackRatio(Width, Height)));
		MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_RATE, MediaFoundationInterop.PackRatio(numerator, denominator)));
		MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_INTERLACE_MODE, 2));
		if (includePixelAspect)
		{
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_PIXEL_ASPECT_RATIO, MediaFoundationInterop.PackRatio(1, 1)));
		}
		if (bitrate.HasValue)
		{
			int valueOrDefault = bitrate.GetValueOrDefault();
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_AVG_BITRATE, valueOrDefault));
		}
	}

	private int EstimateBitrate()
	{
		double num = (double)(Width * Height) / 1000000.0;
		double num2 = FramesPerSecond / 30.0;
		return (int)Math.Round(Math.Clamp(num * num2 * 5500000.0, 8000000.0, 64000000.0));
	}

	private static (int Numerator, int Denominator) CreateFrameRateRatio(double fps)
	{
		if (Math.Abs(fps - 29.97) < 0.02)
		{
			return (Numerator: 30000, Denominator: 1001);
		}
		if (Math.Abs(fps - 59.94) < 0.02)
		{
			return (Numerator: 60000, Denominator: 1001);
		}
		return (Numerator: (int)Math.Round(Math.Clamp(fps, 1.0, 240.0)), Denominator: 1);
	}
}
