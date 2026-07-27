using System;
using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

public sealed record FramePublicationSignalSelfTestResult(
	bool Succeeded,
	string Detail);

public static class FramePublicationSignalSelfTest
{
	public static FramePublicationSignalSelfTestResult Run()
	{
		using var producer = new CountingProducer();
		using var stage = new PassThroughStage(producer);
		stage.Start();
		Thread.Sleep(75);
		int idleReadAttempts = producer.ReadAttempts;

		producer.Publish(1);
		bool firstProcessed = stage.FirstProcessed.Wait(
			TimeSpan.FromSeconds(1));
		int readsAfterFirst = producer.ReadAttempts;

		producer.Publish(2);
		Thread.Sleep(75);
		bool upstreamHeldWhileOutputOccupied =
			producer.ReadAttempts == readsAfterFirst;

		using var firstOutput = new SnapshotCursor<TestSnapshot>();
		bool firstTransferred =
			stage.TryTake(0, firstOutput)
			&& firstOutput.Current.FrameId == 1;
		firstOutput.Release();
		bool secondProcessed = stage.SecondProcessed.Wait(
			TimeSpan.FromSeconds(1));
		using var secondOutput = new SnapshotCursor<TestSnapshot>();
		var secondTransferTimeout = Stopwatch.StartNew();
		bool secondTransferred = false;
		while (!secondTransferred
			&& secondTransferTimeout.Elapsed
				< TimeSpan.FromSeconds(1))
		{
			secondTransferred =
				stage.TryTake(1, secondOutput)
				&& secondOutput.Current.FrameId == 2;
			if (!secondTransferred)
			{
				Thread.Sleep(1);
			}
		}

		var shutdown = Stopwatch.StartNew();
		stage.Dispose();
		shutdown.Stop();

		using var signalPublisher =
			new LatestFramePublisher<TestSnapshot>();
		bool signalFramePublished =
			signalPublisher.Publish(new TestSnapshot(10));
		WaitHandle publicationSignal =
			((IFramePublicationSource)signalPublisher)
				.FramePublishedSignal;
		bool signalPersisted = publicationSignal.WaitOne(0);
		bool onlyOneSignal = !publicationSignal.WaitOne(0);
		using var cursor = new SnapshotCursor<TestSnapshot>();
		bool exactSignalFrame =
			signalPublisher.TryGetLatest(0, cursor)
			&& cursor.Current.FrameId == 10;

		bool passed =
			idleReadAttempts == 0
			&& firstProcessed
			&& readsAfterFirst == 1
			&& upstreamHeldWhileOutputOccupied
			&& firstTransferred
			&& secondProcessed
			&& producer.ReadAttempts == 2
			&& secondTransferred
			&& shutdown.Elapsed < TimeSpan.FromMilliseconds(500)
			&& signalFramePublished
			&& signalPersisted
			&& onlyOneSignal
			&& exactSignalFrame;

		return new FramePublicationSignalSelfTestResult(
			passed,
			passed
				? "PASS: idle workers make zero reads, each publication wakes once, a stage does not take new input until its prior output is consumed, and shutdown wakes immediately."
				: $"FAIL: idleReads={idleReadAttempts}, first={firstProcessed}, readsAfterFirst={readsAfterFirst}, held={upstreamHeldWhileOutputOccupied}, firstTake={firstTransferred}, second={secondProcessed}, totalReads={producer.ReadAttempts}, secondTake={secondTransferred}, shutdownMs={shutdown.Elapsed.TotalMilliseconds:0.###}, signal={signalFramePublished}/{signalPersisted}/{onlyOneSignal}/{exactSignalFrame}.");
	}

	private sealed class CountingProducer :
		ILatestFrameProducer<TestSnapshot>,
		IFramePublicationSource,
		IDisposable
	{
		private readonly LatestFramePublisher<TestSnapshot> _publisher =
			new();

		private int _readAttempts;

		public int ReadAttempts =>
			Volatile.Read(ref _readAttempts);

		WaitHandle IFramePublicationSource.FramePublishedSignal =>
			((IFramePublicationSource)_publisher)
				.FramePublishedSignal;

		public bool TryGetLatest(
			long afterFrameId,
			SnapshotCursor<TestSnapshot> destination)
		{
			Interlocked.Increment(ref _readAttempts);
			return _publisher.TryGetLatest(
				afterFrameId,
				destination);
		}

		public void Publish(long frameId)
		{
			_publisher.Publish(new TestSnapshot(frameId));
		}

		public void Dispose()
		{
			_publisher.Dispose();
		}
	}

	private sealed class PassThroughStage :
		LatestFrameStage<TestSnapshot, TestSnapshot>
	{
		public ManualResetEventSlim FirstProcessed { get; } =
			new(initialState: false);

		public ManualResetEventSlim SecondProcessed { get; } =
			new(initialState: false);

		private int _processed;

		public PassThroughStage(
			ILatestFrameProducer<TestSnapshot> input)
			: base(
				input,
				"Frame publication signal self-test")
		{
		}

		public bool TryTake(
			long afterFrameId,
			SnapshotCursor<TestSnapshot> destination)
		{
			return GetLatestOutput(afterFrameId, destination);
		}

		protected override TestSnapshot Process(
			TestSnapshot input)
		{
			if (Interlocked.Increment(ref _processed) == 1)
			{
				FirstProcessed.Set();
			}
			else
			{
				SecondProcessed.Set();
			}
			return new TestSnapshot(input.FrameId);
		}

		protected override void DisposeStage()
		{
			FirstProcessed.Dispose();
			SecondProcessed.Dispose();
		}
	}

	private sealed class TestSnapshot :
		ModuleOutput,
		IFramePipelineSnapshot
	{
		public long FrameId { get; }

		public long CapturedAtTimestamp => FrameId;

		public DateTime CapturedAtUtc => DateTime.UnixEpoch;

		public TestSnapshot(long frameId)
		{
			FrameId = frameId;
		}

		protected override void DisposeOwnedResources()
		{
		}
	}
}
