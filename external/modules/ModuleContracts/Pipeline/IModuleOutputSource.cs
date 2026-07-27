using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// A module-owned immutable output broadcast. Each subscriber owns one
/// independent pending slot. A slow subscriber can drop only its own
/// unstarted arrivals and can never back-pressure its producer or peers.
/// </summary>
public interface IModuleOutputSource<TOutput>
	where TOutput : ModuleOutput, IModuleSnapshot
{
	IModuleOutputSubscription<TOutput> Subscribe();
}

public interface IModuleOutputSubscription<TOutput> : IDisposable
	where TOutput : ModuleOutput, IModuleSnapshot
{
	WaitHandle OutputAvailable { get; }

	long DroppedOutputs { get; }

	bool TryTake(SnapshotCursor<TOutput> destination);
}
