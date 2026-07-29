using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ali.Modules.Coding.Protocols;

/// <summary>
/// Shared framing used by both the Language Server Protocol and Debug Adapter Protocol.
/// The transport owns no protocol semantics and never starts a process or interprets commands.
/// </summary>
internal sealed class AliContentLengthMessageTransport : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _leaveOpen;

    public AliContentLengthMessageTransport(Stream input, Stream output, bool leaveOpen = false)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _leaveOpen = leaveOpen;
    }

    public async Task WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var contentLength = await ReadContentLengthAsync(_output, cancellationToken).ConfigureAwait(false);
        if (contentLength is null)
        {
            return null;
        }

        if (contentLength is < 0 or > 16_777_216)
        {
            throw new InvalidDataException("Protocol message length is outside Ali's 16 MiB safety bound.");
        }

        var body = new byte[contentLength.Value];
        await _output.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidDataException("Protocol peer returned an empty JSON message.");
    }

    internal static byte[] Frame(JsonNode message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var framed = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(body, 0, framed, header.Length, body.Length);
        return framed;
    }

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var current = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(current, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0
                    ? null
                    : throw new EndOfStreamException("Protocol peer closed while sending a header.");
            }

            bytes.Add(current[0]);
            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                break;
            }
        }

        var header = Encoding.ASCII.GetString(bytes.ToArray());
        var line = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        return int.TryParse(line?["Content-Length:".Length..].Trim(), out var length)
            ? length
            : throw new InvalidDataException("Protocol message omitted a valid Content-Length header.");
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_leaveOpen)
        {
            return;
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_input, _output))
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
