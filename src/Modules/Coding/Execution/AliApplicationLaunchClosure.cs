namespace Ali.Modules.Coding.Execution;

/// <summary>
/// Content-addressed identity for the complete output directory used by one application launch.
/// Application outputs deliberately receive no hard-link exception: every file must have one
/// stable local identity, and every child entry must be a regular no-follow file or directory.
/// </summary>
internal sealed record AliApplicationLaunchClosure(
    string OutputDirectoryPath,
    string Identity,
    AliExecutionDirectoryBinding DirectoryBinding)
{
    internal static AliApplicationLaunchClosure Capture(
        AliBoundExecutionFile principalArtifact)
    {
        ArgumentNullException.ThrowIfNull(principalArtifact);
        var principalBefore = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            principalArtifact.PhysicalPath,
            "The application launch principal artifact");
        if (principalBefore != principalArtifact)
        {
            throw new InvalidOperationException(
                "The application launch principal artifact changed before its output closure was captured.");
        }

        var outputDirectory = AliCodingExecutionAssetFingerprint.NormalizePath(
            Path.GetDirectoryName(principalArtifact.PhysicalPath)
            ?? throw new InvalidDataException(
                "The application launch principal artifact has no output directory."));
        var directoryBinding = AliExecutionDirectoryBinding.Capture(
            outputDirectory,
            "The application launch output directory spine");
        var identity = AliCodingExecutionAssetFingerprint.CaptureRequiredAsset(
            outputDirectory,
            "The complete application launch output directory");
        if (!identity.StartsWith("directory:", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The application launch output closure is not a directory.");
        }

        var principalAfter = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            principalArtifact.PhysicalPath,
            "The application launch principal artifact");
        if (principalAfter != principalArtifact)
        {
            throw new InvalidOperationException(
                "The application launch principal artifact changed while its output closure was captured.");
        }
        var directoryAfter = AliExecutionDirectoryBinding.Capture(
            outputDirectory,
            "The application launch output directory spine");
        if (!string.Equals(
                directoryBinding.Identity,
                directoryAfter.Identity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The application launch output directory identity changed while its closure was captured.");
        }
        return new AliApplicationLaunchClosure(outputDirectory, identity, directoryBinding);
    }

    internal string RequireStable()
    {
        using var directory = DirectoryBinding.Acquire(
            "The exact authorized application launch output directory spine");
        var current = AliCodingExecutionAssetFingerprint.CaptureRequiredAsset(
            OutputDirectoryPath,
            "The exact authorized application launch output directory");
        if (!string.Equals(current, Identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The complete application launch output directory changed after durable authorization.");
        }
        return OutputDirectoryPath;
    }

    internal void AddTo(IDictionary<string, string> values, string prefix)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        values[prefix + ".path"] = OutputDirectoryPath;
        values[prefix + ".identity"] = Identity;
        DirectoryBinding.AddTo(values, prefix + ".spine");
    }
}
