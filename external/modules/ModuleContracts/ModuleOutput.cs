using System;
using System.Threading;

namespace AvatarBuilder.Modules.Contracts;

/// <summary>
/// Intrusive ownership for one immutable module output object. The object
/// begins as the producing module's private working result. Publish transfers
/// that single ownership reference into the module's one output slot. Readers
/// may retain the same object; no copy or publication node is created.
/// </summary>
public abstract class ModuleOutput : IDisposable
{
	private int _referenceCount = 1;

	private int _published;

	private int _disposed;

	internal void MarkPublished()
	{
		if (Interlocked.CompareExchange(ref _published, 1, 0) != 0)
		{
			throw new InvalidOperationException(
				"A module output may be published only once.");
		}
	}

	internal bool TryRetain()
	{
		while (true)
		{
			int references = Volatile.Read(ref _referenceCount);
			if (references <= 0)
			{
				return false;
			}
			if (references == int.MaxValue)
			{
				throw new InvalidOperationException(
					"A module output has too many readers.");
			}
			if (Interlocked.CompareExchange(
				ref _referenceCount,
				references + 1,
				references) == references)
			{
				return true;
			}
		}
	}

	internal void RetainForDownstream()
	{
		if (!TryRetain())
		{
			throw new ObjectDisposedException(
				GetType().Name,
				"An output cannot be retained after its owner released it.");
		}
	}

	public void Dispose()
	{
		int remaining = Interlocked.Decrement(ref _referenceCount);
		if (remaining > 0)
		{
			return;
		}
		if (remaining < 0)
		{
			throw new InvalidOperationException(
				"A module output reference was released twice.");
		}
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			DisposeOwnedResources();
		}
	}

	protected abstract void DisposeOwnedResources();
}
