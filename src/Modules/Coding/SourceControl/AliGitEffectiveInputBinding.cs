using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.SourceControl;

internal sealed record AliGitEffectiveInputBinding(
    string ConfigurationDigest,
    string HelperDigest,
    string RemoteDigest,
    IReadOnlyList<AliGitExecutionFileBinding> ExecutionFiles)
{
    internal string CombinedDigest => WorkIdentityCanonicalizer.MapDigest(
        "ali-git-effective-inputs-v2",
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["configuration"] = ConfigurationDigest,
            ["helpers"] = HelperDigest,
            ["remote"] = RemoteDigest
        });
}

/// <summary>
/// Captures the effective Git configuration graph without executing Git or any helper. Every
/// active origin is opened without following reparse points, parsed under a fixed bounded grammar,
/// and captured twice. Conditional include graphs and dynamic shell helpers are rejected.
/// </summary>
internal static class AliGitEffectiveInputCapture
{
    private const int MaximumConfigurationFiles = 128;
    private const long MaximumConfigurationFileBytes = 16L * 1024 * 1024;
    private const long MaximumConfigurationAggregateBytes = 64L * 1024 * 1024;
    private const int MaximumConfigurationEntries = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static AliGitEffectiveInputBinding Capture(
        AliGitRepositoryLayout repository,
        AliGitProviderPin provider,
        AliGitInvocationKind kind,
        string? remote)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(provider);
        var firstCapture = CaptureStaticConfiguration(repository, provider, kind);
        var firstEntries = firstCapture.Entries;
        ValidateCommandLinePolicy(firstEntries, kind);
        ValidateFixedRepositorySemantics(firstEntries);
        var secondCapture = CaptureStaticConfiguration(repository, provider, kind);
        var secondEntries = secondCapture.Entries;
        if (!FixedTimeTextEquals(
                CanonicalEntries(firstEntries),
                CanonicalEntries(secondEntries)))
        {
            throw new InvalidDataException(
                "The effective Git configuration changed while it was captured.");
        }
        RequireSameMap(
            firstCapture.Files,
            secondCapture.Files,
            "A Git configuration file changed while it was captured.");

        var helpers = CaptureHelpers(firstEntries, provider, kind);
        var repeatedHelpers = CaptureHelpers(secondEntries, provider, kind);
        RequireSameMap(
            helpers.Identities,
            repeatedHelpers.Identities,
            "A Git helper executable changed while it was captured.");

        var remoteCapture = kind == AliGitInvocationKind.Push
            ? CaptureRemote(provider, remote!, firstEntries)
            : new RemoteCapture(HashText("not-applicable"), []);
        var configurationMaterial = new SortedDictionary<string, string>(
            firstCapture.Files,
            StringComparer.Ordinal)
        {
            ["effective-output"] = HashText(CanonicalEntries(firstEntries)),
            ["policy"] = AliGitExecutionPolicy.PolicyIdentity
        };
        return new AliGitEffectiveInputBinding(
            WorkIdentityCanonicalizer.MapDigest(
                "ali-git-effective-configuration-v2",
                configurationMaterial),
            WorkIdentityCanonicalizer.MapDigest(
                "ali-git-effective-helpers-v2",
                helpers.Identities),
            remoteCapture.Digest,
            MergeExecutionFiles(
                firstCapture.ExecutionFiles.Values,
                helpers.ExecutionFiles,
                remoteCapture.ExecutionFiles));
    }

    private static StaticConfigurationCapture CaptureStaticConfiguration(
        AliGitRepositoryLayout repository,
        AliGitProviderPin provider,
        AliGitInvocationKind kind)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var executionFiles = new SortedDictionary<string, AliGitExecutionFileBinding>(
            StringComparer.Ordinal);
        var entries = new List<ConfigurationEntry>();
        var visited = new HashSet<string>(PathComparer);
        long aggregateBytes = 0;

        foreach (var root in ConfigurationRoots(repository, provider))
        {
            CaptureConfigurationGraph(
                root.Path,
                root.Scope,
                provider,
                entries,
                files,
                executionFiles,
                visited,
                ref aggregateBytes,
                depth: 0);
        }

        var localEnablesWorktreeConfiguration = entries.Any(entry =>
            string.Equals(entry.Scope, "local", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Key, "extensions.worktreeconfig", StringComparison.OrdinalIgnoreCase)
            && IsTrue(entry.Value));
        var worktreeConfiguration = Path.Combine(
            repository.WorktreeGitDirectory,
            "config.worktree");
        if (localEnablesWorktreeConfiguration && File.Exists(worktreeConfiguration))
        {
            CaptureConfigurationGraph(
                worktreeConfiguration,
                "worktree",
                provider,
                entries,
                files,
                executionFiles,
                visited,
                ref aggregateBytes,
                depth: 0);
        }

        foreach (var pair in AliGitExecutionPolicy.CommandLineValues(kind))
        {
            foreach (var value in pair.Value)
            {
                entries.Add(new ConfigurationEntry(
                    "command",
                    "command line:",
                    pair.Key,
                    value));
            }
        }
        if (entries.Count > MaximumConfigurationEntries)
        {
            throw new InvalidDataException(
                "The effective Git configuration exceeded its fixed entry-count bound.");
        }

        return new StaticConfigurationCapture(
            Array.AsReadOnly(entries.ToArray()),
            files,
            executionFiles);
    }

    private static IReadOnlyList<ConfigurationRoot> ConfigurationRoots(
        AliGitRepositoryLayout repository,
        AliGitProviderPin provider)
    {
        var result = new List<ConfigurationRoot>();
        AddIfPresent(
            result,
            Path.Combine(provider.InstallationRoot, "etc", "gitconfig"),
            "system");
        var programData = provider.GetPinnedEnvironment("PROGRAMDATA");
        if (!string.IsNullOrWhiteSpace(programData))
        {
            AddIfPresent(
                result,
                Path.Combine(programData, "Git", "config"),
                "system");
        }
        var home = provider.GetPinnedEnvironment("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = provider.GetPinnedEnvironment("USERPROFILE");
        }
        if (!string.IsNullOrWhiteSpace(home))
        {
            var xdg = provider.GetPinnedEnvironment("XDG_CONFIG_HOME");
            AddIfPresent(
                result,
                Path.Combine(
                    string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg,
                    "git",
                    "config"),
                "global");
            AddIfPresent(result, Path.Combine(home, ".gitconfig"), "global");
        }
        AddIfPresent(
            result,
            Path.Combine(repository.CommonGitDirectory, "config"),
            "local");
        return Array.AsReadOnly(result.ToArray());
    }

    private static void AddIfPresent(
        ICollection<ConfigurationRoot> roots,
        string path,
        string scope)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            roots.Add(new ConfigurationRoot(fullPath, scope));
        }
    }

    private static void CaptureConfigurationGraph(
        string path,
        string scope,
        AliGitProviderPin provider,
        ICollection<ConfigurationEntry> entries,
        IDictionary<string, string> files,
        IDictionary<string, AliGitExecutionFileBinding> executionFiles,
        ISet<string> visited,
        ref long aggregateBytes,
        int depth)
    {
        if (depth > 16 || visited.Count >= MaximumConfigurationFiles)
        {
            throw new InvalidDataException(
                "The effective Git configuration exceeds its fixed include bound.");
        }
        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
        {
            throw new InvalidDataException(
                "A cyclic or repeated Git configuration include is unsupported.");
        }

        var captured = CaptureConfigurationFile(fullPath);
        aggregateBytes = checked(aggregateBytes + captured.Length);
        if (aggregateBytes > MaximumConfigurationAggregateBytes)
        {
            throw new InvalidDataException(
                "The effective Git configuration exceeds its aggregate-size bound.");
        }
        files[NormalizePath(fullPath)] = captured.Identity;
        executionFiles[NormalizePath(fullPath)] = captured.ExecutionFile;
        foreach (var parsed in ParseConfigurationFile(captured.Text, scope, fullPath))
        {
            if (string.Equals(parsed.Key, "include.path", StringComparison.OrdinalIgnoreCase))
            {
                CaptureConfigurationGraph(
                    ResolveIncludePath(parsed.Value, fullPath, provider),
                    scope,
                    provider,
                    entries,
                    files,
                    executionFiles,
                    visited,
                    ref aggregateBytes,
                    depth + 1);
                continue;
            }
            entries.Add(parsed);
        }
    }

    private static void ValidateCommandLinePolicy(
        IReadOnlyList<ConfigurationEntry> entries,
        AliGitInvocationKind kind)
    {
        var expected = AliGitExecutionPolicy.CommandLineValues(kind);
        var observed = expected.ToDictionary(
            pair => pair.Key,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Scope, "command", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Origin, "command line:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.Equals(entry.Scope, "command", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Origin, "command line:", StringComparison.OrdinalIgnoreCase)
                || !observed.TryGetValue(entry.Key, out var values))
            {
                throw new InvalidDataException(
                    "An unregistered command-line Git configuration input is active.");
            }
            values.Add(entry.Value);
        }

        foreach (var pair in expected)
        {
            if (!observed.TryGetValue(pair.Key, out var values)
                || !values.SequenceEqual(pair.Value, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "The fixed Git command-line policy was not applied exactly.");
            }
        }
    }

    private static void ValidateFixedRepositorySemantics(
        IReadOnlyList<ConfigurationEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Key, "core.worktree", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                throw new InvalidDataException(
                    "A configured Git worktree pointer is outside the supported fixed repository layout.");
            }
            if ((string.Equals(entry.Key, "core.gitproxy", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(entry.Key, "core.sshcommand", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                throw new InvalidDataException(
                    "A dynamic Git transport command is not supported by the fixed provider.");
            }
        }

        var hooksPath = entries
            .Where(entry => string.Equals(
                entry.Key,
                "core.hookspath",
                StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
        if (hooksPath is null
            || !string.Equals(hooksPath.Scope, "command", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hooksPath.Origin, "command line:", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hooksPath.Value, "/dev/null", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Git hooks were not disabled by the fixed provider policy.");
        }
    }

    private static ConfigurationFileCapture CaptureConfigurationFile(string path)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "An effective Git configuration origin is not a regular local file: "
            + Path.GetFileName(path));
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            "An effective Git configuration origin does not have a stable single-link identity: "
            + Path.GetFileName(path));
        var length = stream.Length;
        if (length < 0 || length > MaximumConfigurationFileBytes)
        {
            throw new InvalidDataException(
                "An effective Git configuration file exceeds its fixed size bound.");
        }
        var bytes = new byte[checked((int)length)];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException(
                    "An effective Git configuration file changed while it was captured.");
            }
            var repeatedIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                path,
                "An effective Git configuration origin does not have a stable single-link identity: "
                + Path.GetFileName(path));
            if (!string.Equals(
                    fileIdentity.CanonicalIdentity,
                    repeatedIdentity.CanonicalIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "An effective Git configuration file changed identity while it was captured.");
            }
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "An effective Git configuration file is not strict UTF-8.",
                    exception);
            }
            if (ContainsAsciiIgnoreCase(bytes, "[includeif"))
            {
                throw new InvalidDataException(
                    "Conditional Git includes are unsupported by the fixed input binder.");
            }
            var contentDigest = HashBytes(bytes);
            var identity = WorkIdentityCanonicalizer.MapDigest(
                "ali-git-configuration-file-v2",
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = NormalizePath(path),
                    ["fileIdentity"] = fileIdentity.CanonicalIdentity,
                    ["length"] = length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["content"] = contentDigest
                });
            var executionFile = AliGitExecutionFileBinding.Capture(
                path,
                MaximumConfigurationFileBytes,
                "An effective Git configuration origin is not a stable regular local file: "
                + Path.GetFileName(path));
            return new ConfigurationFileCapture(
                length,
                identity,
                text,
                executionFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IReadOnlyList<ConfigurationEntry> ParseConfigurationFile(
        string text,
        string scope,
        string path)
    {
        var entries = new List<ConfigurationEntry>();
        var section = string.Empty;
        foreach (var line in LogicalLines(text))
        {
            var content = StripComment(line).Trim();
            if (content.Length == 0)
            {
                continue;
            }
            if (content[0] == '[')
            {
                section = ParseSection(content);
                if (section.StartsWith("includeif.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(section, "includeif", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Conditional Git includes are unsupported by the fixed input binder.");
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(section))
            {
                throw new InvalidDataException(
                    "An effective Git configuration entry appears outside a section.");
            }

            var separator = IndexOfOutsideQuotes(content, '=');
            string name;
            string rawValue;
            if (separator < 0)
            {
                var whitespace = content.IndexOfAny([' ', '\t']);
                name = whitespace < 0 ? content : content[..whitespace];
                rawValue = whitespace < 0 ? "true" : content[whitespace..];
            }
            else
            {
                name = content[..separator].Trim();
                rawValue = content[(separator + 1)..];
            }
            if (!IsConfigurationName(name))
            {
                throw new InvalidDataException(
                    "An effective Git configuration variable name is invalid.");
            }
            var value = DecodeGitValue(rawValue);
            if (value.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException(
                    "An effective Git configuration value contains a null character.");
            }
            entries.Add(new ConfigurationEntry(
                scope,
                "file:" + path,
                section + "." + name,
                value));
        }
        return Array.AsReadOnly(entries.ToArray());
    }

    private static IReadOnlyList<string> LogicalLines(string text)
    {
        var result = new List<string>();
        var pending = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            var trailingBackslashes = 0;
            for (var index = raw.Length - 1; index >= 0 && raw[index] == '\\'; index--)
            {
                trailingBackslashes++;
            }
            if ((trailingBackslashes & 1) != 0)
            {
                pending.Append(raw.AsSpan(0, raw.Length - 1));
                continue;
            }
            pending.Append(raw);
            result.Add(pending.ToString());
            pending.Clear();
        }
        if (pending.Length != 0)
        {
            throw new InvalidDataException(
                "An effective Git configuration ends with an incomplete continuation.");
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static string ParseSection(string content)
    {
        if (content.Length < 3 || content[^1] != ']')
        {
            throw new InvalidDataException("An effective Git configuration section is invalid.");
        }
        var body = content[1..^1].Trim();
        var whitespace = body.IndexOfAny([' ', '\t']);
        if (whitespace >= 0)
        {
            var name = body[..whitespace];
            var subsection = DecodeGitValue(body[whitespace..]);
            if (!IsConfigurationName(name) || string.IsNullOrWhiteSpace(subsection))
            {
                throw new InvalidDataException(
                    "An effective Git configuration section is invalid.");
            }
            return name + "." + subsection;
        }
        var dot = body.IndexOf('.');
        if (dot >= 0)
        {
            var name = body[..dot];
            var subsection = body[(dot + 1)..];
            if (!IsConfigurationName(name) || string.IsNullOrWhiteSpace(subsection))
            {
                throw new InvalidDataException(
                    "An effective Git configuration section is invalid.");
            }
            return name + "." + subsection;
        }
        if (!IsConfigurationName(body))
        {
            throw new InvalidDataException("An effective Git configuration section is invalid.");
        }
        return body;
    }

    private static string DecodeGitValue(string raw)
    {
        var value = new StringBuilder(raw.Length);
        var quoted = false;
        for (var index = 0; index < raw.Length; index++)
        {
            var current = raw[index];
            if (current == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (current != '\\')
            {
                value.Append(current);
                continue;
            }
            if (++index >= raw.Length)
            {
                throw new InvalidDataException(
                    "An effective Git configuration value has an incomplete escape.");
            }
            value.Append(raw[index] switch
            {
                'n' => '\n',
                't' => '\t',
                'b' => '\b',
                '\\' => '\\',
                '"' => '"',
                _ => throw new InvalidDataException(
                    "An effective Git configuration value uses an unsupported escape.")
            });
        }
        if (quoted)
        {
            throw new InvalidDataException(
                "An effective Git configuration value has an unterminated quote.");
        }
        return value.ToString().Trim();
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (current == '\\')
            {
                escaped = true;
                continue;
            }
            if (current == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && current is '#' or ';')
            {
                return line[..index];
            }
        }
        return line;
    }

    private static int IndexOfOutsideQuotes(string value, char target)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (current == '\\')
            {
                escaped = true;
                continue;
            }
            if (current == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && current == target)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsConfigurationName(string value) =>
        value.Length is > 0 and <= 128
        && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string ResolveIncludePath(
        string value,
        string includingFile,
        AliGitProviderPin provider)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(['\r', '\n', '\0']) >= 0
            || value.StartsWith("~", StringComparison.Ordinal)
               && !value.StartsWith("~/", StringComparison.Ordinal)
               && !value.StartsWith("~\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Git configuration include path is invalid.");
        }
        if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = provider.GetPinnedEnvironment("HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = provider.GetPinnedEnvironment("USERPROFILE");
            }
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidDataException(
                    "A home-relative Git configuration include has no pinned home root.");
            }
            return Path.GetFullPath(Path.Combine(home, value[2..]));
        }
        return Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(Path.GetDirectoryName(includingFile)!, value));
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.Ordinal);

    private static string CanonicalEntries(IEnumerable<ConfigurationEntry> entries) =>
        string.Join(
            "\n",
            entries.Select(entry => string.Join(
                "\0",
                entry.Scope,
                entry.Origin,
                entry.Key,
                entry.Value)));

    private static HelperCapture CaptureHelpers(
        IReadOnlyList<ConfigurationEntry> entries,
        AliGitProviderPin provider,
        AliGitInvocationKind kind)
    {
        var helperCommands = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (kind == AliGitInvocationKind.Push
                && IsCredentialHelperKey(entry.Key)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                helperCommands.Add(ParseSimpleCredentialHelper(entry.Value));
            }
            if (IsFilterCommandKey(entry.Key)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                helperCommands.Add(ParseSafeFilterExecutable(entry.Value));
            }
        }

        var identities = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var executionFiles = new List<AliGitExecutionFileBinding>();
        foreach (var command in helperCommands)
        {
            var binding = provider.CaptureProviderCommandBinding(command);
            identities[command] = binding.Identity;
            executionFiles.Add(binding);
        }
        return new HelperCapture(
            identities,
            Array.AsReadOnly(executionFiles.ToArray()));
    }

    private static RemoteCapture CaptureRemote(
        AliGitProviderPin provider,
        string remote,
        IReadOnlyList<ConfigurationEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new InvalidDataException("An exact Git push remote is required.");
        }
        var receivePackKey = "remote." + remote + ".receivepack";
        if (entries.Any(entry =>
                string.Equals(entry.Key, receivePackKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value)))
        {
            throw new InvalidDataException(
                "A custom Git receive-pack command is not supported by the fixed provider.");
        }
        if (entries.Any(entry =>
                (entry.Key.StartsWith("url.", StringComparison.OrdinalIgnoreCase)
                 && (entry.Key.EndsWith(".insteadof", StringComparison.OrdinalIgnoreCase)
                     || entry.Key.EndsWith(".pushinsteadof", StringComparison.OrdinalIgnoreCase)))
                && !string.IsNullOrWhiteSpace(entry.Value)))
        {
            throw new InvalidDataException(
                "Dynamic Git URL rewrite rules are unsupported by the fixed provider.");
        }

        var pushUrlKey = "remote." + remote + ".pushurl";
        var urlKey = "remote." + remote + ".url";
        var urls = entries
            .Where(entry => string.Equals(
                entry.Key,
                pushUrlKey,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .ToArray();
        if (urls.Length == 0)
        {
            urls = entries
                .Where(entry => string.Equals(
                    entry.Key,
                    urlKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value)
                .ToArray();
        }
        ValidateRemoteUrls(urls);
        var helpers = CaptureRemoteHelpers(urls, provider);
        var identities = new SortedDictionary<string, string>(
            helpers.Identities,
            StringComparer.Ordinal)
        {
            ["push-urls"] = HashText(string.Join("\0", urls))
        };
        return new RemoteCapture(
            WorkIdentityCanonicalizer.MapDigest(
                "ali-git-explicit-push-remote-v2",
                identities),
            helpers.ExecutionFiles);
    }

    private static HelperCapture CaptureRemoteHelpers(
        IReadOnlyList<string> urls,
        AliGitProviderPin provider)
    {
        var commands = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            if (Path.IsPathRooted(url) || IsRelativeLocalPath(url))
            {
                commands.Add("git-receive-pack");
                continue;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new InvalidDataException("The exact Git push URL is not supported.");
            }
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "file":
                    commands.Add("git-receive-pack");
                    break;
                case "http":
                    commands.Add("git-remote-http");
                    break;
                case "https":
                    commands.Add("git-remote-https");
                    break;
                default:
                    throw new InvalidDataException(
                        "The exact Git push URL requires an unsupported external transport helper.");
            }
        }

        var identities = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var executionFiles = new List<AliGitExecutionFileBinding>();
        foreach (var command in commands)
        {
            var binding = provider.CaptureProviderCommandBinding(command);
            identities[command] = binding.Identity;
            executionFiles.Add(binding);
        }
        return new HelperCapture(
            identities,
            Array.AsReadOnly(executionFiles.ToArray()));
    }

    private static IReadOnlyList<AliGitExecutionFileBinding> MergeExecutionFiles(
        params IEnumerable<AliGitExecutionFileBinding>[] groups)
    {
        var values = new Dictionary<string, AliGitExecutionFileBinding>(PathComparer);
        foreach (var binding in groups.SelectMany(group => group))
        {
            if (values.TryGetValue(binding.PhysicalPath, out var prior)
                && !string.Equals(prior.Identity, binding.Identity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One effective Git execution input has conflicting captured identities.");
            }
            values[binding.PhysicalPath] = binding;
        }
        return Array.AsReadOnly(values.Values
            .OrderBy(item => item.PhysicalPath, PathComparer)
            .ToArray());
    }

    private static void ValidateRemoteUrls(IReadOnlyList<string> urls)
    {
        if (urls.Count is < 1 or > 32
            || urls.Any(url => url.Length > 4096 || url.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException(
                "The exact Git push remote exceeds its fixed URL schema or bound.");
        }
    }

    private static bool IsRelativeLocalPath(string value)
    {
        if (value.Contains("::", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return false;
        }
        if (OperatingSystem.IsWindows()
            && value.Length >= 2
            && char.IsAsciiLetter(value[0])
            && value[1] == ':')
        {
            return true;
        }
        return !value.Contains(':');
    }

    private static bool IsCredentialHelperKey(string key) =>
        string.Equals(key, "credential.helper", StringComparison.OrdinalIgnoreCase)
        || (key.StartsWith("credential.", StringComparison.OrdinalIgnoreCase)
            && key.EndsWith(".helper", StringComparison.OrdinalIgnoreCase));

    private static bool IsFilterCommandKey(string key) =>
        key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase)
        && (key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(".smudge", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(".process", StringComparison.OrdinalIgnoreCase));

    private static string ParseSimpleCredentialHelper(string value)
    {
        var helper = value.Trim();
        if (!AliGitProviderIdentity.CommandNamePattern().IsMatch(helper))
        {
            throw new InvalidDataException(
                "A dynamic or path-selected Git credential helper is unsupported.");
        }
        return "git-credential-" + helper;
    }

    private static string ParseSafeFilterExecutable(string value)
    {
        var command = value.Trim();
        if (!string.Equals(command, "git-lfs filter-process", StringComparison.Ordinal)
            && !string.Equals(command, "git-lfs clean -- %f", StringComparison.Ordinal)
            && !string.Equals(command, "git-lfs smudge -- %f", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Only exact canonical git-lfs filter helper recipes are supported.");
        }
        return "git-lfs";
    }

    private static void RequireSameMap(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second,
        string message)
    {
        if (first.Count != second.Count)
        {
            throw new InvalidDataException(message);
        }
        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var value)
                || !FixedTimeTextEquals(pair.Value, value))
            {
                throw new InvalidDataException(message);
            }
        }
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length
                   && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool ContainsAsciiIgnoreCase(byte[] bytes, string text)
    {
        var pattern = Encoding.ASCII.GetBytes(text.ToLowerInvariant());
        try
        {
            for (var offset = 0; offset <= bytes.Length - pattern.Length; offset++)
            {
                var matched = true;
                for (var index = 0; index < pattern.Length; index++)
                {
                    var value = bytes[offset + index];
                    if (value is >= (byte)'A' and <= (byte)'Z')
                    {
                        value = (byte)(value + 32);
                    }
                    if (value != pattern[index])
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pattern);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashText(string value) =>
        HashBytes(Encoding.UTF8.GetBytes(value));

    private sealed record ConfigurationEntry(
        string Scope,
        string Origin,
        string Key,
        string Value);

    private sealed record ConfigurationRoot(
        string Path,
        string Scope);

    private sealed record StaticConfigurationCapture(
        IReadOnlyList<ConfigurationEntry> Entries,
        SortedDictionary<string, string> Files,
        SortedDictionary<string, AliGitExecutionFileBinding> ExecutionFiles);

    private sealed record ConfigurationFileCapture(
        long Length,
        string Identity,
        string Text,
        AliGitExecutionFileBinding ExecutionFile);

    private sealed record HelperCapture(
        SortedDictionary<string, string> Identities,
        IReadOnlyList<AliGitExecutionFileBinding> ExecutionFiles);

    private sealed record RemoteCapture(
        string Digest,
        IReadOnlyList<AliGitExecutionFileBinding> ExecutionFiles);
}
