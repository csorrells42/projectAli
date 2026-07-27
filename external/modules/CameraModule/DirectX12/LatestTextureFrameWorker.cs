using System;
using System.Threading;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

/// <summary>
/// Runs optional frame processing on a dedicated thread. There is never a queue:
/// one frame may be in flight and every arrival while it is busy is dropped.
/// </summary>
internal sealed class LatestTextureFrameWorker : IDisposable
{
	private readonly object _acceptedFrameLock = new object();

	private readonly AutoResetEvent _frameReady = new AutoResetEvent(initialState: false);

	private readonly Action<TextureNativeFrameLease> _processor;

	private readonly Action<Exception>? _failureHandler;

	private readonly Thread _worker;

	private TextureNativeFrameLease? _acceptedFrame;

	private int _busy;

	private int _stopping;

	public LatestTextureFrameWorker(
		string threadName,
		Action<TextureNativeFrameLease> processor,
		ThreadPriority priority = ThreadPriority.BelowNormal,
		Action<Exception>? failureHandler = null)
	{
		_processor = processor ?? throw new ArgumentNullException(nameof(processor));
		_failureHandler = failureHandler;
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = threadName,
			Priority = priority
		};
		_worker.Start();
	}

	public bool TryAcceptPreviewData(TextureNativeFrameLease source)
	{
		return TryAccept(source, includeGpuTexture: false);
	}

	public bool TryAcceptTexture(TextureNativeFrameLease source)
	{
		return TryAccept(source, includeGpuTexture: true);
	}

	private bool TryAccept(
		TextureNativeFrameLease source,
		bool includeGpuTexture)
	{
		if (Volatile.Read(ref _stopping) != 0
			|| Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
		{
			return false;
		}

		TextureNativeFrameLease? accepted = null;
		bool handedOff = false;
		try
		{
			// This is a reference-counted handoff. It does not copy the frame.
			accepted = includeGpuTexture
				? source.Duplicate()
				: source.DuplicatePreviewData();
			if (accepted == null)
			{
				return false;
			}

			lock (_acceptedFrameLock)
			{
				if (Volatile.Read(ref _stopping) == 0 && _acceptedFrame == null)
				{
					_acceptedFrame = accepted;
					accepted = null;
					handedOff = true;
				}
			}

			if (handedOff)
			{
				_frameReady.Set();
			}
			return handedOff;
		}
		finally
		{
			accepted?.Dispose();
			if (!handedOff)
			{
				Interlocked.Exchange(ref _busy, 0);
			}
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0)
		{
			return;
		}

		_frameReady.Set();
		bool stopped = true;
		if (_worker != Thread.CurrentThread)
		{
			stopped = _worker.Join(TimeSpan.FromSeconds(3));
		}
		if (stopped)
		{
			_frameReady.Dispose();
		}
	}

	private void WorkerLoop()
	{
		while (true)
		{
			_frameReady.WaitOne();
			TextureNativeFrameLease? frame;
			bool stopping;
			lock (_acceptedFrameLock)
			{
				frame = _acceptedFrame;
				_acceptedFrame = null;
				stopping = Volatile.Read(ref _stopping) != 0;
			}

			if (frame == null)
			{
				Interlocked.Exchange(ref _busy, 0);
				if (stopping)
				{
					break;
				}
				continue;
			}

			try
			{
				_processor(frame);
			}
			catch (Exception ex)
			{
				try
				{
					_failureHandler?.Invoke(ex);
				}
				catch
				{
				}
			}
			finally
			{
				frame.Dispose();
				Interlocked.Exchange(ref _busy, 0);
			}
			if (stopping)
			{
				break;
			}
		}
	}
}
