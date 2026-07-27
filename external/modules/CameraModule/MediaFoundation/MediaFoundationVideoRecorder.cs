using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal sealed class MediaFoundationVideoRecorder : IDisposable
{
	private readonly object _lock = new object();

	private readonly int _frameBytes;

	private readonly IMFDXGIDeviceManager? _d3dManager;

	private IMFSinkWriter? _writer;

	private int _streamIndex;

	private long _nextSampleTime;

	private int _samplesWritten;

	private int _framesOffered;

	private int _framesSkipped;

	private bool _isPaused;

	private bool _isFinalized;

	public string Path { get; }

	public int Width { get; }

	public int Height { get; }

	public int Stride { get; }

	public double FramesPerSecond { get; }

	public int SamplesWritten => _samplesWritten;

	public int FramesOffered => _framesOffered;

	public int FramesSkipped => _framesSkipped;

	public string? LastSkipReason { get; private set; }

	private long SampleDuration => Math.Max(1L, (long)Math.Round(10000000.0 / FramesPerSecond));

	public MediaFoundationVideoRecorder(string path, int width, int height, double framesPerSecond, IMFDXGIDeviceManager? d3dManager)
	{
		Path = path;
		Width = width;
		Height = height;
		FramesPerSecond = Math.Clamp(framesPerSecond, 1.0, 120.0);
		Stride = Width * 4;
		_frameBytes = Stride * Height;
		_d3dManager = d3dManager;
		_nextSampleTime = 0L;
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
		InitializeWriter();
	}

	public void Pause()
	{
		lock (_lock)
		{
			_isPaused = true;
		}
	}

	public void Resume()
	{
		lock (_lock)
		{
			_isPaused = false;
		}
	}

	public bool WriteFrame(byte[] bgraBytes)
	{
		_framesOffered++;
		if (bgraBytes.Length < _frameBytes)
		{
			_framesSkipped++;
			LastSkipReason = $"frame buffer too small ({bgraBytes.Length} < {_frameBytes})";
			return false;
		}
		lock (_lock)
		{
			if (_writer == null)
			{
				_framesSkipped++;
				LastSkipReason = "writer is not initialized";
				return false;
			}
			if (_isPaused)
			{
				_framesSkipped++;
				LastSkipReason = "recording is paused";
				return false;
			}
			if (_isFinalized)
			{
				_framesSkipped++;
				LastSkipReason = "writer is already finalized";
				return false;
			}
			IMFMediaBuffer? buffer = null;
			IMFSample? sample = null;
			try
			{
				MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMemoryBuffer(_frameBytes, out buffer));
				MediaFoundationInterop.ThrowIfFailed(buffer.Lock(out var buffer2, out var _, out var _));
				try
				{
					Marshal.Copy(bgraBytes, 0, buffer2, _frameBytes);
				}
				finally
				{
					buffer.Unlock();
				}
				MediaFoundationInterop.ThrowIfFailed(buffer.SetCurrentLength(_frameBytes));
				MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSample(out sample));
				MediaFoundationInterop.ThrowIfFailed(sample.AddBuffer(buffer));
				MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
				MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(SampleDuration));
				MediaFoundationInterop.ThrowIfFailed(sample.SetUINT32(in MediaFoundationGuids.MFSampleExtension_CleanPoint, 1));
				MediaFoundationInterop.ThrowIfFailed(_writer.WriteSample(_streamIndex, sample));
				_samplesWritten++;
				LastSkipReason = null;
				_nextSampleTime += SampleDuration;
				return true;
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(sample);
				MediaFoundationInterop.ReleaseComObject(buffer);
			}
		}
	}

	public string Stop()
	{
		lock (_lock)
		{
			if (_writer == null || _isFinalized)
			{
				return Path;
			}
			try
			{
				_isFinalized = true;
				int result = _writer.Finalize_();
				if (MediaFoundationInterop.Failed(result) && _samplesWritten > 0)
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
		return Path;
	}

	public void Dispose()
	{
		Stop();
	}

	private void InitializeWriter()
	{
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 1));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
			if (_d3dManager != null)
			{
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUnknown(in MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER, _d3dManager));
			}
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
			ConfigureVideoType(mediaType2, MediaFoundationGuids.MFVideoFormat_RGB32, null, includePixelAspect: true);
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
		if (subtype == MediaFoundationGuids.MFVideoFormat_RGB32)
		{
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_DEFAULT_STRIDE, Stride));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_FIXED_SIZE_SAMPLES, 1));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_ALL_SAMPLES_INDEPENDENT, 1));
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
