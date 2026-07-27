using System;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

public sealed record ModuleOutputBroadcasterSelfTestResult(
	bool Succeeded,
	string Detail);

public static class ModuleOutputBroadcasterSelfTest
{
	public static ModuleOutputBroadcasterSelfTestResult Run()
	{
		using var broadcaster =
			new ModuleOutputBroadcaster<TestOutput>();
		using IModuleOutputSubscription<TestOutput> fast =
			broadcaster.Subscribe();
		using IModuleOutputSubscription<TestOutput> slow =
			broadcaster.Subscribe();
		using var fastCursor = new SnapshotCursor<TestOutput>();
		using var slowCursor = new SnapshotCursor<TestOutput>();

		var first = new TestOutput(1);
		if (broadcaster.Publish(first) != 2
			|| !fast.TryTake(fastCursor)
			|| fastCursor.Current.SequenceId != 1)
		{
			return Fail("first output did not reach both subscribers");
		}

		var second = new TestOutput(2);
		if (broadcaster.Publish(second) != 1)
		{
			return Fail(
				"a subscriber with an occupied slot affected its peer");
		}
		using var secondFastCursor =
			new SnapshotCursor<TestOutput>();
		if (!fast.TryTake(secondFastCursor)
			|| secondFastCursor.Current.SequenceId != 2)
		{
			return Fail("fast subscriber did not receive the next output");
		}
		if (!slow.TryTake(slowCursor)
			|| slowCursor.Current.SequenceId != 1
			|| !ReferenceEquals(
				fastCursor.Current,
				slowCursor.Current))
		{
			return Fail(
				"subscribers did not receive the same immutable reference");
		}
		if (slow.DroppedOutputs != 1)
		{
			return Fail("slow subscriber drop count is incorrect");
		}

		return new ModuleOutputBroadcasterSelfTestResult(
			true,
			"two subscribers independently consumed one immutable output; " +
			"the slow subscriber dropped only its own next arrival");
	}

	private static ModuleOutputBroadcasterSelfTestResult Fail(
		string detail)
	{
		return new ModuleOutputBroadcasterSelfTestResult(
			false,
			detail);
	}

	private sealed class TestOutput :
		ModuleOutput,
		IModuleSnapshot
	{
		public long SequenceId { get; }

		public long ProducedAtTimestamp => SequenceId;

		public DateTime ProducedAtUtc => DateTime.UnixEpoch;

		internal TestOutput(long sequenceId)
		{
			SequenceId = sequenceId;
		}

		protected override void DisposeOwnedResources()
		{
		}
	}
}
