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
    private readonly AliCodingModule _codingModule;
    private bool _disposed;

    private HeadlessMcpToolRuntime(
        IReadOnlyList<McpServerTool> tools,
        HttpClient runtimeHttpClient,
        HttpClient internetHttpClient,
        QdrantServiceManager qdrant,
        AliCodingModule codingModule)
    {
        Tools = tools;
        _runtimeHttpClient = runtimeHttpClient;
        _internetHttpClient = internetHttpClient;
        _qdrant = qdrant;
        _codingModule = codingModule;
    }

    public IReadOnlyList<McpServerTool> Tools { get; }

    public static HeadlessMcpToolRuntime Create(
        string dataRoot,
        string applicationBaseDirectory,
        McpServerSettings settings,
        string? workspaceRoot = null,
        string? hostConfigurationPath = null)
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

        var runtimeHttpClient = LocalOnlyHttpClientFactory.Create("AliMcpHost/1.0");
        var internetHttpClient = InternetHttpClientFactory.CreateClient();
        var internetSettings = new WebSourceBackendSettingsSnapshotOwner(dataRoot);
        var qdrant = new QdrantServiceManager(dataRoot);
        var activeUsers = new ActiveUserSession(
            dataRoot,
            Path.Combine(userDataRoot, "Vision"));
        var toolPermissions = new AgentToolPermissionStore(dataRoot);
        var fileAccess = CreateFileAccess(
            userDataRoot,
            profileDataRoot,
            toolPermissions,
            activeUsers,
            workspaceRoot,
            profile.AssistantName);
        var codingModule = new AliCodingModule(fileAccess);
        var localLibrary = new LocalVectorLibraryRetriever(
            dataRoot,
            runtimeHttpClient,
            qdrant: qdrant);
        var webSources = new TavilyFirecrawlSourceRetriever(
            internetHttpClient,
            () => internetSettings.Capture().Settings,
            dataRoot: dataRoot);
        var webResearch = new McpWebResearchClient(
            () => internetSettings.Capture().Settings);
        var toolFactory = new AliMcpServerToolFactory(
            localLibrary,
            webSources,
            webResearch,
            memories,
            reminders,
            profile,
            codingModule,
            fileAccess,
            () => internetSettings.Capture().Settings);
        var capabilitySettings = toolFactory.CreateCapabilitySettingsOwner(dataRoot, settings);
        Func<string> invocationBoundaryRevisionAccessor = () =>
            McpPersistedSecurityBoundaryRevision.Capture(dataRoot, hostConfigurationPath);
        var publication = toolFactory.CreateTools(
            settings,
            capabilitySettings,
            invocationBoundaryRevisionAccessor: invocationBoundaryRevisionAccessor);

        return new HeadlessMcpToolRuntime(
            publication.Tools,
            runtimeHttpClient,
            internetHttpClient,
            qdrant,
            codingModule);
    }

    internal static AliWorkstationFileAccess CreateFileAccess(
        string userDataRoot,
        string profileDataRoot,
        AgentToolPermissionStore permissions,
        IActiveUserSession? activeUsers,
        string? workspaceRoot,
        string? assistantProfileBinding = null) =>
        AliWorkstationFileAccess.CreateDefault(
            userDataRoot,
            profileDataRoot,
            permissions,
            activeUsers,
            durableOrchestrationRoot: Path.Combine(userDataRoot, "OrchestrationV2"),
            assistantProfileBinding: assistantProfileBinding,
            workspaceRootOverride: workspaceRoot);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _codingModule.DisposeAsync().ConfigureAwait(false);
        await _qdrant.DisposeAsync().ConfigureAwait(false);
        _internetHttpClient.Dispose();
        _runtimeHttpClient.Dispose();
    }
}
