using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ali.Core.Sources;

namespace Ali.Infrastructure.Sources;

public sealed record SourceCatalogRepairResult(
    int ExistingSourceCount,
    int AddedStarterSourceCount,
    bool CatalogCreated,
    string? BackupPath);

public sealed class FileSourceRetriever
{
    private const string BundledDefaultCatalogResourceName = "Ali.Infrastructure.Sources.curated_sources.seed.json";
    private const int MaxSourcesPerRequest = 3;
    private const int MaxExcerptCharacters = 2400;
    private const int MinimumSourceScore = 4;
    private const int MinimumPlannedSourceScore = 6;
    private const string NwsPointForecastType = "nws-point-forecast";
    private static readonly HashSet<string> GenericQueryTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "can",
        "current",
        "currently",
        "could",
        "does",
        "explain",
        "find",
        "give",
        "happening",
        "headlines",
        "latest",
        "news",
        "please",
        "recent",
        "search",
        "today",
        "update",
        "updates",
        "tell",
        "me",
        "was",
        "were",
        "whats",
        "what",
        "would",
        "you"
    };
    private static readonly HashSet<string> ImportantShortTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai",
        "ap",
        "uk",
        "us"
    };
    private static readonly HashSet<string> SportsIntentTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "espn",
        "football",
        "game",
        "ncaa",
        "score",
        "scores",
        "sports"
    };
    private static readonly HashSet<string> SportsSourceTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "espn",
        "football",
        "game",
        "games",
        "ncaa",
        "schedule",
        "score",
        "scores",
        "sports",
        "teams"
    };
    private static readonly HashSet<string> SportsIdentityTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "alabama",
        "auburn",
        "braves",
        "crimson",
        "rolltide",
        "tennessee",
        "tide",
        "titans",
        "vols",
        "volunteers"
    };
    private static readonly HashSet<string> SourceGroundingIntentTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "according",
        "current",
        "currently",
        "find",
        "forecast",
        "guidance",
        "happening",
        "headlines",
        "internet",
        "latest",
        "live",
        "lookup",
        "news",
        "now",
        "official",
        "online",
        "price",
        "recent",
        "recommend",
        "recommends",
        "said",
        "say",
        "says",
        "schedule",
        "score",
        "scores",
        "search",
        "source",
        "sources",
        "status",
        "today",
        "update",
        "updates",
        "weather",
        "web",
        "website"
    };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;

    public FileSourceRetriever(string dataRoot, HttpClient httpClient)
    {
        RootDirectory = Path.Combine(dataRoot, "Sources");
        CatalogPath = Path.Combine(RootDirectory, "curated_sources.json");
        ExamplePath = Path.Combine(RootDirectory, "curated_sources.example.json");
        _httpClient = httpClient;
    }

    public string RootDirectory { get; }

    public string CatalogPath { get; }

    public string ExamplePath { get; }

    public void WriteExample()
    {
        RepairStarterCatalog();
        var defaultCatalog = BuildDefaultCatalog();
        if (File.Exists(ExamplePath))
        {
            return;
        }

        using var stream = File.Create(ExamplePath);
        JsonSerializer.Serialize(stream, defaultCatalog, JsonOptions);
    }

    public SourceCatalogRepairResult RepairStarterCatalog()
    {
        Directory.CreateDirectory(RootDirectory);
        var defaultCatalog = BuildDefaultCatalog();
        if (!File.Exists(CatalogPath))
        {
            SaveCatalog(defaultCatalog);
            return new SourceCatalogRepairResult(0, defaultCatalog.Length, CatalogCreated: true, BackupPath: null);
        }

        SourceCatalogEntry[] currentCatalog;
        try
        {
            currentCatalog = LoadCatalog().ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backupPath = Path.Combine(
                RootDirectory,
                $"curated_sources.invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(CatalogPath, backupPath, overwrite: false);
            SaveCatalog(defaultCatalog);
            return new SourceCatalogRepairResult(0, defaultCatalog.Length, CatalogCreated: true, backupPath);
        }

        var existingIds = currentCatalog
            .Select(source => source.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDefaults = defaultCatalog
            .Where(source => !existingIds.Contains(source.Id))
            .ToArray();
        if (missingDefaults.Length > 0)
        {
            SaveCatalog(currentCatalog.Concat(missingDefaults));
        }

        return new SourceCatalogRepairResult(
            currentCatalog.Length,
            missingDefaults.Length,
            CatalogCreated: false,
            BackupPath: null);
    }

    private static SourceCatalogEntry[] BuildDefaultCatalog()
    {
        return LoadBundledDefaultCatalog() ?? BuildFallbackDefaultCatalog();
    }

    private static SourceCatalogEntry[]? LoadBundledDefaultCatalog()
    {
        try
        {
            var assembly = typeof(FileSourceRetriever).Assembly;
            using var stream = assembly.GetManifestResourceStream(BundledDefaultCatalogResourceName);
            if (stream is null)
            {
                return null;
            }

            var catalog = JsonSerializer.Deserialize<SourceCatalogEntry[]>(stream, JsonOptions);
            var enabledCatalog = catalog?
                .Where(source => source.Enabled)
                .GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            return enabledCatalog is { Length: > 0 } ? enabledCatalog : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static SourceCatalogEntry[] BuildFallbackDefaultCatalog()
    {
        return
        [
            new SourceCatalogEntry(
                Id: "cdc-respiratory-viruses",
                Topic: "health",
                Name: "CDC Respiratory Viruses",
                Url: "https://www.cdc.gov/respiratory-viruses/",
                Type: "web",
                TrustLevel: "primary",
                Keywords: ["health", "respiratory", "cdc", "virus", "flu", "covid"],
                Topics: ["health", "respiratory viruses", "flu", "covid"],
                Notes: "Primary US public-health source.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "national-weather-service",
                Topic: "weather",
                Name: "National Weather Service",
                Url: "https://www.weather.gov/",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["weather", "forecast", "warnings", "alerts", "radar", "storm", "temperature", "rain", "snow", "hurricane", "tornado"],
                Topics: ["weather", "forecast", "alerts", "radar", "storms", "local weather"],
                Notes: "Official NOAA/NWS weather portal for forecasts, alerts, radar, and weather safety.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "usa-gov-services",
                Topic: "government",
                Name: "USA.gov Government Services",
                Url: "https://www.usa.gov/",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["government", "benefits", "passport", "taxes", "travel", "voting", "housing", "jobs", "services", "usa.gov"],
                Topics: ["government services", "benefits", "passport", "taxes", "travel", "voting"],
                Notes: "Official US government guide for federal services and public information.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "irs-newsroom",
                Topic: "taxes",
                Name: "IRS Newsroom",
                Url: "https://www.irs.gov/newsroom",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["irs", "tax", "refund", "filing", "tax credit", "deduction", "forms", "tax news"],
                Topics: ["taxes", "irs", "refunds", "tax filing", "tax news"],
                Notes: "Official IRS news and tax update source.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "bls-cpi",
                Topic: "economy",
                Name: "BLS Consumer Price Index",
                Url: "https://www.bls.gov/cpi/",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["bls", "cpi", "inflation", "prices", "consumer price index", "economy", "cost of living"],
                Topics: ["economy", "inflation", "consumer prices", "cpi"],
                Notes: "Official Bureau of Labor Statistics CPI landing page.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "federal-reserve-monetary-policy",
                Topic: "economy",
                Name: "Federal Reserve Monetary Policy",
                Url: "https://www.federalreserve.gov/monetarypolicy.htm",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["federal reserve", "fed", "interest rates", "monetary policy", "fomc", "economy"],
                Topics: ["economy", "interest rates", "monetary policy", "federal reserve"],
                Notes: "Official Federal Reserve monetary policy source.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "windows-release-health",
                Topic: "software",
                Name: "Windows Release Health",
                Url: "https://learn.microsoft.com/en-us/windows/release-health/",
                Type: "docs",
                TrustLevel: "official",
                Keywords: ["windows", "update", "release health", "known issue", "safeguard", "windows 11", "windows update"],
                Topics: ["windows", "windows update", "release health", "known issues"],
                Notes: "Official Microsoft page for Windows update status, known issues, and release health.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "microsoft-powershell-docs",
                Topic: "software",
                Name: "Microsoft PowerShell Documentation",
                Url: "https://learn.microsoft.com/en-us/powershell/",
                Type: "docs",
                TrustLevel: "official",
                Keywords: ["powershell", "microsoft", "script", "command", "windows terminal", "automation", "cmdlet"],
                Topics: ["powershell", "windows scripting", "automation", "microsoft docs"],
                Notes: "Official Microsoft PowerShell documentation.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "python-docs",
                Topic: "software",
                Name: "Python Documentation",
                Url: "https://docs.python.org/3/",
                Type: "docs",
                TrustLevel: "official",
                Keywords: ["python", "standard library", "language reference", "tutorial", "docs", "package", "pip"],
                Topics: ["python", "programming", "standard library", "developer docs"],
                Notes: "Official Python 3 documentation.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "github-docs",
                Topic: "software",
                Name: "GitHub Docs",
                Url: "https://docs.github.com/",
                Type: "docs",
                TrustLevel: "official",
                Keywords: ["github", "git", "repository", "pull request", "actions", "issues", "codespaces", "github docs"],
                Topics: ["github", "git", "repositories", "pull requests", "github actions"],
                Notes: "Official GitHub product documentation.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "ollama-github",
                Topic: "software",
                Name: "Ollama GitHub Repository",
                Url: "https://github.com/ollama/ollama",
                Type: "docs",
                TrustLevel: "official",
                Keywords: ["ollama", "local model", "model runtime", "llm", "api", "openai compatible", "modelfile"],
                Topics: ["ollama", "local models", "model runtime", "llm setup"],
                Notes: "Official Ollama repository and project documentation.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "amd-driver-support",
                Topic: "hardware",
                Name: "AMD Drivers and Support",
                Url: "https://www.amd.com/en/support",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["amd", "radeon", "driver", "gpu", "graphics", "adrenalin", "chipset", "support"],
                Topics: ["amd", "gpu drivers", "radeon", "hardware support"],
                Notes: "Official AMD driver and hardware support page.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "nvidia-drivers",
                Topic: "hardware",
                Name: "NVIDIA Drivers",
                Url: "https://www.nvidia.com/en-us/drivers/",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["nvidia", "geforce", "rtx", "driver", "gpu", "graphics", "studio driver", "game ready"],
                Topics: ["nvidia", "gpu drivers", "geforce", "hardware support"],
                Notes: "Official NVIDIA driver download page.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "cisa-cybersecurity-advisories",
                Topic: "security",
                Name: "CISA Cybersecurity Advisories",
                Url: "https://www.cisa.gov/news-events/cybersecurity-advisories",
                Type: "web",
                TrustLevel: "official",
                Keywords: ["cisa", "cybersecurity", "advisory", "vulnerability", "patch", "security alert", "exploit"],
                Topics: ["cybersecurity", "vulnerabilities", "security advisories", "patching"],
                Notes: "Official CISA advisory source for cybersecurity alerts and vulnerability guidance.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "ncaa-football-scoreboard",
                Topic: "sports",
                Name: "NCAA FBS Football Scoreboard",
                Url: "https://www.ncaa.com/scoreboard/football/fbs",
                Type: "web",
                TrustLevel: "standard",
                Keywords: ["ncaa", "football", "fbs", "college football", "score", "schedule", "game"],
                Topics: ["sports", "college football", "scores", "schedule", "ncaa"],
                Notes: "NCAA scoreboard page for college football scores and schedules.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "espn-college-football-scoreboard",
                Topic: "sports",
                Name: "ESPN College Football Scoreboard",
                Url: "https://www.espn.com/college-football/scoreboard",
                Type: "web",
                TrustLevel: "standard",
                Keywords: ["espn", "college football", "football", "score", "scores", "schedule", "teams", "alabama", "tennessee"],
                Topics: ["sports", "college football", "scores", "schedule", "espn"],
                Notes: "Standard sports scoreboard source for college football scores and schedules.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "focusrite-scarlett-solo-4th-gen-downloads",
                Topic: "audio",
                Name: "Focusrite Scarlett Solo 4th Gen Downloads and User Guide",
                Url: "https://downloads.focusrite.com/focusrite/scarlett-4th-gen/scarlett-solo-4th-gen",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["focusrite", "scarlett", "solo", "4th gen", "audio interface", "gain", "air", "halo", "driver", "control 2", "microphone setup"],
                Topics: ["audio", "focusrite", "scarlett solo", "microphone setup", "audio interface"],
                Notes: "Official Focusrite downloads page for Scarlett Solo 4th Gen, including user guide and software links.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "focusrite-scarlett-2i2-4th-gen-downloads",
                Topic: "audio",
                Name: "Focusrite Scarlett 2i2 4th Gen Downloads and User Guide",
                Url: "https://downloads.focusrite.com/focusrite/scarlett-4th-gen/scarlett-2i2-4th-gen",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["focusrite", "scarlett", "2i2", "4th gen", "audio interface", "gain", "air", "halo", "driver", "control 2", "microphone setup"],
                Topics: ["audio", "focusrite", "scarlett 2i2", "microphone setup", "audio interface"],
                Notes: "Official Focusrite downloads page for Scarlett 2i2 4th Gen, including user guide and software links.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "audio-technica-at2040-product",
                Topic: "audio",
                Name: "Audio-Technica AT2040 Product Page",
                Url: "https://www.audio-technica.com/en-us/at2040",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["audio-technica", "at2040", "dynamic microphone", "hypercardioid", "podcast microphone", "xlr", "gain", "mic technique"],
                Topics: ["audio", "microphone", "at2040", "dynamic microphone", "mic technique"],
                Notes: "Official Audio-Technica product page for AT2040 microphone specifications and documents.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "tritonaudio-fethead-product",
                Topic: "audio",
                Name: "TritonAudio FetHead Product Page",
                Url: "https://tritonaudio.com/product/fethead/",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["tritonaudio", "fethead", "inline preamp", "microphone preamp", "phantom power", "dynamic microphone", "gain", "xlr"],
                Topics: ["audio", "fethead", "inline preamp", "phantom power", "microphone gain"],
                Notes: "Official TritonAudio FetHead product page for inline preamp behavior and power requirements.",
                Enabled: true),
            new SourceCatalogEntry(
                Id: "shure-gator-broadcast2-boom",
                Topic: "audio",
                Name: "Shure Gator Low Profile Boom Arm SH-BROADCAST2",
                Url: "https://www.shure.com/en-US/products/accessories/gator-broadcast2-boom?variant=SH-BROADCAST2",
                Type: "reference",
                TrustLevel: "official",
                Keywords: ["shure", "gator", "sh-broadcast2", "broadcast2", "low profile boom arm", "microphone arm", "desk clamp", "direct mount", "cable channel", "at2040"],
                Topics: ["audio", "boom arm", "microphone arm", "shure", "gator broadcast2"],
                Notes: "Official Shure product page for the Gator Low Profile Boom Arm, SKU SH-BROADCAST2.",
                Enabled: true)
        ];
    }

    public IReadOnlyList<SourceCatalogEntry> LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return Array.Empty<SourceCatalogEntry>();
        }

        using var stream = File.OpenRead(CatalogPath);
        return JsonSerializer.Deserialize<List<SourceCatalogEntry>>(stream, JsonOptions) ?? [];
    }

    public void SaveCatalog(IEnumerable<SourceCatalogEntry> sources)
    {
        Directory.CreateDirectory(RootDirectory);
        using var stream = File.Create(CatalogPath);
        JsonSerializer.Serialize(stream, sources.ToList(), JsonOptions);
    }

    public ISourceRetriever CreateRetriever() => new Retriever(this, _httpClient);

    private SourceSelection SelectSources(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return SourceSelection.Empty;
        }

        var allQueryTerms = Tokenize(userText, removeGenericTerms: false);
        var requiresSourceGrounding = allQueryTerms.Overlaps(SourceGroundingIntentTerms);
        if (!requiresSourceGrounding)
        {
            return SourceSelection.Empty;
        }

        var queryTerms = ExpandQueryTerms(Tokenize(userText, removeGenericTerms: true));
        if (queryTerms.Count == 0)
        {
            queryTerms = allQueryTerms;
            if (queryTerms.Count == 0)
            {
                return SourceSelection.Empty;
            }
        }

        var sportsIntent = queryTerms.Overlaps(SportsIntentTerms);
        var sources = LoadCatalog()
            .Where(source => source.Enabled && Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .Where(source => !sportsIntent || SourceContainsAny(source, SportsSourceTerms))
            .Select(source => new
            {
                Source = source,
                Score = ScoreSource(source, queryTerms)
            })
            .Where(item => item.Score >= MinimumSourceScore)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Source.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourcesPerRequest)
            .Select(item => item.Source)
            .ToList();
        return new SourceSelection(sources, requiresSourceGrounding);
    }

    private SourceSelection SelectSources(SourceQueryPlan plan)
    {
        if (!plan.UseSources)
        {
            return SourceSelection.Empty;
        }

        var queryTerms = BuildPlannedQueryTerms(plan);
        if (queryTerms.Count == 0)
        {
            return SourceSelection.Empty;
        }

        var preferredTopics = plan.PreferredSourceTopics
            .Select(NormalizeToken)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = LoadCatalog()
            .Where(source => source.Enabled && Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .Where(source => preferredTopics.Count == 0 || SourceMatchesPreferredTopics(source, preferredTopics))
            .Where(source => SourceIdentityMatchesPlannedQuery(source, plan, queryTerms))
            .Select(source => new
            {
                Source = source,
                Score = ScoreSource(source, queryTerms)
            })
            .Where(item => item.Score >= MinimumPlannedSourceScore)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Source.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourcesPerRequest)
            .Select(item => item.Source)
            .ToList();

        return new SourceSelection(sources, plan.RequiresSourceGrounding, LookupAttempted: true);
    }

    private static int ScoreSource(SourceCatalogEntry source, IReadOnlySet<string> queryTerms)
    {
        var score = 0;
        score += ScoreText(source.Topic, queryTerms) * 3;
        foreach (var topic in source.Topics ?? Array.Empty<string>())
        {
            score += ScoreText(topic, queryTerms) * 3;
        }

        score += ScoreText(source.Name, queryTerms) * 2;
        score += ScoreText(source.Notes ?? string.Empty, queryTerms);
        foreach (var keyword in source.Keywords ?? Array.Empty<string>())
        {
            score += ScoreText(keyword, queryTerms) * 4;
        }

        return score;
    }

    private static int ScoreText(string text, IReadOnlySet<string> queryTerms) =>
        Tokenize(text, removeGenericTerms: false).Count(queryTerms.Contains);

    private static bool SourceContainsAny(SourceCatalogEntry source, IReadOnlySet<string> requiredTerms)
    {
        var keywords = source.Keywords is null
            ? string.Empty
            : string.Join(' ', source.Keywords);
        var topics = source.Topics is null
            ? string.Empty
            : string.Join(' ', source.Topics);
        var sourceTerms = Tokenize($"{source.Topic} {topics} {source.Name} {source.Notes ?? string.Empty} {keywords}", removeGenericTerms: false);
        return sourceTerms.Overlaps(requiredTerms);
    }

    private static bool SourceIdentityMatchesPlannedQuery(
        SourceCatalogEntry source,
        SourceQueryPlan plan,
        IReadOnlySet<string> queryTerms)
    {
        if (!string.Equals(plan.Intent, "sports_score", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(NormalizeToken(source.Topic), "sports", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceSportsIdentities = CanonicalSportsIdentities(SourceTerms(source));
        if (sourceSportsIdentities.Count == 0)
        {
            return true;
        }

        var querySportsIdentities = CanonicalSportsIdentities(queryTerms);
        return querySportsIdentities.Count > 0 && sourceSportsIdentities.Overlaps(querySportsIdentities);
    }

    private static HashSet<string> SourceTerms(SourceCatalogEntry source)
    {
        var keywords = source.Keywords is null
            ? string.Empty
            : string.Join(' ', source.Keywords);
        var topics = source.Topics is null
            ? string.Empty
            : string.Join(' ', source.Topics);
        return Tokenize($"{source.Id} {source.Topic} {topics} {source.Name} {source.Notes ?? string.Empty} {keywords}", removeGenericTerms: false);
    }

    private static bool SourceMatchesPreferredTopics(SourceCatalogEntry source, IReadOnlySet<string> preferredTopics) =>
        SourceTopicTokens(source).Overlaps(preferredTopics);

    private static HashSet<string> SourceTopicTokens(SourceCatalogEntry source)
    {
        var topics = source.Topics is null
            ? Array.Empty<string>()
            : source.Topics;
        return new[] { source.Topic }
            .Concat(topics)
            .Select(NormalizeToken)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CanonicalSportsIdentities(IEnumerable<string> terms)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (!SportsIdentityTerms.Contains(term))
            {
                continue;
            }

            identities.Add(term switch
            {
                "crimson" or "rolltide" or "tide" => "alabama",
                "vols" or "volunteers" => "tennessee",
                _ => term
            });
        }

        return identities;
    }

    private static HashSet<string> Tokenize(string text, bool removeGenericTerms) =>
        text.Split([' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .Where(token => token.Length >= 3 || ImportantShortTerms.Contains(token))
            .Where(token => !removeGenericTerms || !GenericQueryTerms.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ExpandQueryTerms(HashSet<string> queryTerms)
    {
        if (queryTerms.Contains("tech"))
        {
            queryTerms.Add("technology");
            queryTerms.Add("software");
            queryTerms.Add("cybersecurity");
            queryTerms.Add("computing");
            queryTerms.Add("ai");
        }

        if (queryTerms.Contains("ai"))
        {
            queryTerms.Add("artificial");
            queryTerms.Add("intelligence");
            queryTerms.Add("llm");
            queryTerms.Add("machine");
            queryTerms.Add("learning");
            queryTerms.Add("model");
            queryTerms.Add("models");
            queryTerms.Add("openai");
        }

        if (queryTerms.Contains("score") || queryTerms.Contains("scores") || queryTerms.Contains("game"))
        {
            queryTerms.Add("sports");
            queryTerms.Add("football");
            queryTerms.Add("teams");
            queryTerms.Add("schedule");
            queryTerms.Add("ncaa");
            queryTerms.Add("espn");
        }

        if (queryTerms.Contains("alabama") && (queryTerms.Contains("football") || queryTerms.Contains("sports")))
        {
            queryTerms.Add("college");
            queryTerms.Add("sec");
            queryTerms.Add("ncaa");
        }

        return queryTerms;
    }

    private static HashSet<string> BuildPlannedQueryTerms(SourceQueryPlan plan)
    {
        var text = string.Join(
            ' ',
            new[] { plan.Intent, plan.Topic }
                .Concat(plan.QueryTerms)
                .Concat(plan.PreferredSourceTopics)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        return ExpandQueryTerms(Tokenize(text, removeGenericTerms: false));
    }

    private static string NormalizeToken(string token) =>
        new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string CleanHtml(string html)
    {
        var decoded = WebUtility.HtmlDecode(html);
        decoded = Regex.Replace(decoded, @"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        decoded = Regex.Replace(decoded, @"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        decoded = TagRegex.Replace(decoded, " ");
        decoded = WhitespaceRegex.Replace(decoded, " ").Trim();
        decoded = TrimToUsefulPageBody(decoded);
        return decoded.Length <= MaxExcerptCharacters ? decoded : decoded[..MaxExcerptCharacters];
    }

    private static string TrimToUsefulPageBody(string text)
    {
        var anchors = new[]
        {
            "Next Game Information",
            "Football Schedule",
            "Overall Wins",
            "The Administration",
            "President Donald J. Trump",
            "President of the United States",
            "National Weather Service local forecast:"
        };

        foreach (var anchor in anchors)
        {
            var index = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return text[index..].Trim();
            }
        }

        return text;
    }

    private sealed class Retriever(FileSourceRetriever owner, HttpClient httpClient) : ISourceRetriever
    {
        public Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken) =>
            RetrieveAsync(owner.SelectSources(plan), cancellationToken);

        public async Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken)
        {
            return await RetrieveAsync(owner.SelectSources(userText), cancellationToken).ConfigureAwait(false);
        }

        private async Task<SourceRetrievalResult> RetrieveAsync(SourceSelection selected, CancellationToken cancellationToken)
        {
            if (selected.Sources.Count == 0)
            {
                return selected.LookupAttempted
                    ? new SourceRetrievalResult(
                        Array.Empty<SourceExcerpt>(),
                        ["No matching approved sources were selected for the planned query."],
                        selected.RequiresSourceGrounding)
                    : SourceRetrievalResult.Empty;
            }

            var excerpts = new List<SourceExcerpt>();
            var warnings = new List<string>();
            var index = 1;

            foreach (var source in selected.Sources)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(RequestTimeout);
                    excerpts.Add(new SourceExcerpt(
                        index++,
                        source.Topic,
                        source.Name,
                        source.Url,
                        DateTimeOffset.UtcNow,
                        await RetrieveExcerptAsync(source, timeout.Token).ConfigureAwait(false)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
                {
                    warnings.Add($"{source.Name} could not be retrieved: {ex.Message}");
                }
            }

            return new SourceRetrievalResult(excerpts, warnings, selected.RequiresSourceGrounding);
        }

        private async Task<string> RetrieveExcerptAsync(SourceCatalogEntry source, CancellationToken cancellationToken)
        {
            using var response = await httpClient.GetAsync(source.Url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{source.Name} returned HTTP {(int)response.StatusCode}.");
            }

            return string.Equals(source.Type, NwsPointForecastType, StringComparison.OrdinalIgnoreCase)
                ? await RetrieveNwsPointForecastAsync(body, cancellationToken).ConfigureAwait(false)
                : CleanHtml(body);
        }

        private async Task<string> RetrieveNwsPointForecastAsync(string pointJson, CancellationToken cancellationToken)
        {
            using var pointDocument = JsonDocument.Parse(pointJson);
            if (!pointDocument.RootElement.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("forecast", out var forecastElement)
                || forecastElement.ValueKind is not JsonValueKind.String
                || string.IsNullOrWhiteSpace(forecastElement.GetString()))
            {
                throw new InvalidOperationException("NWS point response did not include a forecast URL.");
            }

            using var forecastResponse = await httpClient.GetAsync(forecastElement.GetString(), cancellationToken).ConfigureAwait(false);
            var forecastBody = await forecastResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!forecastResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"NWS forecast returned HTTP {(int)forecastResponse.StatusCode}.");
            }

            using var forecastDocument = JsonDocument.Parse(forecastBody);
            if (!forecastDocument.RootElement.TryGetProperty("properties", out var forecastProperties)
                || !forecastProperties.TryGetProperty("periods", out var periods)
                || periods.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException("NWS forecast response did not include periods.");
            }

            var lines = new StringBuilder("National Weather Service local forecast:");
            foreach (var period in periods.EnumerateArray().Take(5))
            {
                var name = ReadJsonString(period, "name");
                var shortForecast = ReadJsonString(period, "shortForecast");
                var detailedForecast = ReadJsonString(period, "detailedForecast");
                var temperature = period.TryGetProperty("temperature", out var tempElement)
                                  && tempElement.ValueKind is JsonValueKind.Number
                    ? tempElement.GetInt32().ToString()
                    : string.Empty;
                var unit = ReadJsonString(period, "temperatureUnit");
                var windSpeed = ReadJsonString(period, "windSpeed");
                var windDirection = ReadJsonString(period, "windDirection");
                lines.AppendLine();
                lines.Append($"{name}: {temperature}{unit}. {shortForecast}. ");
                if (!string.IsNullOrWhiteSpace(windSpeed) || !string.IsNullOrWhiteSpace(windDirection))
                {
                    lines.Append($"Wind {windDirection} {windSpeed}. ");
                }

                lines.Append(detailedForecast);
            }

            var text = lines.ToString();
            return text.Length <= MaxExcerptCharacters ? text : text[..MaxExcerptCharacters];
        }

        private static string ReadJsonString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private sealed record SourceSelection(
        IReadOnlyList<SourceCatalogEntry> Sources,
        bool RequiresSourceGrounding,
        bool LookupAttempted = false)
    {
        public static SourceSelection Empty { get; } = new(Array.Empty<SourceCatalogEntry>(), false);
    }
}
