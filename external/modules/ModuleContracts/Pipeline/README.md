# Latest-Frame Pipeline Contracts

The pipeline is a synchronous dependency chain implemented by isolated worker
threads.

Rules:

1. A module sleeps until its predecessor signals that a completed snapshot was
   published, then pulls only the newest snapshot.
2. Publication signals coalesce while a consumer is busy. A module may skip
   frames before accepting work; it never creates a waiting mailbox.
3. After acceptance, the frame finishes.
4. Work happens in private state. Only a complete immutable result is published.
5. Publication is an atomic pointer/generation transition through a fixed node pool.
6. Every successor preserves the input `FrameId` and capture timestamps.
7. Disposing a cursor releases only its retained published node.
8. Adding a module never modifies or blocks an earlier tested producer.
9. Each module measures its own `TimeWaited` from its previous frame-out to its
   next accepted frame and its own `TimeWorked` from acceptance to frame-out.
10. Those are the only public module timing values. No frame carries diagnostic
    timing metadata.

Core contracts:

- `IFramePipelineSnapshot`
- `ILatestFrameProducer<TSnapshot>`
- `IFramePublicationSource` (internal wake-up contract)
- `SnapshotCursor<TSnapshot>`
- `LatestFramePublisher<TSnapshot>`
- `LatestFrameStage<TInput,TOutput>`
- `IFrameModuleTimingSource`

`LatestFramePublisherSelfTest` stress-checks publication and reader lifetime
concurrently. `FramePublicationSignalSelfTest` verifies that idle workers make
zero read attempts, publication wakes one consumer immediately, busy-time
signals coalesce to the newest frame, and shutdown wakes a sleeping worker.
`FrameModuleTimingSelfTest` verifies the module-owned waited and worked
arithmetic.
