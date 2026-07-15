using System.Threading.Channels;
using Ali.Modules.Voice;

namespace Ali.UI.ViewModels;

internal sealed class StreamingSpeechState
{
    public StreamingSpeechState(CancellationTokenSource cancellation)
    {
        Cancellation = cancellation;
    }

    public SpeechStreamingBuffer Buffer { get; } = new();

    public Channel<string> Queue { get; } = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    public CancellationTokenSource Cancellation { get; }

    public Task ConsumerTask { get; set; } = Task.CompletedTask;
}

