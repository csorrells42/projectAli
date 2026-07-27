using System;
using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Base for an isolated event-driven stage. The worker sleeps until its
/// predecessor publishes, owns one reusable cursor, accepts only the newest
/// completed frame, and never queues work.
/// </summary>
public abstract class LatestFrameStage<TInput, TOutput> :
	ILatestFrameProducer<TOutput>,
	IFramePublicationSource,
	IFrameModuleTimingSource,
	IVisionModule,
	IDisposable
	where TInput : ModuleOutput, IFramePipelineSnapshot
	where TOutput : ModuleOutput, IFramePipelineSnapshot
{
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

	private readonly ILatestFrameProducer<TInput> _input;

	private readonly LatestFramePublisher<TOutput> _output;

	private readonly ManualResetEvent _stopSignal =
		new(initialState: false);

	private readonly WaitHandle[] _wakeSignals;

	private readonly Thread _worker;

	private readonly FrameModuleTiming _timing;

	private int _started;

	private int _stopping;

	private long _completedFrames;

	private long _skippedFrames;

	private long _failedFrames;

	private long _lastCompletedTimestamp;

	private string _status = "waiting";

	protected LatestFrameStage(
		ILatestFrameProducer<TInput> input,
		string workerName,
		ThreadPriority priority = ThreadPriority.AboveNormal)
	{
		_input = input ?? throw new ArgumentNullException(nameof(input));
		IFramePublicationSource publicationSource =
			input as IFramePublicationSource
			?? throw new ArgumentException(
				"The input producer does not provide event-driven publication.",
				nameof(input));
		_output = new LatestFramePublisher<TOutput>();
		_wakeSignals =
		[
			publicationSource.FramePublishedSignal,
			_stopSignal
		];
		_timing = new FrameModuleTiming();
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = workerName,
			Priority = priority
		};
	}

	public long CompletedFrames => Interlocked.Read(ref _completedFrames);

	public long SkippedFrames => Interlocked.Read(ref _skippedFrames);

	public long FailedFrames => Interlocked.Read(ref _failedFrames);

	public long LastCompletedTimestamp =>
		Volatile.Read(ref _lastCompletedTimestamp);

	public string Status => Volatile.Read(ref _status);

	public TimeSpan TimeWaited => _timing.TimeWaited;

	public TimeSpan TimeWorked => _timing.TimeWorked;

	WaitHandle IFramePublicationSource.FramePublishedSignal =>
		((IFramePublicationSource)_output).FramePublishedSignal;

	public void Start()
	{
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_worker.Start();
		}
	}

	public TimeSpan GetIdleTime()
	{
		return _timing.TimeWaited;
	}

	public TimeSpan GetWorkingTime()
	{
		return _timing.TimeWorked;
	}

	bool ILatestFrameProducer<TOutput>.TryGetLatest(
		long afterFrameId,
		SnapshotCursor<TOutput> destination)
	{
		return GetLatestOutput(afterFrameId, destination);
	}

	protected bool GetLatestOutput(
		long afterFrameId,
		SnapshotCursor<TOutput> destination)
	{
		return _output.TryGetLatest(afterFrameId, destination);
	}

	protected abstract TOutput? Process(TInput input);

	protected virtual void OnProcessingFailure(Exception exception)
	{
		Volatile.Write(ref _status, exception.Message);
	}

	protected virtual void DisposeStage()
	{
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0)
		{
			return;
		}

		_stopSignal.Set();
		if (Volatile.Read(ref _started) != 0
			&& _worker != Thread.CurrentThread)
		{
			_worker.Join(StopTimeout);
		}

		_output.Dispose();
		_stopSignal.Dispose();
		DisposeStage();
		GC.SuppressFinalize(this);
	}

	private void WorkerLoop()
	{
		using SnapshotCursor<TInput> cursor = new();
		long lastStartedFrameId = 0;

		while (Volatile.Read(ref _stopping) == 0)
		{
			int signalIndex;
			try
			{
				signalIndex = WaitHandle.WaitAny(_wakeSignals);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			if (signalIndex == 1
				|| Volatile.Read(ref _stopping) != 0)
			{
				break;
			}
			if (!_output.WaitForEmptySlot(_stopSignal))
			{
				break;
			}
			if (!_input.TryGetLatest(lastStartedFrameId, cursor))
			{
				continue;
			}

			TInput input = cursor.Current;
			long frameId = input.FrameId;
			_timing.WorkStarted(Stopwatch.GetTimestamp());
			if (lastStartedFrameId != 0
				&& frameId > lastStartedFrameId + 1)
			{
				Interlocked.Add(
					ref _skippedFrames,
					frameId - lastStartedFrameId - 1);
			}
			lastStartedFrameId = frameId;

			try
			{
				TOutput? result = Process(input);
				if (result is null)
				{
					continue;
				}
				if (result.FrameId != frameId)
				{
					result.Dispose();
					throw new InvalidOperationException(
						$"{GetType().Name} produced frame {result.FrameId} " +
						$"from input frame {frameId}.");
				}
				if (_output.Publish(result))
				{
					long frameOutTimestamp =
						Stopwatch.GetTimestamp();
					_timing.FrameMovedOut(frameOutTimestamp);
					Interlocked.Increment(ref _completedFrames);
					Volatile.Write(
						ref _lastCompletedTimestamp,
						frameOutTimestamp);
					Volatile.Write(ref _status, "active");
				}
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref _failedFrames);
				OnProcessingFailure(ex);
			}
			finally
			{
				cursor.Release();
			}
		}
	}
}
