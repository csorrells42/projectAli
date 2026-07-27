using System;
using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Event-driven one-in-flight audio worker. Each module owns one input slot,
/// never queues arrivals, and never waits for downstream subscribers.
/// </summary>
public abstract class LatestValueAudioModule<TInput, TOutput> :
	IAudioModule,
	IModuleOutputSource<TOutput>,
	IDisposable
	where TInput : ModuleOutput, IModuleSnapshot
	where TOutput : ModuleOutput, IModuleSnapshot
{
	private readonly IModuleOutputSubscription<TInput> _input;
	private readonly ModuleOutputBroadcaster<TOutput> _output = new();
	private readonly ManualResetEvent _stop = new(false);
	private readonly WaitHandle[] _signals;
	private readonly Thread _worker;
	private readonly FrameModuleTiming _timing = new();
	private int _started;
	private int _stopping;
	private long _completed;
	private long _failed;
	private string _status = "waiting";

	protected LatestValueAudioModule(
		IModuleOutputSource<TInput> input,
		string workerName,
		ThreadPriority priority = ThreadPriority.AboveNormal)
	{
		ArgumentNullException.ThrowIfNull(input);
		_input = input.Subscribe();
		_signals = [_input.OutputAvailable, _stop];
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = workerName,
			Priority = priority
		};
	}

	public long CompletedOutputs => Interlocked.Read(ref _completed);
	public long FailedOutputs => Interlocked.Read(ref _failed);
	public long DroppedInputs => _input.DroppedOutputs;
	public string Status => Volatile.Read(ref _status);

	public void Start()
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _stopping) != 0,
			this);
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_worker.Start();
		}
	}

	public TimeSpan GetIdleTime() => _timing.TimeWaited;
	public TimeSpan GetWorkingTime() => _timing.TimeWorked;
	public IModuleOutputSubscription<TOutput> Subscribe() =>
		_output.Subscribe();

	protected abstract TOutput? Process(TInput input);

	protected virtual void OnProcessingFailure(Exception exception)
	{
		Volatile.Write(ref _status, exception.Message);
	}

	protected virtual void DisposeModule()
	{
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0)
		{
			return;
		}
		_stop.Set();
		if (Volatile.Read(ref _started) != 0
			&& _worker != Thread.CurrentThread)
		{
			_worker.Join(TimeSpan.FromSeconds(3));
		}
		_input.Dispose();
		_output.Dispose();
		_stop.Dispose();
		DisposeModule();
		GC.SuppressFinalize(this);
	}

	private void WorkerLoop()
	{
		using SnapshotCursor<TInput> cursor = new();
		while (Volatile.Read(ref _stopping) == 0)
		{
			int signal;
			try
			{
				signal = WaitHandle.WaitAny(_signals);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			if (signal == 1 || Volatile.Read(ref _stopping) != 0)
			{
				break;
			}
			if (!_input.TryTake(cursor))
			{
				continue;
			}
			_timing.WorkStarted(Stopwatch.GetTimestamp());
			try
			{
				TOutput? output = Process(cursor.Current);
				if (output is null)
				{
					continue;
				}
				_output.Publish(output);
				_timing.FrameMovedOut(Stopwatch.GetTimestamp());
				Interlocked.Increment(ref _completed);
				Volatile.Write(ref _status, "active");
			}
			catch (Exception exception)
			{
				Interlocked.Increment(ref _failed);
				OnProcessingFailure(exception);
			}
			finally
			{
				cursor.Release();
			}
		}
	}
}
