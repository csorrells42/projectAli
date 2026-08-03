using System.Buffers;
using System.Text.Json;

namespace Ali.Modules.Orchestration.Evidence;

internal static class CanonicalEvidenceJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        => SerializeToUtf8Bytes(value, int.MaxValue);

    public static byte[] SerializeToUtf8Bytes<T>(T value, int maximumBytes)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, SerializerOptions);
        var inner = new ArrayBufferWriter<byte>(Math.Min(maximumBytes, 4096));
        var buffer = new BoundedBufferWriter(inner, maximumBytes);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteElement(writer, element);
        }

        return inner.WrittenSpan.ToArray();
    }

    public static JsonElement CloneOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement<object?>(null, SerializerOptions)
            : value.Clone();

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed class BoundedBufferWriter(
        ArrayBufferWriter<byte> inner,
        int maximumBytes) : IBufferWriter<byte>
    {
        public void Advance(int count)
        {
            if (count < 0 || count > maximumBytes - inner.WrittenCount)
            {
                throw new InvalidDataException(
                    $"Canonical evidence JSON cannot exceed {maximumBytes} bytes.");
            }

            inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            RequireCapacity(sizeHint);
            return inner.GetMemory(sizeHint);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            RequireCapacity(sizeHint);
            return inner.GetSpan(sizeHint);
        }

        private void RequireCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            var required = Math.Max(sizeHint, 1);
            if (required > maximumBytes - inner.WrittenCount)
            {
                throw new InvalidDataException(
                    $"Canonical evidence JSON cannot exceed {maximumBytes} bytes.");
            }
        }
    }
}
