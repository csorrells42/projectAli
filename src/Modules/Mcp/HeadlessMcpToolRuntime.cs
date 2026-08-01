using Ali.Modules.Calendar;
using Ali.Modules.Coding;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Permissions;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.Storage;
using Ali.Modules.UserMemory;
using Ali.Modules.WorkstationFiles;
using ModelContextProtocol.Server;

namespace Ali.Modules.Mcp;

/// <summary>
/// Creates only the services required by Ali's exported MCP tools. It deliberately
/// omits the desktop UI, camera, voice, model coordinator, and agent runtime.
/// </summary>
public sealed class HeadlessMcpToolRuntime : IAsyncDisposable
{
    private readonly HttpClient _runtimeHttpClient;
    private readonly HttpClient _internetHttpClient;
    private readonly QdrantServiceManager _qdrant;
    private readonly Mem0UserMemoryService _userMemories;
    private readonly AliCodingModule _codingModule;
    private bool _disposed;

    private HeadlessMcpToolRuntime(
        IReadOnlyList<McpServerTool> tools,
        HttpClient runtimeHttpClient,
        HttpClient internetHttpClient,
        QdrantServiceManager qdrant,
        Mem0UserMemoryService userMemories,
        AliCodingModule codingModule)
    {
        Tools = tools;
        _runtimeHttpClient = runtimeHttpClient;
        _internetHttpClient = internetHttpClient;
        _qdrant = qdrant;
        _userMemories = userMemories;
        _codingModule = codingModule;
    }

    public IReadOnlyList<McpServerTool> Tools { get; }

    public static HeadlessMcpToolRuntime Create(
        string dataRoot,
        string applicationBaseDirectory,
        McpServerSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        dataRoot = Path.GetFullPath(dataRoot);
        var userDataRoot = Path.Combine(dataRoot, "Data");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "Settings"));
        Directory.CreateDirectory(userDataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "Backups"));
        Directory.CreateDirectory(Path.Combine(userDataRoot, "Logs"));

        var profile = AssistantProfileStore.LoadOrDefault(dataRoot).Normalize();
        var profileDataRoot = Path.Combine(userDataRoot, "Profiles", profile.ProfileId);
        var correctionStore = new FileCorrectionQueueStore(profileDataRoot);
        var conversations = new FileConversationStore(profileDataRoot);
        var memories = new FileMemoryStore(profileDataRoot);
        var reminders = new FileReminderStore(
            profileDataRoot,
            new WindowsCalendarEventPublisher(profileDataRoot));
        PersistentUserDataBootstrapper.EnsureCreated(
            dataRoot,
            profileDataRoot,
            profile,
            conversations,
            memories,
            reminders,
            correctionStore);

        var runtimeHttpClient = new HttpClient();
        runtimeHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AliMcpHost/1.0");
        var internetHttpClient = InternetHttpClientFactory.CreateClient();
        var qdrant = new QdrantServiceManager(dataRoot);
        var activeUsers = new ActiveUserSession(
            dataRoot,
            Path.Combine(userDataRoot, "Vision"));
        var mem0Client = new Mem0ProcessClient(
            dataRoot,
            qdrant,
            () => LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot),
            () => UserMemorySettingsStore.LoadOrDefault(dataRoot),
            () => RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot));
        var userMemories = new Mem0UserMemoryService(
            mem0Client,
            () => UserMemorySettingsStore.LoadOrDefault(dataRoot));
        var toolPermissions = new AgentToolPermissionStore(dataRoot);
        var fileAccess = AliWorkstationFileAccess.CreateDefault(
            userDataRoot,
            profileDataRoot,
            toolPermissions,
            activeUsers);
        var codingModule = new AliCodingModule(
            fileAccess,
            () => AgentOrchestrationSettingsStore.LoadOrDefault(dataRoot),
            () => RuntimeSettingsStore.LoadOrDefault(dataRoot),
            applicationBaseDirectory);
        var localLibrary = new LocalVectorLibraryRetriever(
            dataRoot,
            runtimeHttpClient,
            qdrant: qdrant);
        var webSources = new TavilyFirecrawlSourceRetriever(
            internetHttpClient,
            () => WebSourceBackendSettingsStore.LoadOrDefault(dataRoot),
            dataRoot: dataRoot);
        var webResearch = new McpWebResearchClient(
            () => WebSourceBackendSettingsStore.LoadOrDefault(dataRoot));
        var toolFactory = new AliMcpServerToolFactory(
            localLibrary,
            webSources,
            webResearch,
            memories,
            reminders,
            profile,
            userMemories,
            activeUsers,
            () => UserMemorySettingsStore.LoadOrDefault(dataRoot),
            codingModule);

        return new HeadlessMcpToolRuntime(
            toolFactory.CreateTools(settings),
            runtimeHttpClient,
            internetHttpClient,
            qdrant,
            userMemories,
            codingModule);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _codingModule.DisposeAsync().ConfigureAwait(false);
        await _userMemories.DisposeAsync().ConfigureAwait(false);
        await _qdrant.DisposeAsync().ConfigureAwait(false);
        _internetHttpClient.Dispose();
        _runtimeHttpClient.Dispose();
    }
}
