using System.Security.Cryptography;

namespace Ali.Modules.Mcp;

/// <summary>
/// Hashes one already-observed file extent without following concurrent growth.
/// Callers still compare metadata after this read to reject changes that land at
/// the edge of the bounded read.
/// </summary>
internal static class McpBoundedFileHash
{
    private const int BufferSize = 64 * 1024;

    internal static string CalculateExactExtent(
        Stream stream,
        long expectedLength,
        long maximumBytes,
        string fileName,
        string boundaryDescription)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryDescription);
        if (expectedLength < 0 || maximumBytes < 0 || expectedLength > maximumBytes)
        {
            throw new IOException(
                $"{boundaryDescription} '{fileName}' exceeds Ali's bounded inspection size.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var remaining = expectedLength;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new IOException(
                    $"{boundaryDescription} '{fileName}' changed while it was being inspected.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        // Read at most one byte beyond the observed extent. This detects growth
        // without allowing a continuously growing file to extend the hash loop.
        if (stream.ReadByte() != -1)
        {
            throw new IOException(
                $"{boundaryDescription} '{fileName}' changed or exceeded Ali's bounded inspection size while it was being inspected.");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
