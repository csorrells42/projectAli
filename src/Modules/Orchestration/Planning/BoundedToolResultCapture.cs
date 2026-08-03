using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record BoundedToolResult(
    JsonElement Value,
    bool Withheld,
    string? ReasonCode);

/// <summary>
/// Captures one framework tool terminal without first materializing an unbounded JSON string or
/// DOM. The fixed-capacity writer rejects a serializer request before it can rent a result-sized
/// buffer. Oversized/unsupported results become explicit fail-closed evidence rather than being
/// mistaken for a successful domain outcome.
/// </summary>
internal static class BoundedToolResultCapture
{
    internal const int MaximumCapturedResultBytes = 512 * 1024;
    private const int MaximumTypeNameCharacters = 512;

    internal static BoundedToolResult Capture(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Exception is not null)
        {
            return new BoundedToolResult(
                JsonSerializer.SerializeToElement(new
                {
                    exceptionType = BoundTypeName(result.Exception.GetType())
                }),
                Withheld: false,
                ReasonCode: null);
        }

        try
        {
            using var buffer = new FixedCapacityJsonBuffer(MaximumCapturedResultBytes);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var runtimeType = result.Result?.GetType() ?? typeof(object);
                JsonSerializer.Serialize(writer, result.Result, runtimeType);
                writer.Flush();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return new BoundedToolResult(
                document.RootElement.Clone(),
                Withheld: false,
                ReasonCode: null);
        }
        catch (ToolResultCaptureLimitExceededException)
        {
            return Withheld(
                "result-exceeded-capture-limit",
                result.Result?.GetType());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Withheld(
                "result-not-json-serializable",
                result.Result?.GetType());
        }
    }

    private static BoundedToolResult Withheld(string reasonCode, Type? resultType) =>
        new(
            JsonSerializer.SerializeToElement(new
            {
                withheld = true,
                reason = reasonCode,
                resultType = BoundTypeName(resultType),
                maximumBytes = MaximumCapturedResultBytes
            }),
            Withheld: true,
            reasonCode);

    private static string BoundTypeName(Type? type)
    {
        var name = type?.FullName ?? type?.Name ?? "null";
        return name.Length <= MaximumTypeNameCharacters
            ? name
            : name[..MaximumTypeNameCharacters];
    }

    private sealed class FixedCapacityJsonBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly int _capacity;
        private byte[]? _buffer;
        private int _written;

        internal FixedCapacityJsonBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        }

        internal ReadOnlyMemory<byte> WrittenMemory =>
            (_buffer ?? throw new ObjectDisposedException(nameof(FixedCapacityJsonBuffer)))
            .AsMemory(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _capacity - _written)
            {
                throw new ToolResultCaptureLimitExceededException();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var buffer = _buffer
                ?? throw new ObjectDisposedException(nameof(FixedCapacityJsonBuffer));
            var requested = Math.Max(sizeHint, 1);
            if (requested > _capacity - _written)
            {
                throw new ToolResultCaptureLimitExceededException();
            }

            return buffer.AsMemory(_written, _capacity - _written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            _written = 0;
        }
    }

    private sealed class ToolResultCaptureLimitExceededException : Exception;
}
