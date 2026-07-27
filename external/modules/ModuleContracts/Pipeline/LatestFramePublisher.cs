using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// One immutable output slot. Publish performs one atomic pointer change from
/// empty to the completed working object. The single downstream consumer
/// atomically takes that exact pointer and the slot becomes empty. There is no
/// queue, ring, node pool, replacement, or output copy.
/// </summary>
public sealed class LatestFramePublisher<TOutput> :
	ILatestFrameProducer<TOutput>,
	IFramePublicationSource,
	IDisposable
	where TOutput : ModuleOutput, IFramePipelineSnapshot
{
	private readonly AutoResetEvent _framePublished =
		new(initialState: false);

	private readonly AutoResetEvent _outputTaken =
		new(initialState: false);

	private TOutput? _output;

	private int _disposed;

	WaitHandle IFramePublicationSource.FramePublishedSignal =>
		_framePublished;

	public long PublishedFrameId
	{
		get
		{
			return Volatile.Read(ref _output)?.FrameId ?? 0L;
		}
	}

	internal bool HasEmptySlot =>
		Volatile.Read(ref _disposed) == 0
		&& Volatile.Read(ref _output) is null;

	/// <summary>
	/// Transfers the caller's only working ownership reference into the single
	/// empty output slot. This method performs exactly one atomic output pointer
	/// change.
	/// Whether it succeeds or fails, the caller no longer owns the object.
	/// </summary>
	public bool Publish(TOutput completedWorkingObject)
	{
		ArgumentNullException.ThrowIfNull(completedWorkingObject);
		completedWorkingObject.MarkPublished();
		if (Volatile.Read(ref _disposed) != 0)
		{
			completedWorkingObject.Dispose();
			return false;
		}

		TOutput? occupied = Interlocked.CompareExchange(
			ref _output,
			completedWorkingObject,
			null);
		if (occupied is not null)
		{
			completedWorkingObject.Dispose();
			return false;
		}

		if (Volatile.Read(ref _disposed) != 0)
		{
			if (Interlocked.CompareExchange(
				ref _output,
				null,
				completedWorkingObject) == completedWorkingObject)
			{
				completedWorkingObject.Dispose();
			}
			return false;
		}

		SignalFramePublished();
		return true;
	}

	public bool TryGetLatest(
		long afterFrameId,
		SnapshotCursor<TOutput> destination)
	{
		ArgumentNullException.ThrowIfNull(destination);
		destination.Release();

		if (Volatile.Read(ref _disposed) == 0)
		{
			TOutput? output = Interlocked.Exchange(
				ref _output,
				null);
			if (output is null)
			{
				return false;
			}
			SignalOutputTaken();
			if (output.FrameId <= afterFrameId)
			{
				output.Dispose();
				return false;
			}

			destination.Attach(output);
			return true;
		}

		return false;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		SignalFramePublished();
		SignalOutputTaken();
		Interlocked.Exchange(ref _output, null)?.Dispose();
		_framePublished.Dispose();
		_outputTaken.Dispose();
	}

	internal bool WaitForEmptySlot(WaitHandle stopSignal)
	{
		while (Volatile.Read(ref _disposed) == 0
			&& Volatile.Read(ref _output) is not null)
		{
			int signal = WaitHandle.WaitAny(
				[_outputTaken, stopSignal]);
			if (signal == 1)
			{
				return false;
			}
		}
		return Volatile.Read(ref _disposed) == 0;
	}

	private void SignalFramePublished()
	{
		try
		{
			_framePublished.Set();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void SignalOutputTaken()
	{
		try
		{
			_outputTaken.Set();
		}
		catch (ObjectDisposedException)
		{
		}
	}
}
