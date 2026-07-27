using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.Ffmpeg;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.Pipeline;

public sealed class CameraPreviewService : ICameraPreviewService, IDisposable
{
	private readonly MediaFoundationBitmapCameraPreviewService _mediaFoundation = new MediaFoundationBitmapCameraPreviewService();

	private readonly FfmpegCameraPreviewService _ffmpeg = new FfmpegCameraPreviewService();

	private ICameraPreviewService? _activeService;

	private int _maxOutputWidth = 960;

	private double _maxOutputFramesPerSecond = 1000.0;

	public bool IsAvailable
	{
		get
		{
			if (!_mediaFoundation.IsAvailable)
			{
				return _ffmpeg.IsAvailable;
			}
			return true;
		}
	}

	public bool BitmapFramesEnabled
	{
		get
		{
			return _ffmpeg.BitmapFramesEnabled;
		}
		set
		{
			_ffmpeg.BitmapFramesEnabled = value;
		}
	}

	public int MaxOutputWidth
	{
		get
		{
			return _maxOutputWidth;
		}
		set
		{
			_maxOutputWidth = value;
			_mediaFoundation.MaxOutputWidth = value;
			_ffmpeg.MaxOutputWidth = value;
		}
	}

	public double MaxOutputFramesPerSecond
	{
		get
		{
			return _maxOutputFramesPerSecond;
		}
		set
		{
			_maxOutputFramesPerSecond = value;
			_mediaFoundation.MaxOutputFramesPerSecond = value;
			_ffmpeg.MaxOutputFramesPerSecond = value;
		}
	}

	public event EventHandler<BitmapSource>? FrameAvailable;

	public event EventHandler<CameraFrame>? CameraFrameAvailable;

	public event EventHandler<string>? StatusChanged;

	public CameraPreviewService()
	{
		_mediaFoundation.FrameAvailable += ForwardFrameAvailable;
		_mediaFoundation.CameraFrameAvailable += ForwardCameraFrameAvailable;
		_mediaFoundation.StatusChanged += ForwardStatusChanged;
		_ffmpeg.FrameAvailable += ForwardFrameAvailable;
		_ffmpeg.CameraFrameAvailable += ForwardCameraFrameAvailable;
		_ffmpeg.StatusChanged += ForwardStatusChanged;
	}

	public async Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default(CancellationToken))
	{
		Stop();
		ApplySettings();
		bool flag;
		if (string.Equals(camera.Source, "DirectShow", StringComparison.OrdinalIgnoreCase))
		{
			this.StatusChanged?.Invoke(this, "Opening DirectShow camera path for " + camera.DisplayName + "...");
			flag = _ffmpeg.IsAvailable;
			if (flag)
			{
				flag = await StartDirectShowAsync(camera, mode, cancellationToken);
			}
			if (flag)
			{
				_activeService = _ffmpeg;
				this.StatusChanged?.Invoke(this, "Camera active through the DirectShow capture path.");
				return true;
			}
			_activeService = null;
			return false;
		}
		if (_mediaFoundation.IsAvailable)
		{
			this.StatusChanged?.Invoke(this, "Trying Windows Media Foundation camera path for " + camera.DisplayName + "...");
			if (await _mediaFoundation.StartAsync(camera, mode, cancellationToken))
			{
				_activeService = _mediaFoundation;
				this.StatusChanged?.Invoke(this, "Camera active through Windows Media Foundation.");
				return true;
			}
			_mediaFoundation.Stop();
			this.StatusChanged?.Invoke(this, "Media Foundation camera path failed; trying bundled FFmpeg fallback.");
		}
		CameraDevice camera2 = camera.DirectShowDeviceOrSelf();
		flag = _ffmpeg.IsAvailable;
		if (flag)
		{
			flag = await StartDirectShowAsync(camera2, mode, cancellationToken);
		}
		if (flag)
		{
			_activeService = _ffmpeg;
			this.StatusChanged?.Invoke(this, "Camera active through bundled FFmpeg fallback.");
			return true;
		}
		_activeService = null;
		return false;
	}

	private async Task<bool> StartDirectShowAsync(CameraDevice camera, CameraVideoMode? requestedMode, CancellationToken cancellationToken)
	{
		foreach (CameraVideoMode? item in CreateDirectShowModeAttempts(requestedMode))
		{
			if (item is not null && item != requestedMode)
			{
				this.StatusChanged?.Invoke(this, "Requested camera format did not open; retrying " + item.Label + " without lowering resolution.");
			}
			if (await _ffmpeg.StartAsync(camera, item, cancellationToken))
			{
				return true;
			}
		}
		return false;
	}

	private static IEnumerable<CameraVideoMode?> CreateDirectShowModeAttempts(CameraVideoMode? requestedMode)
	{
		yield return requestedMode;
		if (requestedMode == null || requestedMode.IsAuto)
		{
			yield break;
		}
		int? width = requestedMode.Width;
		if (!width.HasValue)
		{
			yield break;
		}
		int width2 = width.GetValueOrDefault();
		int? height = requestedMode.Height;
		if (!height.HasValue)
		{
			yield break;
		}
		int height2 = height.GetValueOrDefault();
		double? framesPerSecond = requestedMode.FramesPerSecond;
		if (framesPerSecond.HasValue)
		{
			double framesPerSecond2 = framesPerSecond.GetValueOrDefault();
			if (!string.Equals(requestedMode.InputFormat, "yuyv422", StringComparison.OrdinalIgnoreCase) && !string.Equals(requestedMode.InputFormat, "yuy2", StringComparison.OrdinalIgnoreCase))
			{
				yield return new CameraVideoMode($"{width2}x{height2} @ {framesPerSecond2:0.###} fps (YUYV422)", width2, height2, framesPerSecond2, "yuyv422");
			}
			if (!string.IsNullOrWhiteSpace(requestedMode.InputFormat))
			{
				yield return new CameraVideoMode($"{width2}x{height2} @ {framesPerSecond2:0.###} fps (driver-selected format)", width2, height2, framesPerSecond2, null);
			}
		}
	}

	public void Stop()
	{
		if (_activeService != null)
		{
			_activeService.Stop();
			_activeService = null;
		}
		else
		{
			_mediaFoundation.Stop();
			_ffmpeg.Stop();
		}
	}

	public void Dispose()
	{
		Stop();
		_mediaFoundation.FrameAvailable -= ForwardFrameAvailable;
		_mediaFoundation.CameraFrameAvailable -= ForwardCameraFrameAvailable;
		_mediaFoundation.StatusChanged -= ForwardStatusChanged;
		_ffmpeg.FrameAvailable -= ForwardFrameAvailable;
		_ffmpeg.CameraFrameAvailable -= ForwardCameraFrameAvailable;
		_ffmpeg.StatusChanged -= ForwardStatusChanged;
		_mediaFoundation.Dispose();
		_ffmpeg.Dispose();
	}

	private void ApplySettings()
	{
		_mediaFoundation.MaxOutputWidth = _maxOutputWidth;
		_mediaFoundation.MaxOutputFramesPerSecond = _maxOutputFramesPerSecond;
		_ffmpeg.MaxOutputWidth = _maxOutputWidth;
		_ffmpeg.MaxOutputFramesPerSecond = _maxOutputFramesPerSecond;
	}

	private void ForwardFrameAvailable(object? sender, BitmapSource frame)
	{
		this.FrameAvailable?.Invoke(this, frame);
	}

	private void ForwardCameraFrameAvailable(object? sender, CameraFrame frame)
	{
		this.CameraFrameAvailable?.Invoke(this, frame);
	}

	private void ForwardStatusChanged(object? sender, string status)
	{
		this.StatusChanged?.Invoke(this, status);
	}
}
