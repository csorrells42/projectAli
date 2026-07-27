using System;
using System.Threading;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

/// <summary>
/// Owns one optional CPU camera frame at a time. Arrivals while busy are
/// rejected before copying or conversion.
/// </summary>
internal sealed class LatestCameraFrameWorker : IDisposable
{
	private readonly object _acceptedFrameLock = new();

	private readonly AutoResetEvent _frameReady = new(initialState: false);

	private readonly Action<CameraFrame> _processor;

	private readonly Thread _worker;

	private CameraFrame? _acceptedFrame;

	private int _busy;

	private int _stopping;

	public LatestCameraFrameWorker(
		string threadName,
		Action<CameraFrame> processor,
		ThreadPriority priority = ThreadPriority.BelowNormal)
	{
		_processor = processor
			?? throw new ArgumentNullException(nameof(processor));
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = threadName,
			Priority = priority
		};
		_worker.Start();
	}

	public bool TryAccept(CameraFrame source)
	{
		if (Volatile.Read(ref _stopping) != 0
			|| Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
		{
			return false;
		}
		CameraFrame? accepted = null;
		bool handedOff = false;
		try
		{
			accepted = source.Duplicate();
			lock (_acceptedFrameLock)
			{
				if (Volatile.Read(ref _stopping) == 0
					&& _acceptedFrame is null)
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
		bool stopped = _worker == Thread.CurrentThread
			|| _worker.Join(TimeSpan.FromSeconds(3));
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
			CameraFrame? frame;
			bool stopping;
			lock (_acceptedFrameLock)
			{
				frame = _acceptedFrame;
				_acceptedFrame = null;
				stopping = Volatile.Read(ref _stopping) != 0;
			}
			if (frame is null)
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
			catch
			{
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
