using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Coding.Changesets;

internal static class AliSourceTextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);
    private static readonly UTF32Encoding StrictUtf32Le = new(false, false, true);
    private static readonly UTF32Encoding StrictUtf32Be = new(true, false, true);

    internal static AliSourceEncodingMetadata Detect(ReadOnlySpan<byte> bytes)
    {
        foreach (var encoding in new Encoding[]
                 {
                     StrictUtf32Le,
                     StrictUtf32Be,
                     StrictUtf8,
                     StrictUtf16Le,
                     StrictUtf16Be
                 })
        {
            var preamble = StandardPreamble(encoding);
            if (preamble.Length > 0 && bytes.StartsWith(preamble))
            {
                _ = encoding.GetString(bytes[preamble.Length..]);
                return new AliSourceEncodingMetadata(encoding.WebName, Convert.ToHexString(preamble));
            }
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return new AliSourceEncodingMetadata(StrictUtf8.WebName, string.Empty);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "Text source without a supported BOM must contain strict UTF-8. Use a byte request for binary content.",
                ex);
        }
    }

    internal static AliSourceEncodingMetadata CreateMetadata(string encodingName, bool emitPreamble)
    {
        var encoding = Resolve(encodingName);
        var preamble = emitPreamble ? StandardPreamble(encoding) : [];
        var metadata = new AliSourceEncodingMetadata(encoding.WebName, Convert.ToHexString(preamble));
        metadata.Validate();
        return metadata;
    }

    internal static byte[] Encode(string text, AliSourceEncodingMetadata metadata)
    {
        metadata.Validate();
        var encoding = Resolve(metadata.WebName);
        var preamble = Convert.FromHexString(metadata.PreambleHex);
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0)
        {
            return body;
        }

        var bytes = new byte[checked(preamble.Length + body.Length)];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        CryptographicOperations.ZeroMemory(body);
        return bytes;
    }

    internal static string Decode(ReadOnlySpan<byte> bytes, AliSourceEncodingMetadata? metadata = null)
    {
        var exact = metadata ?? Detect(bytes);
        exact.Validate();
        var encoding = Resolve(exact.WebName);
        var preamble = Convert.FromHexString(exact.PreambleHex);
        if (preamble.Length > bytes.Length || !bytes.StartsWith(preamble))
        {
            throw new InvalidDataException("Text source does not match its declared encoding preamble.");
        }

        try
        {
            return encoding.GetString(bytes[preamble.Length..]);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Text source contains invalid encoded bytes.", ex);
        }
    }

    internal static Encoding Resolve(string webName)
    {
        if (string.IsNullOrWhiteSpace(webName))
        {
            throw new InvalidDataException("A source encoding is missing.");
        }

        return webName.Trim().ToLowerInvariant() switch
        {
            "utf-8" => StrictUtf8,
            "utf-16" or "utf-16le" => StrictUtf16Le,
            "utf-16be" => StrictUtf16Be,
            "utf-32" or "utf-32le" => StrictUtf32Le,
            "utf-32be" => StrictUtf32Be,
            _ => throw new InvalidDataException($"Unsupported source text encoding: {webName}")
        };
    }

    internal static IEnumerable<byte[]> SupportedPreambles(Encoding encoding)
    {
        yield return [];
        var preamble = StandardPreamble(encoding);
        if (preamble.Length > 0)
        {
            yield return preamble;
        }
    }

    private static byte[] StandardPreamble(Encoding encoding) => encoding.WebName.ToLowerInvariant() switch
    {
        "utf-8" => [0xEF, 0xBB, 0xBF],
        "utf-16" => [0xFF, 0xFE],
        "utf-16be" => [0xFE, 0xFF],
        "utf-32" => [0xFF, 0xFE, 0x00, 0x00],
        "utf-32be" => [0x00, 0x00, 0xFE, 0xFF],
        _ => []
    };
}
