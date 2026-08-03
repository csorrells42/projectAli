using System.Collections.Immutable;

namespace Ali.Modules.Coding.Changesets;

internal enum AliSourceChangeKind
{
    Add,
    Replace,
    Delete,
    Rename
}

internal sealed record AliSourceFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint NumberOfLinks,
    string FinalNameDigest)
{
    internal string ObjectKey => string.Join(
        ':',
        VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FileIdLow.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
        FileIdHigh.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));

    internal void Validate(string role)
    {
        if (VolumeSerialNumber == 0
            || NumberOfLinks == 0
            || string.IsNullOrWhiteSpace(FinalNameDigest)
            || !string.Equals(
                AliSourceChangeSetStore.NormalizeSha256(FinalNameDigest),
                FinalNameDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {role} FILE_ID_INFO binding is invalid.");
        }
    }

    internal bool SameObject(AliSourceFileIdentity other) =>
        other is not null
        && VolumeSerialNumber == other.VolumeSerialNumber
        && FileIdLow == other.FileIdLow
        && FileIdHigh == other.FileIdHigh;

    internal bool MatchesExactNamespace(AliSourceFileIdentity other) =>
        SameObject(other)
        && NumberOfLinks == other.NumberOfLinks
        && string.Equals(FinalNameDigest, other.FinalNameDigest, StringComparison.Ordinal);
}

internal sealed record AliSourceNamespaceSpineIdentity(
    string RelativeDirectoryPath,
    AliSourceFileIdentity Identity);

internal sealed record AliSourceExpectedFile(bool Exists, string? Sha256, long Length)
{
    internal static AliSourceExpectedFile Absent { get; } = new(false, null, 0);

    internal static AliSourceExpectedFile Present(string sha256, long length) =>
        new(true, AliSourceChangeSetStore.NormalizeSha256(sha256), length);

    internal void Validate(string parameterName)
    {
        if (!Exists)
        {
            if (Sha256 is not null || Length != 0)
            {
                throw new ArgumentException("An absent expected file cannot carry a hash or length.", parameterName);
            }

            return;
        }

        _ = AliSourceChangeSetStore.NormalizeSha256(Sha256
            ?? throw new ArgumentException("A present expected file requires a SHA-256 hash.", parameterName));
        if (Length < 0 || Length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"An expected source file cannot exceed {AliSourceChangeSetStore.MaximumFileBytes} bytes.");
        }
    }
}

internal sealed record AliSourceEncodingMetadata(string WebName, string PreambleHex)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(WebName) || WebName.Length > 128)
        {
            throw new InvalidDataException("A source encoding name is missing or unbounded.");
        }

        byte[] preamble;
        try
        {
            preamble = Convert.FromHexString(PreambleHex ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("A source encoding preamble is invalid.", ex);
        }

        if (preamble.Length > 8)
        {
            throw new InvalidDataException("A source encoding preamble is unbounded.");
        }

        var encoding = AliSourceTextEncoding.Resolve(WebName);
        if (!AliSourceTextEncoding.SupportedPreambles(encoding)
                .Any(candidate => candidate.AsSpan().SequenceEqual(preamble)))
        {
            throw new InvalidDataException("A source encoding preamble does not match its encoding.");
        }
    }
}

internal sealed record AliSourceFileImage(bool Exists, string? Sha256, long Length)
{
    internal static AliSourceFileImage Absent { get; } = new(false, null, 0);

    internal static AliSourceFileImage FromBytes(ReadOnlySpan<byte> bytes) =>
        new(true, AliSourceChangeSetStore.Hash(bytes), bytes.Length);

    internal void Validate()
    {
        if (!Exists)
        {
            if (Sha256 is not null || Length != 0)
            {
                throw new InvalidDataException("An absent file image cannot carry a hash or length.");
            }

            return;
        }

        _ = AliSourceChangeSetStore.NormalizeSha256(Sha256
            ?? throw new InvalidDataException("A present file image requires a SHA-256 hash."));
        if (Length < 0 || Length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new InvalidDataException("A file image length is invalid or unbounded.");
        }
    }

    internal bool Matches(AliSourceFileImage other) =>
        Exists == other.Exists
        && Length == other.Length
        && (Exists
            ? string.Equals(Sha256, other.Sha256, StringComparison.Ordinal)
            : other.Sha256 is null);
}

internal sealed record AliSourceChangeOperation(
    int Sequence,
    AliSourceChangeKind Kind,
    string SourceRelativePath,
    string? DestinationRelativePath,
    AliSourceFileImage SourcePreimage,
    AliSourceFileImage SourcePostimage,
    AliSourceFileImage? DestinationPreimage,
    AliSourceFileImage? DestinationPostimage,
    AliSourceFileIdentity? SourcePreimageIdentity,
    AliSourceFileIdentity? DestinationPreimageIdentity,
    string? ContentBlobSha256,
    long ContentLength,
    AliSourceEncodingMetadata? Encoding);

internal sealed record AliSourceChangeSet(
    string Id,
    string CanonicalSourceRoot,
    DateTimeOffset CreatedAtUtc,
    string ManifestDigest,
    AliSourceFileIdentity SourceRootIdentity,
    ImmutableArray<AliSourceNamespaceSpineIdentity> NamespaceSpines,
    ImmutableArray<AliSourceChangeOperation> Operations);

internal sealed class AliSourceChangeRequest
{
    private AliSourceChangeRequest(
        AliSourceChangeKind kind,
        string sourceRelativePath,
        string? destinationRelativePath,
        byte[]? content,
        string? text,
        AliSourceEncodingMetadata? encoding,
        AliSourceExpectedFile? expectedSource,
        AliSourceExpectedFile? expectedDestination)
    {
        Kind = kind;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        Content = content?.ToArray();
        Text = text;
        Encoding = encoding;
        ExpectedSource = expectedSource;
        ExpectedDestination = expectedDestination;
    }

    internal AliSourceChangeKind Kind { get; }
    internal string SourceRelativePath { get; }
    internal string? DestinationRelativePath { get; }
    internal byte[]? Content { get; }
    internal string? Text { get; }
    internal AliSourceEncodingMetadata? Encoding { get; }
    internal AliSourceExpectedFile? ExpectedSource { get; }
    internal AliSourceExpectedFile? ExpectedDestination { get; }

    internal static AliSourceChangeRequest AddText(
        string relativePath,
        string content,
        AliSourceExpectedFile? expectedDestination = null,
        string encodingName = "utf-8",
        bool emitPreamble = false) =>
        new(
            AliSourceChangeKind.Add,
            relativePath,
            null,
            null,
            content ?? throw new ArgumentNullException(nameof(content)),
            AliSourceTextEncoding.CreateMetadata(encodingName, emitPreamble),
            expectedDestination ?? AliSourceExpectedFile.Absent,
            null);

    internal static AliSourceChangeRequest AddBytes(
        string relativePath,
        ReadOnlyMemory<byte> content,
        AliSourceExpectedFile? expectedDestination = null) =>
        new(
            AliSourceChangeKind.Add,
            relativePath,
            null,
            content.ToArray(),
            null,
            null,
            expectedDestination ?? AliSourceExpectedFile.Absent,
            null);

    internal static AliSourceChangeRequest ReplaceText(
        string relativePath,
        string content,
        AliSourceExpectedFile? expectedSource = null) =>
        new(
            AliSourceChangeKind.Replace,
            relativePath,
            null,
            null,
            content ?? throw new ArgumentNullException(nameof(content)),
            null,
            expectedSource,
            null);

    internal static AliSourceChangeRequest ReplaceBytes(
        string relativePath,
        ReadOnlyMemory<byte> content,
        AliSourceExpectedFile? expectedSource = null) =>
        new(
            AliSourceChangeKind.Replace,
            relativePath,
            null,
            content.ToArray(),
            null,
            null,
            expectedSource,
            null);

    internal static AliSourceChangeRequest Delete(
        string relativePath,
        AliSourceExpectedFile? expectedSource = null) =>
        new(
            AliSourceChangeKind.Delete,
            relativePath,
            null,
            null,
            null,
            null,
            expectedSource,
            null);

    internal static AliSourceChangeRequest Rename(
        string sourceRelativePath,
        string destinationRelativePath,
        AliSourceExpectedFile? expectedSource = null,
        AliSourceExpectedFile? expectedDestination = null) =>
        new(
            AliSourceChangeKind.Rename,
            sourceRelativePath,
            destinationRelativePath,
            null,
            null,
            null,
            expectedSource,
            expectedDestination ?? AliSourceExpectedFile.Absent);
}
