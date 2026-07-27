using System;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

public sealed record LatestFramePublisherSelfTestResult(
	bool Passed,
	string Status,
	long Published,
	long Read,
	long Disposed);

public static class LatestFramePublisherSelfTest
{
	public static LatestFramePublisherSelfTestResult Run()
	{
		long disposed = 0;
		using LatestFramePublisher<TestSnapshot> publisher = new();
		var first = new TestSnapshot(1, () => disposed++);
		var rejected = new TestSnapshot(2, () => disposed++);
		bool firstPublished = publisher.Publish(first);
		bool occupiedRejected = !publisher.Publish(rejected);
		bool rejectedDisposed = disposed == 1;

		using var cursor = new SnapshotCursor<TestSnapshot>();
		bool exactPointerTransferred =
			publisher.TryGetLatest(0, cursor)
			&& ReferenceEquals(first, cursor.Current);
		bool slotEmptyAfterTake = publisher.PublishedFrameId == 0;
		cursor.Release();
		bool firstDisposedAfterConsumerRelease = disposed == 2;

		var next = new TestSnapshot(3, () => disposed++);
		bool nextPublished = publisher.Publish(next);
		bool staleTakeRejected =
			!publisher.TryGetLatest(3, cursor);
		bool nextDisposedOnRejectedTake = disposed == 3;

		bool republishRejected = false;
		try
		{
			publisher.Publish(first);
		}
		catch (InvalidOperationException)
		{
			republishRejected = true;
		}

		bool passed =
			firstPublished
			&& occupiedRejected
			&& rejectedDisposed
			&& exactPointerTransferred
			&& slotEmptyAfterTake
			&& firstDisposedAfterConsumerRelease
			&& nextPublished
			&& staleTakeRejected
			&& nextDisposedOnRejectedTake
			&& republishRejected;
		return new LatestFramePublisherSelfTestResult(
			passed,
			passed
				? "PASS: the one-slot publisher transfers the exact object, never replaces an occupied output, releases ownership deterministically, and rejects republishing."
				: $"FAIL: first={firstPublished}, occupiedRejected={occupiedRejected}, exactPointer={exactPointerTransferred}, slotEmpty={slotEmptyAfterTake}, staleRejected={staleTakeRejected}, republishRejected={republishRejected}, disposed={disposed}.",
			3,
			exactPointerTransferred ? 1 : 0,
			disposed);
	}

	private sealed class TestSnapshot :
		ModuleOutput,
		IFramePipelineSnapshot
	{
		private readonly Action _onDispose;

		public long FrameId { get; }

		public long CapturedAtTimestamp => FrameId;

		public DateTime CapturedAtUtc => DateTime.UnixEpoch;

		public TestSnapshot(long frameId, Action onDispose)
		{
			FrameId = frameId;
			_onDispose = onDispose;
		}

		protected override void DisposeOwnedResources()
		{
			_onDispose();
		}
	}
}
