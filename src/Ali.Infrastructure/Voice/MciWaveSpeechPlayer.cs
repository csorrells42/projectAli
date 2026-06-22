using System.Runtime.InteropServices;
using System.Text;
using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed class MciWaveSpeechPlayer : ISpeechPlayer
{
    private const string Alias = "AliSpeechPlayer";
    private readonly object _sync = new();
    private bool _isSpeaking;

    public bool IsSpeaking
    {
        get
        {
            lock (_sync)
            {
                return _isSpeaking;
            }
        }
    }

    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Speech audio file was not found.", audioPath);
        }

        Stop();

        lock (_sync)
        {
            _isSpeaking = true;
        }

        using var registration = cancellationToken.Register(Stop);
        try
        {
            await Task.Run(
                () =>
                {
                    SendMci($"close {Alias}", ignoreErrors: true);
                    SendMci($"open \"{audioPath}\" type waveaudio alias {Alias}");
                    SendMci($"play {Alias} wait");
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendMci($"close {Alias}", ignoreErrors: true);
            lock (_sync)
            {
                _isSpeaking = false;
            }
        }
    }

    public void Stop()
    {
        SendMci($"stop {Alias}", ignoreErrors: true);
        SendMci($"close {Alias}", ignoreErrors: true);
        lock (_sync)
        {
            _isSpeaking = false;
        }
    }

    private static void SendMci(string command, bool ignoreErrors = false)
    {
        var error = mciSendString(command, null, 0, IntPtr.Zero);
        if (error == 0 || ignoreErrors)
        {
            return;
        }

        var message = new StringBuilder(256);
        _ = mciGetErrorString(error, message, message.Capacity);
        throw new InvalidOperationException($"Windows audio command failed: {message}");
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextLength);
}
