using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Orchestration.Work;

internal static class WorkIdentityCanonicalizer
{
    public static string CanonicalJsonDigest(ReadOnlySpan<byte> jsonUtf8)
    {
        if (jsonUtf8.IsEmpty)
        {
            throw new ArgumentException("Action arguments must contain one JSON value.", nameof(jsonUtf8));
        }

        using var document = JsonDocument.Parse(
            jsonUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 256
            });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return DigestBytes(buffer.WrittenSpan);
    }

    public static string MapDigest(
        string domain,
        IReadOnlyDictionary<string, string>? values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var parts = new List<string> { domain };
        if (values is null)
        {
            parts.Add("0");
            return DigestParts(parts);
        }

        parts.Add(values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("Identity-map keys cannot be blank.", nameof(values));
            }

            if (pair.Value is null)
            {
                throw new ArgumentException(
                    $"Identity-map value '{pair.Key}' cannot be null.",
                    nameof(values));
            }

            parts.Add(pair.Key);
            parts.Add(pair.Value);
        }

        return DigestParts(parts);
    }

    public static string SetDigest(string domain, IEnumerable<string>? values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var normalized = values is null
            ? []
            : values
                .Select(value =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    return value;
                })
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var parts = new List<string>
        {
            domain,
            normalized.Length.ToString(CultureInfo.InvariantCulture)
        };
        parts.AddRange(normalized);
        return DigestParts(parts);
    }

    public static string DigestParts(params string[] parts) => DigestParts((IEnumerable<string>)parts);

    public static string DigestParts(IEnumerable<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[4];
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException(
                            $"Action arguments contain duplicate object property '{property.Name}'.");
                    }
                }

                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(
                             static property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            }

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalizeNumber(element.GetRawText()));
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        var negative = raw[0] == '-';
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOfAny('e', 'E');
        var significand = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(
                unsigned[(exponentIndex + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);

        var decimalIndex = significand.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : significand.Length - decimalIndex - 1;
        var digits = decimalIndex < 0
            ? significand
            : string.Concat(significand.AsSpan(0, decimalIndex), significand.AsSpan(decimalIndex + 1));
        exponent -= fractionalDigits;

        var firstNonzero = 0;
        while (firstNonzero < digits.Length && digits[firstNonzero] == '0')
        {
            firstNonzero++;
        }

        if (firstNonzero == digits.Length)
        {
            return "0";
        }

        digits = digits[firstNonzero..];
        var trailingZeros = 0;
        while (trailingZeros < digits.Length - 1 && digits[^(trailingZeros + 1)] == '0')
        {
            trailingZeros++;
        }

        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            exponent += trailingZeros;
        }

        var sign = negative ? "-" : string.Empty;
        return exponent.IsZero
            ? string.Concat(sign, digits)
            : string.Concat(sign, digits, "e", exponent.ToString(CultureInfo.InvariantCulture));
    }

    private static string DigestBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
