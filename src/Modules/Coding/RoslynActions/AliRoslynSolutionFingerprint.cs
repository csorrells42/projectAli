using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynSolutionFingerprintSnapshot(
    string Sha256,
    int ProjectCount,
    int DocumentCount,
    int MetadataReferenceCount,
    int AnalyzerReferenceCount);

/// <summary>Produces a stable digest of every semantic input visible to Roslyn.</summary>
internal sealed class AliRoslynSolutionFingerprint(AliRoslynTargetReferenceResolver referenceResolver)
{
    private static readonly Version PinnedRoslynVersion = new(5, 6, 0, 0);
    private static readonly BindingFlags DeclaredInstanceProperties =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal static ImmutableHashSet<string> CompilationOptionProperties { get; } =
    [
        "AssemblyIdentityComparer", "CheckOverflow", "ConcurrentBuild", "CryptoKeyContainer",
        "CryptoKeyFile", "CryptoPublicKey", "CurrentLocalTime", "DebugPlusMode", "DelaySign",
        "Deterministic", "EnableEditAndContinue", "Errors", "Features", "GeneralDiagnosticOption",
        "Language", "MainTypeName", "MetadataImportOptions", "MetadataReferenceResolver", "ModuleName",
        "NullableContextOptions", "OptimizationLevel", "OutputKind", "Platform", "PublicSign",
        "ReferencesSupersedeLowerVersions", "ReportSuppressedDiagnostics", "ScriptClassName",
        "SourceReferenceResolver", "SpecificDiagnosticOptions", "StrongNameProvider",
        "SyntaxTreeOptionsProvider", "WarningLevel", "XmlReferenceResolver"
    ];

    internal static ImmutableHashSet<string> CSharpCompilationOptionProperties { get; } =
    [
        "AllowUnsafe", "Language", "MemorySafetyRules", "NullableContextOptions",
        "TopLevelBinderFlags", "UseUpdatedMemorySafetyRules", "Usings"
    ];

    internal static ImmutableHashSet<string> ParseOptionProperties { get; } =
    [
        "DocumentationMode", "Errors", "Features", "Kind", "Language",
        "PreprocessorSymbolNames", "SpecifiedKind"
    ];

    internal static ImmutableHashSet<string> CSharpParseOptionProperties { get; } =
    [
        "Features", "FileBasedProgram", "InterceptorsNamespaces", "Language", "LanguageVersion",
        "PreprocessorSymbolNames", "PreprocessorSymbols", "SpecifiedLanguageVersion"
    ];

    internal static ImmutableHashSet<string> DiagnosticProperties { get; } =
    [
        "AdditionalLocations", "Arguments", "Category", "Code", "CustomTags", "DefaultSeverity",
        "Descriptor", "Id", "IsEnabledByDefault", "IsSuppressed", "IsUnsuppressedError",
        "IsWarningAsError", "Location",
        "ProgrammaticSuppressionInfo", "Properties", "Severity", "WarningLevel"
    ];

    internal static ImmutableHashSet<string> DiagnosticDescriptorProperties { get; } =
    [
        "Category", "CustomTags", "DefaultSeverity", "Description", "HelpLinkUri", "Id",
        "ImmutableCustomTags", "IsEnabledByDefault", "MessageFormat", "Title"
    ];

    private static readonly Lazy<bool> Roslyn56SurfaceValidated = new(() =>
    {
        RequireAssemblyVersion(typeof(CompilationOptions).Assembly, "Microsoft.CodeAnalysis");
        RequireAssemblyVersion(typeof(CSharpCompilationOptions).Assembly, "Microsoft.CodeAnalysis.CSharp");
        RequireExactPropertySurface(typeof(CompilationOptions), CompilationOptionProperties);
        RequireExactPropertySurface(typeof(CSharpCompilationOptions), CSharpCompilationOptionProperties);
        RequireExactPropertySurface(typeof(ParseOptions), ParseOptionProperties);
        RequireExactPropertySurface(typeof(CSharpParseOptions), CSharpParseOptionProperties);
        RequireExactPropertySurface(typeof(Diagnostic), DiagnosticProperties);
        RequireExactPropertySurface(typeof(DiagnosticDescriptor), DiagnosticDescriptorProperties);
        return true;
    });

    public async Task<AliRoslynSolutionFingerprintSnapshot> CaptureAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "ali-roslyn-solution-fingerprint-v2-roslyn-5.6");
        AddPath(hash, solution.FilePath);

        var documentCount = 0;
        var metadataCount = 0;
        var analyzerCount = 0;
        foreach (var project in solution.Projects.OrderBy(ProjectKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(hash, "project");
            Add(hash, ProjectKey(project));
            Add(hash, project.Name);
            Add(hash, project.AssemblyName);
            Add(hash, project.Language);
            AddPath(hash, project.FilePath);
            AddPath(hash, project.OutputFilePath);
            AddPath(hash, project.OutputRefFilePath);
            Add(hash, project.DefaultNamespace);
            AddCompilationOptions(hash, project, project.CompilationOptions);
            AddParseOptions(hash, project.ParseOptions);

            if (!string.IsNullOrWhiteSpace(project.FilePath) && File.Exists(project.FilePath))
            {
                Add(hash, await AliRoslynTargetReferenceResolver.HashFileAsync(project.FilePath, cancellationToken)
                    .ConfigureAwait(false));
            }

            foreach (var reference in project.ProjectReferences
                         .OrderBy(item => ProjectReferenceKey(solution, item), StringComparer.Ordinal))
            {
                Add(hash, "project-reference");
                Add(hash, ProjectReferenceKey(solution, reference));
                Add(hash, reference.EmbedInteropTypes.ToString(CultureInfo.InvariantCulture));
                foreach (var alias in reference.Aliases)
                {
                    Add(hash, alias);
                }
            }

            var references = await referenceResolver.ResolveAsync(project, cancellationToken).ConfigureAwait(false);
            foreach (var reference in references.MetadataReferences
                         .OrderBy(item => NormalizePath(item.PhysicalPath), StringComparer.Ordinal))
            {
                metadataCount++;
                Add(hash, "metadata-reference");
                AddPath(hash, reference.PhysicalPath);
                Add(hash, reference.Sha256);
                Add(hash, reference.Properties.Kind.ToString());
                Add(hash, reference.Properties.EmbedInteropTypes.ToString(CultureInfo.InvariantCulture));
                foreach (var alias in reference.Properties.Aliases)
                {
                    Add(hash, alias);
                }
            }

            foreach (var analyzer in references.AnalyzerReferences
                         .OrderBy(item => NormalizePath(item.PhysicalPath), StringComparer.Ordinal))
            {
                analyzerCount++;
                Add(hash, "analyzer-reference");
                AddPath(hash, analyzer.PhysicalPath);
                Add(hash, analyzer.Sha256);
                Add(hash, analyzer.Reference.GetType().AssemblyQualifiedName);
            }

            foreach (var document in project.Documents.OrderBy(DocumentKey, StringComparer.Ordinal))
            {
                documentCount++;
                await AddDocumentAsync(hash, "document", document, cancellationToken).ConfigureAwait(false);
            }

            foreach (var document in project.AdditionalDocuments.OrderBy(DocumentKey, StringComparer.Ordinal))
            {
                documentCount++;
                await AddTextDocumentAsync(hash, "additional-document", document, cancellationToken).ConfigureAwait(false);
            }

            foreach (var document in project.AnalyzerConfigDocuments.OrderBy(DocumentKey, StringComparer.Ordinal))
            {
                documentCount++;
                await AddTextDocumentAsync(hash, "analyzer-config-document", document, cancellationToken).ConfigureAwait(false);
            }
        }

        var solutionAnalyzers = await referenceResolver.ResolveSolutionAnalyzersAsync(solution, cancellationToken)
            .ConfigureAwait(false);
        foreach (var analyzer in solutionAnalyzers.OrderBy(item => NormalizePath(item.PhysicalPath), StringComparer.Ordinal))
        {
            analyzerCount++;
            Add(hash, "solution-analyzer-reference");
            AddPath(hash, analyzer.PhysicalPath);
            Add(hash, analyzer.Sha256);
            Add(hash, analyzer.Reference.GetType().AssemblyQualifiedName);
        }

        return new(
            Convert.ToHexString(hash.GetHashAndReset()),
            solution.ProjectIds.Count,
            documentCount,
            metadataCount,
            analyzerCount);
    }

    private static async Task AddDocumentAsync(
        IncrementalHash hash,
        string kind,
        Document document,
        CancellationToken cancellationToken)
    {
        Add(hash, document.SourceCodeKind.ToString());
        await AddTextDocumentAsync(hash, kind, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTextDocumentAsync(
        IncrementalHash hash,
        string kind,
        TextDocument document,
        CancellationToken cancellationToken)
    {
        Add(hash, kind);
        Add(hash, DocumentKey(document));
        Add(hash, document.Name);
        AddPath(hash, document.FilePath);
        foreach (var folder in document.Folders)
        {
            Add(hash, folder);
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        Add(hash, text.ChecksumAlgorithm.ToString());
        Add(hash, Convert.ToHexString(text.GetChecksum().AsSpan()));
        Add(hash, text.Encoding?.WebName);
    }

    private static void AddCompilationOptions(
        IncrementalHash hash,
        Project project,
        CompilationOptions? options)
    {
        _ = Roslyn56SurfaceValidated.Value;
        Add(hash, "compilation-options");
        Add(hash, options?.GetType().AssemblyQualifiedName);
        if (options is null)
        {
            return;
        }
        if (options.GetType() != typeof(CSharpCompilationOptions)
            || options is not CSharpCompilationOptions csharp)
        {
            throw new InvalidDataException(
                "The solution fingerprint supports only the pinned Roslyn 5.6 C# option contract.");
        }

        AddNamed(hash, "Language", options.Language);
        AddNamedEnum(hash, "OutputKind", options.OutputKind);
        AddNamed(hash, "ModuleName", options.ModuleName);
        AddNamed(hash, "MainTypeName", options.MainTypeName);
        AddNamed(hash, "ScriptClassName", options.ScriptClassName);
        AddNamedEnum(hash, "OptimizationLevel", options.OptimizationLevel);
        AddNamed(hash, "CheckOverflow", options.CheckOverflow);
        AddNamedEnum(hash, "Platform", options.Platform);
        AddNamedEnum(hash, "GeneralDiagnosticOption", options.GeneralDiagnosticOption);
        AddNamed(hash, "WarningLevel", options.WarningLevel);
        AddNamed(hash, "ConcurrentBuild", options.ConcurrentBuild);
        AddNamed(hash, "Deterministic", options.Deterministic);
        AddNamed(hash, "ReportSuppressedDiagnostics", options.ReportSuppressedDiagnostics);
        AddNamedEnum(hash, "MetadataImportOptions", options.MetadataImportOptions);
        AddNamedEnum(hash, "NullableContextOptions", options.NullableContextOptions);
        AddNamed(hash, "CryptoKeyContainer", options.CryptoKeyContainer);
        AddNamed(hash, "CryptoKeyFile", options.CryptoKeyFile);
        AddNamedImmutableBytes(hash, "CryptoPublicKey", options.CryptoPublicKey);
        AddNamedNullableBoolean(hash, "DelaySign", options.DelaySign);
        AddNamed(hash, "PublicSign", options.PublicSign);
        AddNamedDiagnostics(hash, "Errors", options.Errors);
        Add(hash, "SpecificDiagnosticOptions");
        foreach (var item in options.SpecificDiagnosticOptions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Add(hash, item.Key);
            AddEnum(hash, item.Value);
        }

        AddNamed(
            hash,
            "DebugPlusMode",
            RequirePinnedProperty<bool>(options, typeof(CompilationOptions), "DebugPlusMode"));
        AddNamed(
            hash,
            "ReferencesSupersedeLowerVersions",
            RequirePinnedProperty<bool>(options, typeof(CompilationOptions), "ReferencesSupersedeLowerVersions"));
        AddNamed(
            hash,
            "EnableEditAndContinue",
            RequirePinnedProperty<bool>(options, typeof(CompilationOptions), "EnableEditAndContinue"));
        // Roslyn 5.6 exposes CompilationOptions.Features as part of its structural surface, but
        // both its getter and CSharpCompilationOptions.CommonWithFeatures throw
        // NotImplementedException and there is no backing state. Exact C# options therefore have
        // one canonical Features state in this pinned version. The reflection coverage test locks
        // this assumption and will fail if a later Roslyn version makes the property representable.
        AddNamed(hash, "Features", "unrepresentable-not-implemented-roslyn-5.6");
        var currentLocalTime = RequirePinnedProperty<DateTime>(
            options,
            typeof(CompilationOptions),
            "CurrentLocalTime");
        Add(hash, "CurrentLocalTime");
        Add(hash, currentLocalTime.ToBinary().ToString(CultureInfo.InvariantCulture));
        AddEnum(hash, currentLocalTime.Kind);

        AddNamed(hash, "AllowUnsafe", csharp.AllowUnsafe);
        AddNamedStringSequence(hash, "Usings", csharp.Usings);
        AddNamed(
            hash,
            "TopLevelBinderFlags",
            RequirePinnedEnumValue(csharp, typeof(CSharpCompilationOptions), "TopLevelBinderFlags"));
        AddNamed(
            hash,
            "MemorySafetyRules",
            RequirePinnedProperty<int>(csharp, typeof(CSharpCompilationOptions), "MemorySafetyRules"));
        AddNamed(
            hash,
            "UseUpdatedMemorySafetyRules",
            RequirePinnedProperty<bool>(csharp, typeof(CSharpCompilationOptions), "UseUpdatedMemorySafetyRules"));

        AddProvider(hash, "MetadataReferenceResolver", options.MetadataReferenceResolver, project);
        AddProvider(hash, "XmlReferenceResolver", options.XmlReferenceResolver, project);
        AddProvider(hash, "SourceReferenceResolver", options.SourceReferenceResolver, project);
        AddProvider(hash, "StrongNameProvider", options.StrongNameProvider, project);
        AddProvider(hash, "AssemblyIdentityComparer", options.AssemblyIdentityComparer, project);
        AddProvider(hash, "SyntaxTreeOptionsProvider", options.SyntaxTreeOptionsProvider, project);
    }

    private static void AddParseOptions(IncrementalHash hash, ParseOptions? options)
    {
        _ = Roslyn56SurfaceValidated.Value;
        Add(hash, "parse-options");
        Add(hash, options?.GetType().AssemblyQualifiedName);
        if (options is null)
        {
            return;
        }
        if (options.GetType() != typeof(CSharpParseOptions)
            || options is not CSharpParseOptions csharp)
        {
            throw new InvalidDataException(
                "The solution fingerprint supports only the pinned Roslyn 5.6 C# parse-option contract.");
        }

        AddNamed(hash, "Language", options.Language);
        AddNamedEnum(hash, "Kind", options.Kind);
        AddNamedEnum(hash, "SpecifiedKind", options.SpecifiedKind);
        AddNamedEnum(hash, "DocumentationMode", options.DocumentationMode);
        AddNamedDiagnostics(hash, "Errors", options.Errors);
        Add(hash, "Features");
        foreach (var item in options.Features.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Add(hash, item.Key);
            Add(hash, item.Value);
        }

        AddNamedEnum(hash, "LanguageVersion", csharp.LanguageVersion);
        AddNamedEnum(hash, "SpecifiedLanguageVersion", csharp.SpecifiedLanguageVersion);
        AddNamedStringSequence(hash, "PreprocessorSymbolNames", csharp.PreprocessorSymbolNames);
        AddNamedStringSequence(
            hash,
            "PreprocessorSymbols",
            RequirePinnedProperty<object>(csharp, typeof(CSharpParseOptions), "PreprocessorSymbols"));
        AddNamed(
            hash,
            "FileBasedProgram",
            RequirePinnedProperty<bool>(csharp, typeof(CSharpParseOptions), "FileBasedProgram"));
        AddNamedNestedStringSequence(
            hash,
            "InterceptorsNamespaces",
            RequirePinnedProperty<object>(csharp, typeof(CSharpParseOptions), "InterceptorsNamespaces"));
    }

    internal static void RequirePinnedRoslyn56OptionSurface() =>
        _ = Roslyn56SurfaceValidated.Value;

    private static void AddProvider(
        IncrementalHash hash,
        string name,
        object? provider,
        Project project)
    {
        Add(hash, name);
        if (provider is null)
        {
            Add(hash, "null");
            return;
        }

        var type = provider.GetType();
        Add(hash, type.AssemblyQualifiedName);
        switch (type.FullName)
        {
            case "Microsoft.CodeAnalysis.Host.WorkspaceMetadataFileReferenceResolver":
                RequireProviderType(type, "Microsoft.CodeAnalysis.Workspaces");
                RequireExactFieldSurface(type, ["_metadataService", "PathResolver"]);
                var metadataService = RequirePinnedField(
                    provider,
                    type.FullName,
                    "_metadataService",
                    "Microsoft.CodeAnalysis.Host.IMetadataService");
                RequireExactRuntimeType(
                    metadataService,
                    "Microsoft.CodeAnalysis.Host.MetadataServiceFactory+MetadataService",
                    "Microsoft.CodeAnalysis.Workspaces");
                RequireExactFieldSurface(metadataService.GetType(), []);
                Add(hash, metadataService.GetType().AssemblyQualifiedName);
                AddRelativePathResolver(
                    hash,
                    RequirePinnedField(
                        provider,
                        type.FullName,
                        "PathResolver",
                        "Microsoft.CodeAnalysis.RelativePathResolver"));
                return;

            case "Microsoft.CodeAnalysis.XmlFileResolver":
                RequireProviderType(type, "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(type, ["_baseDirectory"]);
                AddPath(
                    hash,
                    RequirePinnedOptionalField(
                        provider,
                        type.FullName,
                        "_baseDirectory",
                        typeof(string).FullName!) as string);
                return;

            case "Microsoft.CodeAnalysis.SourceFileResolver":
                RequireProviderType(type, "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(type, ["_baseDirectory", "_pathMap", "_searchPaths"]);
                AddPath(
                    hash,
                    RequirePinnedOptionalField(
                        provider,
                        type.FullName,
                        "_baseDirectory",
                        typeof(string).FullName!) as string);
                AddPathSequence(
                    hash,
                    RequirePinnedField(
                        provider,
                        type.FullName,
                        "_searchPaths",
                        "System.Collections.Immutable.ImmutableArray`1[[System.String, System.Private.CoreLib]]",
                        matchAssemblyQualifiedGeneric: true));
                AddPathMap(
                    hash,
                    RequirePinnedField(
                        provider,
                        type.FullName,
                        "_pathMap",
                        "System.Collections.Immutable.ImmutableArray`1",
                        matchGenericDefinition: true));
                return;

            case "Microsoft.CodeAnalysis.DesktopStrongNameProvider":
                RequireProviderType(type, "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(type, ["<FileSystem>k__BackingField", "_keyFileSearchPaths"]);
                AddPathSequence(
                    hash,
                    RequirePinnedField(
                        provider,
                        type.FullName,
                        "_keyFileSearchPaths",
                        "System.Collections.Immutable.ImmutableArray`1[[System.String, System.Private.CoreLib]]",
                        matchAssemblyQualifiedGeneric: true));
                var fileSystem = RequirePinnedField(
                    provider,
                    type.FullName,
                    "<FileSystem>k__BackingField",
                    "Microsoft.CodeAnalysis.StrongNameFileSystem");
                RequireExactRuntimeType(
                    fileSystem,
                    "Microsoft.CodeAnalysis.StrongNameFileSystem",
                    "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(fileSystem.GetType(), ["_signingTempPath"]);
                AddPath(
                    hash,
                    RequirePinnedOptionalField(
                        fileSystem,
                        fileSystem.GetType().FullName!,
                        "_signingTempPath",
                        typeof(string).FullName!) as string);
                return;

            case "Microsoft.CodeAnalysis.DesktopAssemblyIdentityComparer":
                RequireProviderType(type, "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(type, ["policy"]);
                var policy = RequirePinnedField(
                    provider,
                    type.FullName,
                    "policy",
                    "Microsoft.CodeAnalysis.AssemblyPortabilityPolicy");
                RequireExactRuntimeType(
                    policy,
                    "Microsoft.CodeAnalysis.AssemblyPortabilityPolicy",
                    "Microsoft.CodeAnalysis");
                RequireExactFieldSurface(
                    policy.GetType(),
                    [
                        "SuppressSilverlightLibraryAssembliesPortability",
                        "SuppressSilverlightPlatformAssembliesPortability"
                    ]);
                AddNamed(
                    hash,
                    "SuppressSilverlightLibraryAssembliesPortability",
                    (bool)RequirePinnedField(
                        policy,
                        policy.GetType().FullName!,
                        "SuppressSilverlightLibraryAssembliesPortability",
                        typeof(bool).FullName!));
                AddNamed(
                    hash,
                    "SuppressSilverlightPlatformAssembliesPortability",
                    (bool)RequirePinnedField(
                        policy,
                        policy.GetType().FullName!,
                        "SuppressSilverlightPlatformAssembliesPortability",
                        typeof(bool).FullName!));
                return;

            case "Microsoft.CodeAnalysis.ProjectState+ProjectSyntaxTreeOptionsProvider":
                RequireProviderType(type, "Microsoft.CodeAnalysis.Workspaces");
                RequireExactFieldSurface(type, ["_lazyAnalyzerConfigSet"]);
                var cache = RequirePinnedField(
                    provider,
                    type.FullName,
                    "_lazyAnalyzerConfigSet",
                    "Microsoft.CodeAnalysis.ProjectState+AnalyzerConfigOptionsCache");
                RequireExactRuntimeType(
                    cache,
                    "Microsoft.CodeAnalysis.ProjectState+AnalyzerConfigOptionsCache",
                    "Microsoft.CodeAnalysis.Workspaces");
                RequireExactFieldSurface(cache.GetType(), ["<fallbackOptions>P", "Lazy"]);
                AddStructuredAnalyzerConfigOptions(
                    hash,
                    RequirePinnedOptionalField(
                        cache,
                        cache.GetType().FullName!,
                        "<fallbackOptions>P",
                        "Microsoft.CodeAnalysis.Diagnostics.StructuredAnalyzerConfigOptions"));
                AddNamed(hash, "AnalyzerConfigDocuments", project.AnalyzerConfigDocuments.Count());
                foreach (var document in project.AnalyzerConfigDocuments.OrderBy(DocumentKey, StringComparer.Ordinal))
                {
                    Add(hash, DocumentKey(document));
                }
                Add(hash, "analyzer-config-document-text-is-canonicalized-by-project-fingerprint");
                return;

            default:
                throw new InvalidDataException(
                    $"The {name} type is not supported by the pinned Roslyn 5.6 canonical serializer.");
        }
    }

    private static void AddRelativePathResolver(IncrementalHash hash, object resolver)
    {
        RequireExactRuntimeType(
            resolver,
            "Microsoft.CodeAnalysis.RelativePathResolver",
            "Microsoft.CodeAnalysis");
        RequireExactFieldSurface(
            resolver.GetType(),
            ["<BaseDirectory>k__BackingField", "<SearchPaths>k__BackingField"]);
        AddPathSequence(
            hash,
            RequirePinnedField(
                resolver,
                resolver.GetType().FullName!,
                "<SearchPaths>k__BackingField",
                "System.Collections.Immutable.ImmutableArray`1[[System.String, System.Private.CoreLib]]",
                matchAssemblyQualifiedGeneric: true));
        AddPath(
            hash,
            RequirePinnedOptionalField(
                resolver,
                resolver.GetType().FullName!,
                "<BaseDirectory>k__BackingField",
                typeof(string).FullName!) as string);
    }

    private static void AddStructuredAnalyzerConfigOptions(IncrementalHash hash, object? structured)
    {
        if (structured is null)
        {
            Add(hash, "null-fallback-options");
            return;
        }

        RequireExactRuntimeType(
            structured,
            "Microsoft.CodeAnalysis.Diagnostics.StructuredAnalyzerConfigOptions+Implementation",
            "Microsoft.CodeAnalysis.Workspaces");
        RequireExactFieldSurface(
            structured.GetType(),
            ["_fallback", "_lazyNamingStylePreferences", "_options"]);
        var options = RequirePinnedField(
            structured,
            structured.GetType().FullName!,
            "_options",
            "Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions");
        RequireExactRuntimeType(
            options,
            "Microsoft.CodeAnalysis.Diagnostics.DictionaryAnalyzerConfigOptions",
            "Microsoft.CodeAnalysis.Workspaces");
        RequireExactFieldSurface(options.GetType(), ["Options"]);
        AddStringMap(
            hash,
            RequirePinnedField(
                options,
                options.GetType().FullName!,
                "Options",
                "System.Collections.Immutable.ImmutableDictionary`2",
                matchGenericDefinition: true));
        AddStructuredAnalyzerConfigOptions(
            hash,
            RequirePinnedOptionalField(
                structured,
                structured.GetType().FullName!,
                "_fallback",
                "Microsoft.CodeAnalysis.Diagnostics.StructuredAnalyzerConfigOptions"));
    }

    private static void AddNamedDiagnostics(
        IncrementalHash hash,
        string name,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Add(hash, name);
        if (diagnostics.IsDefault)
        {
            Add(hash, "default");
            return;
        }

        Add(hash, diagnostics.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var diagnostic in diagnostics)
        {
            var diagnosticAssembly = diagnostic.GetType().Assembly;
            var diagnosticAssemblyName = diagnosticAssembly.GetName().Name;
            if (diagnosticAssemblyName is not ("Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp"))
            {
                throw new InvalidDataException(
                    "A pinned Roslyn option diagnostic came from an unsupported provider assembly.");
            }
            RequireAssemblyVersion(diagnosticAssembly, diagnosticAssemblyName);
            Add(hash, diagnostic.GetType().AssemblyQualifiedName);
            Add(hash, diagnostic.Id);
            AddEnum(hash, diagnostic.Severity);
            AddEnum(hash, diagnostic.DefaultSeverity);
            Add(hash, diagnostic.WarningLevel.ToString(CultureInfo.InvariantCulture));
            Add(hash, diagnostic.IsSuppressed.ToString(CultureInfo.InvariantCulture));
            Add(hash, diagnostic.IsWarningAsError.ToString(CultureInfo.InvariantCulture));
            Add(hash, RequirePinnedProperty<string>(diagnostic, typeof(Diagnostic), "Category"));
            AddNamed(
                hash,
                "Code",
                RequirePinnedProperty<int>(diagnostic, typeof(Diagnostic), "Code"));
            AddNamed(
                hash,
                "IsEnabledByDefault",
                RequirePinnedProperty<bool>(
                    diagnostic,
                    typeof(Diagnostic),
                    "IsEnabledByDefault"));
            AddNamedStringSequence(
                hash,
                "CustomTags",
                RequirePinnedProperty<object>(diagnostic, typeof(Diagnostic), "CustomTags"));
            AddDiagnosticArguments(
                hash,
                RequirePinnedProperty<object>(diagnostic, typeof(Diagnostic), "Arguments"));
            if (ReadPinnedPropertyValue(
                    diagnostic,
                    typeof(Diagnostic),
                    "ProgrammaticSuppressionInfo") is not null)
            {
                throw new InvalidDataException(
                    "A pinned Roslyn option diagnostic has unsupported programmatic suppression state.");
            }
            Add(hash, "programmatic-suppression-none");

            var descriptor = diagnostic.Descriptor;
            if (descriptor.GetType() != typeof(DiagnosticDescriptor))
            {
                throw new InvalidDataException(
                    "A pinned Roslyn option diagnostic has an unsupported descriptor type.");
            }
            Add(hash, descriptor.Id);
            Add(hash, descriptor.Category);
            AddEnum(hash, descriptor.DefaultSeverity);
            Add(hash, descriptor.IsEnabledByDefault.ToString(CultureInfo.InvariantCulture));
            Add(hash, descriptor.HelpLinkUri);
            Add(hash, descriptor.Title.ToString(CultureInfo.InvariantCulture));
            Add(hash, descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture));
            Add(hash, descriptor.Description.ToString(CultureInfo.InvariantCulture));
            var customTags = descriptor.CustomTags.ToArray();
            Add(hash, customTags.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var customTag in customTags)
            {
                Add(hash, customTag);
            }

            // Invariant message rendering is Roslyn's stable public projection of the descriptor's
            // message arguments. Pairing it with the invariant MessageFormat preserves both the
            // template and its fully substituted argument result without localized current-culture
            // output or an arbitrary object.ToString fallback.
            Add(hash, diagnostic.GetMessage(CultureInfo.InvariantCulture));
            Add(hash, diagnostic.Properties.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var property in diagnostic.Properties.OrderBy(
                         item => item.Key,
                         StringComparer.Ordinal))
            {
                Add(hash, property.Key);
                Add(hash, property.Value);
            }

            // CompilationOptions.Errors and ParseOptions.Errors are option-validation diagnostics
            // and have no source/metadata location in pinned Roslyn 5.6. A located option error
            // would introduce additional semantic state that this serializer does not guess at.
            if (diagnostic.Location.Kind != LocationKind.None
                || diagnostic.AdditionalLocations.Count != 0)
            {
                throw new InvalidDataException(
                    "A pinned Roslyn option diagnostic has unsupported location state.");
            }
            Add(hash, "location-none");
        }
    }

    private static void AddDiagnosticArguments(IncrementalHash hash, object values)
    {
        Add(hash, "Arguments");
        if (values is string || values is not IEnumerable sequence)
        {
            throw new InvalidDataException(
                "A pinned Roslyn option diagnostic has an unexpected argument collection.");
        }

        var arguments = sequence.Cast<object?>().ToArray();
        Add(hash, arguments.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                Add(hash, "argument-null");
                continue;
            }

            Add(hash, argument.GetType().AssemblyQualifiedName);
            switch (argument)
            {
                case string text:
                    Add(hash, text);
                    break;
                case char character:
                    Add(hash, character.ToString());
                    break;
                case bool boolean:
                    Add(hash, boolean.ToString(CultureInfo.InvariantCulture));
                    break;
                case Enum enumValue:
                    AddEnum(hash, enumValue);
                    break;
                case byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal:
                    Add(hash, Convert.ToString(argument, CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new InvalidDataException(
                        "A pinned Roslyn option diagnostic has an unsupported message argument type.");
            }
        }
    }

    private static void AddNamedImmutableBytes(
        IncrementalHash hash,
        string name,
        ImmutableArray<byte> bytes)
    {
        Add(hash, name);
        Add(hash, bytes.IsDefault ? "default" : Convert.ToHexString(bytes.AsSpan()));
    }

    private static void AddNamedNullableBoolean(IncrementalHash hash, string name, bool? value)
    {
        Add(hash, name);
        Add(hash, value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
    }

    private static void AddNamedStringSequence(IncrementalHash hash, string name, object values)
    {
        Add(hash, name);
        AddStringSequence(hash, values, paths: false);
    }

    private static void AddPathSequence(IncrementalHash hash, object values) =>
        AddStringSequence(hash, values, paths: true);

    private static void AddStringSequence(IncrementalHash hash, object values, bool paths)
    {
        if (IsDefaultImmutableArray(values))
        {
            Add(hash, "default");
            return;
        }
        if (values is string || values is not IEnumerable sequence)
        {
            throw new InvalidDataException("A pinned Roslyn string sequence has an unexpected type.");
        }

        var materialized = sequence.Cast<object?>().ToArray();
        Add(hash, materialized.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var value in materialized)
        {
            if (value is not string text)
            {
                throw new InvalidDataException("A pinned Roslyn string sequence contains a non-string value.");
            }
            if (paths)
            {
                AddPath(hash, text);
            }
            else
            {
                Add(hash, text);
            }
        }
    }

    private static void AddNamedNestedStringSequence(IncrementalHash hash, string name, object values)
    {
        Add(hash, name);
        if (IsDefaultImmutableArray(values))
        {
            Add(hash, "default");
            return;
        }
        if (values is not IEnumerable outer)
        {
            throw new InvalidDataException("A pinned Roslyn nested string sequence has an unexpected type.");
        }

        var materialized = outer.Cast<object?>().ToArray();
        Add(hash, materialized.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var value in materialized)
        {
            if (value is null)
            {
                throw new InvalidDataException("A pinned Roslyn nested string sequence contains null.");
            }
            AddStringSequence(hash, value, paths: false);
        }
    }

    private static void AddPathMap(IncrementalHash hash, object values)
    {
        if (IsDefaultImmutableArray(values))
        {
            Add(hash, "default");
            return;
        }
        if (values is not IEnumerable sequence)
        {
            throw new InvalidDataException("A pinned Roslyn path map has an unexpected type.");
        }

        var materialized = sequence.Cast<object?>().ToArray();
        Add(hash, materialized.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var value in materialized)
        {
            if (value is not KeyValuePair<string, string> item)
            {
                throw new InvalidDataException("A pinned Roslyn path map contains an unexpected value.");
            }
            AddPath(hash, item.Key);
            Add(hash, item.Value);
        }
    }

    private static void AddStringMap(IncrementalHash hash, object values)
    {
        if (values is not IEnumerable<KeyValuePair<string, string>> map)
        {
            throw new InvalidDataException("A pinned Roslyn option map has an unexpected type.");
        }

        var materialized = map.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        Add(hash, materialized.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var item in materialized)
        {
            Add(hash, item.Key);
            Add(hash, item.Value);
        }
    }

    private static bool IsDefaultImmutableArray(object value)
    {
        var type = value.GetType();
        if (!type.IsGenericType
            || type.GetGenericTypeDefinition() != typeof(ImmutableArray<>))
        {
            return false;
        }

        return type.GetProperty("IsDefault", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value)
            is true;
    }

    private static void AddNamed(IncrementalHash hash, string name, string? value)
    {
        Add(hash, name);
        Add(hash, value);
    }

    private static void AddNamed(IncrementalHash hash, string name, bool value) =>
        AddNamed(hash, name, value.ToString(CultureInfo.InvariantCulture));

    private static void AddNamed(IncrementalHash hash, string name, int value) =>
        AddNamed(hash, name, value.ToString(CultureInfo.InvariantCulture));

    private static void AddNamed(IncrementalHash hash, string name, long value) =>
        AddNamed(hash, name, value.ToString(CultureInfo.InvariantCulture));

    private static void AddNamedEnum<T>(IncrementalHash hash, string name, T value)
        where T : struct, Enum
    {
        Add(hash, name);
        AddEnum(hash, value);
    }

    private static void AddEnum(IncrementalHash hash, object value)
    {
        var type = value.GetType();
        if (!type.IsEnum)
        {
            throw new InvalidDataException("A pinned Roslyn enum value has an unexpected type.");
        }
        Add(hash, type.FullName);
        Add(hash, Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
    }

    private static T RequirePinnedProperty<T>(object target, Type declaringType, string name)
    {
        var value = ReadPinnedPropertyValue(target, declaringType, name);
        if (value is not T typed)
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 property {declaringType.FullName}.{name} has an unexpected value type.");
        }
        return typed;
    }

    private static object? ReadPinnedPropertyValue(object target, Type declaringType, string name)
    {
        var property = declaringType.GetProperty(name, DeclaredInstanceProperties)
            ?? throw new InvalidDataException(
                $"The pinned Roslyn 5.6 property {declaringType.FullName}.{name} is missing.");
        try
        {
            return property.GetValue(target);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 property {declaringType.FullName}.{name} could not be read.",
                exception);
        }
    }

    private static long RequirePinnedEnumValue(object target, Type declaringType, string name)
    {
        var value = RequirePinnedProperty<object>(target, declaringType, name);
        if (!value.GetType().IsEnum)
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 property {declaringType.FullName}.{name} is not an enum.");
        }
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static object RequirePinnedField(
        object target,
        string declaringTypeName,
        string fieldName,
        string expectedTypeName,
        bool matchGenericDefinition = false,
        bool matchAssemblyQualifiedGeneric = false) =>
        RequirePinnedFieldCore(
            target,
            declaringTypeName,
            fieldName,
            expectedTypeName,
            allowNull: false,
            matchGenericDefinition,
            matchAssemblyQualifiedGeneric)!;

    private static object? RequirePinnedOptionalField(
        object target,
        string declaringTypeName,
        string fieldName,
        string expectedTypeName,
        bool matchGenericDefinition = false,
        bool matchAssemblyQualifiedGeneric = false) =>
        RequirePinnedFieldCore(
            target,
            declaringTypeName,
            fieldName,
            expectedTypeName,
            allowNull: true,
            matchGenericDefinition,
            matchAssemblyQualifiedGeneric);

    private static object? RequirePinnedFieldCore(
        object target,
        string declaringTypeName,
        string fieldName,
        string expectedTypeName,
        bool allowNull,
        bool matchGenericDefinition,
        bool matchAssemblyQualifiedGeneric)
    {
        var declaringType = FindType(target.GetType(), declaringTypeName);
        var field = declaringType.GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new InvalidDataException(
                $"The pinned Roslyn 5.6 field {declaringTypeName}.{fieldName} is missing.");
        var fieldTypeName = matchGenericDefinition && field.FieldType.IsGenericType
            ? field.FieldType.GetGenericTypeDefinition().FullName
            : matchAssemblyQualifiedGeneric
                ? SimplifyAssemblyQualifiedGeneric(field.FieldType)
                : field.FieldType.FullName;
        if (!string.Equals(fieldTypeName, expectedTypeName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 field {declaringTypeName}.{fieldName} changed type.");
        }

        var value = field.GetValue(target);
        if (value is null && !allowNull)
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 field {declaringTypeName}.{fieldName} is unexpectedly null.");
        }
        return value;
    }

    private static string? SimplifyAssemblyQualifiedGeneric(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName;
        }
        var arguments = string.Join(",", type.GetGenericArguments().Select(argument =>
            $"[{argument.FullName}, {argument.Assembly.GetName().Name}]"));
        return $"{type.GetGenericTypeDefinition().FullName}[{arguments}]";
    }

    private static Type FindType(Type type, string fullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
            {
                return current;
            }
        }
        throw new InvalidDataException($"The pinned Roslyn 5.6 type {fullName} is missing.");
    }

    private static void RequireProviderType(Type type, string assemblyName)
    {
        RequireAssemblyVersion(type.Assembly, assemblyName);
        if (type.Assembly.GetName().Name != assemblyName)
        {
            throw new InvalidDataException("A pinned Roslyn provider came from an unexpected assembly.");
        }
    }

    private static void RequireExactRuntimeType(object value, string typeName, string assemblyName)
    {
        var type = value.GetType();
        if (!string.Equals(type.FullName, typeName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The pinned Roslyn 5.6 provider state {typeName} changed type.");
        }
        RequireProviderType(type, assemblyName);
    }

    private static void RequireExactFieldSurface(Type type, IEnumerable<string> expected)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actual = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expectedSet))
        {
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 provider state surface {type.FullName} changed.");
        }
    }

    private static void RequireExactPropertySurface(Type type, IEnumerable<string> expected)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actual = type.GetProperties(DeclaredInstanceProperties)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expectedSet))
        {
            var missing = expectedSet.Except(actual).OrderBy(name => name, StringComparer.Ordinal);
            var unexpected = actual.Except(expectedSet).OrderBy(name => name, StringComparer.Ordinal);
            throw new InvalidDataException(
                $"The pinned Roslyn 5.6 option property surface {type.FullName} changed. "
                + $"Missing: [{string.Join(", ", missing)}]. "
                + $"Unexpected: [{string.Join(", ", unexpected)}].");
        }
    }

    private static void RequireAssemblyVersion(Assembly assembly, string expectedName)
    {
        var name = assembly.GetName();
        if (!string.Equals(name.Name, expectedName, StringComparison.Ordinal)
            || name.Version != PinnedRoslynVersion)
        {
            throw new InvalidDataException(
                $"The solution fingerprint requires {expectedName} {PinnedRoslynVersion} exactly.");
        }
    }

    private static string ProjectKey(Project project) =>
        NormalizePath(project.FilePath) + "|" + project.Name + "|" + project.AssemblyName + "|" + project.Language;

    private static string ProjectReferenceKey(Solution solution, ProjectReference reference) =>
        solution.GetProject(reference.ProjectId) is { } project
            ? ProjectKey(project)
            : "<missing>|" + reference.ProjectId.Id.ToString("D", CultureInfo.InvariantCulture);

    private static string DocumentKey(TextDocument document) =>
        NormalizePath(document.FilePath) + "|" + string.Join("/", document.Folders) + "|" + document.Name;

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static void AddPath(IncrementalHash hash, string? path) => Add(hash, NormalizePath(path));

    private static void Add(IncrementalHash hash, string? value)
    {
        Span<byte> presence = stackalloc byte[1];
        presence[0] = value is null ? (byte)0 : (byte)1;
        hash.AppendData(presence);
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
