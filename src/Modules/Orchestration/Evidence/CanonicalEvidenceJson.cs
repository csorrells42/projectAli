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
    {
        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, SerializerOptions);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteElement(writer, element);
        }

        return buffer.WrittenSpan.ToArray();
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
}
