using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Lock-free hot-path fan-out for one immutable module output. Subscription
/// changes use a private lock; publication only reads an immutable subscriber
/// array and performs atomic pointer operations on each independent slot.
/// </summary>
public sealed class ModuleOutputBroadcaster<TOutput> :
	IModuleOutputSource<TOutput>,
	IDisposable
	where TOutput : ModuleOutput, IModuleSnapshot
{
	private readonly object _subscriptionGate = new();

	private OutputSubscription[] _subscriptions = [];

	private int _disposed;

	public bool HasSubscribers =>
		Volatile.Read(ref _subscriptions).Length != 0;

	public bool CanAcceptAny
	{
		get
		{
			OutputSubscription[] subscriptions =
				Volatile.Read(ref _subscriptions);
			foreach (OutputSubscription subscription in subscriptions)
			{
				if (subscription.CanAccept)
				{
					return true;
				}
			}
			return false;
		}
	}

	public IModuleOutputSubscription<TOutput> Subscribe()
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _disposed) != 0,
			this);
		var subscription = new OutputSubscription(this);
		lock (_subscriptionGate)
		{
			ObjectDisposedException.ThrowIf(
				Volatile.Read(ref _disposed) != 0,
				this);
			OutputSubscription[] current = _subscriptions;
			var replacement =
				new OutputSubscription[current.Length + 1];
			Array.Copy(current, replacement, current.Length);
			replacement[^1] = subscription;
			Volatile.Write(ref _subscriptions, replacement);
		}
		return subscription;
	}

	/// <summary>
	/// Publishes a completed private working object. This call always consumes
	/// the caller's ownership reference. No subscriber callback executes here.
	/// </summary>
	public int Publish(TOutput completedOutput)
	{
		ArgumentNullException.ThrowIfNull(completedOutput);
		completedOutput.MarkPublished();
		int accepted = 0;
		try
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				return 0;
			}
			OutputSubscription[] subscriptions =
				Volatile.Read(ref _subscriptions);
			foreach (OutputSubscription subscription in subscriptions)
			{
				if (subscription.TryOffer(completedOutput))
				{
					accepted++;
				}
			}
			return accepted;
		}
		finally
		{
			completedOutput.Dispose();
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		OutputSubscription[] subscriptions;
		lock (_subscriptionGate)
		{
			subscriptions = _subscriptions;
			Volatile.Write(ref _subscriptions, []);
		}
		foreach (OutputSubscription subscription in subscriptions)
		{
			subscription.DisposeFromOwner();
		}
	}

	private void Remove(OutputSubscription subscription)
	{
		lock (_subscriptionGate)
		{
			OutputSubscription[] current = _subscriptions;
			int index = Array.IndexOf(current, subscription);
			if (index < 0)
			{
				return;
			}
			if (current.Length == 1)
			{
				Volatile.Write(ref _subscriptions, []);
				return;
			}
			var replacement =
				new OutputSubscription[current.Length - 1];
			if (index > 0)
			{
				Array.Copy(current, 0, replacement, 0, index);
			}
			if (index < current.Length - 1)
			{
				Array.Copy(
					current,
					index + 1,
					replacement,
					index,
					current.Length - index - 1);
			}
			Volatile.Write(ref _subscriptions, replacement);
		}
	}

	private sealed class OutputSubscription :
		IModuleOutputSubscription<TOutput>
	{
		private readonly ModuleOutputBroadcaster<TOutput> _owner;

		private readonly AutoResetEvent _available = new(false);

		private TOutput? _pending;

		private long _dropped;

		private int _disposed;

		internal bool CanAccept =>
			Volatile.Read(ref _disposed) == 0
			&& Volatile.Read(ref _pending) is null;

		public WaitHandle OutputAvailable => _available;

		public long DroppedOutputs =>
			Interlocked.Read(ref _dropped);

		internal OutputSubscription(
			ModuleOutputBroadcaster<TOutput> owner)
		{
			_owner = owner;
		}

		internal bool TryOffer(TOutput output)
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				return false;
			}
			if (!output.TryRetain())
			{
				return false;
			}
			if (Interlocked.CompareExchange(
				ref _pending,
				output,
				null) is not null)
			{
				output.Dispose();
				Interlocked.Increment(ref _dropped);
				return false;
			}
			if (Volatile.Read(ref _disposed) != 0)
			{
				Interlocked.Exchange(
					ref _pending,
					null)?.Dispose();
				return false;
			}
			try
			{
				_available.Set();
			}
			catch (ObjectDisposedException)
			{
				Interlocked.Exchange(
					ref _pending,
					null)?.Dispose();
				return false;
			}
			return true;
		}

		public bool TryTake(SnapshotCursor<TOutput> destination)
		{
			ArgumentNullException.ThrowIfNull(destination);
			destination.Release();
			if (Volatile.Read(ref _disposed) != 0)
			{
				return false;
			}
			TOutput? output =
				Interlocked.Exchange(ref _pending, null);
			if (output is null)
			{
				return false;
			}
			destination.Attach(output);
			return true;
		}

		public void Dispose()
		{
			if (DisposeCore())
			{
				_owner.Remove(this);
			}
		}

		internal void DisposeFromOwner()
		{
			DisposeCore();
		}

		private bool DisposeCore()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return false;
			}
			Interlocked.Exchange(ref _pending, null)?.Dispose();
			try
			{
				_available.Set();
			}
			catch (ObjectDisposedException)
			{
			}
			_available.Dispose();
			return true;
		}
	}
}
