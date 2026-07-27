using System.Threading;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Internal worker-to-worker wake-up contract. Public consumers still see only
/// ILatestFrameProducer.TryGetLatest; pipeline workers sleep on this signal
/// until a producer atomically publishes a completed snapshot.
/// </summary>
internal interface IFramePublicationSource
{
	WaitHandle FramePublishedSignal { get; }
}
