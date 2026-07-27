using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// A reader-owned reference to the exact immutable object in a module's one
/// output slot. Releasing the cursor never returns the object to its producer.
/// </summary>
public sealed class SnapshotCursor<TOutput> : IDisposable
	where TOutput : ModuleOutput
{
	private TOutput? _current;

	public bool HasValue => Volatile.Read(ref _current) is not null;

	public TOutput Current =>
		Volatile.Read(ref _current)
		?? throw new InvalidOperationException(
			"The cursor does not own a module output.");

	internal void Attach(TOutput output)
	{
		Release();
		Volatile.Write(ref _current, output);
	}

	public void Release()
	{
		Interlocked.Exchange(ref _current, null)?.Dispose();
	}

	public void Dispose()
	{
		Release();
	}
}
