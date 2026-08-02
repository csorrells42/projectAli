using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Ali.Modules.WorkstationFiles;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Captures the exact Agent Framework-owned tool declarations through its supported
/// provider pipeline. The probe uses the production stores and provider options but
/// a local terminal chat client, so it cannot contact a model or invoke a tool.
/// </summary>
internal static class AliFrameworkCapabilityProbe
{
    public static IReadOnlyList<AIFunctionDeclaration> Capture(
        AliWorkstationFileAccess fileAccess,
        Func<CoordinatorTurnContext?> turnAccessor)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        ArgumentNullException.ThrowIfNull(turnAccessor);

        return Task.Run(async () =>
        {
            var client = new CapturingChatClient();
            var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
            var agent = client.AsHarnessAgent(new HarnessAgentOptions
            {
                MaximumIterationsPerRequest = 1,
                DisableWebSearch = true,
                DisableFileMemory = false,
                DisableAgentSkillsProvider = false,
                AgentSkillsSource = new AgentFileSkillsSource(skillsRoot),
                DisableOpenTelemetry = true,
                DisableTodoProvider = true,
                // The provider needs a store to compose its schemas. A non-persistent
                // empty store avoids touching or auditing user work memory at startup.
                FileMemoryStore = new CapabilityProbeFileStore(),
                FileAccessStore = fileAccess.Store,
                FileAccessProviderOptions = new FileAccessProviderOptions
                {
                    Instructions = fileAccess.Instructions,
                    DisableWriteTools = false,
                    DisableReadOnlyToolApproval = false,
                    DisableWriteToolApproval = false
                },
                ToolApprovalAgentOptions = new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = [fileAccess.ShouldAutoApproveAsync]
                },
                ChatOptions = new ChatOptions
                {
                    Instructions = "Capture the exact framework capability schemas.",
                    Tools = [],
                    ToolMode = ChatToolMode.Auto,
                    AllowMultipleToolCalls = false
                }
            });

            var session = await agent.CreateSessionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _ = await agent.RunAsync(
                    "Return without calling a tool.",
                    session,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return client.CapturedTools;
        }).GetAwaiter().GetResult();
    }

    private sealed class CapabilityProbeFileStore : AgentFileStore
    {
        private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

        public override Task WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files[path] = content;
            return Task.CompletedTask;
        }

        public override Task<string?> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_files.TryGetValue(path, out var content) ? content : null);
        }

        public override Task<bool> DeleteAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_files.TryRemove(path, out _));
        }

        public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
            string directory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileStoreEntry>>([]);

        public override Task<bool> FileExistsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_files.ContainsKey(path));
        }

        public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string directory,
            string regexPattern,
            string? globPattern,
            bool recursive,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileSearchResult>>([]);

        public override Task CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<AIFunctionDeclaration> CapturedTools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Capture(options);
            return Task.FromResult(StopResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Capture(options);
            foreach (var update in StopResponse().ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private void Capture(ChatOptions? options)
        {
            CapturedTools = (options?.Tools ?? [])
                .OfType<AIFunctionDeclaration>()
                .ToArray();
        }

        private static ChatResponse StopResponse() =>
            new(new ChatMessage(ChatRole.Assistant, "Framework capability schemas captured."))
            {
                FinishReason = ChatFinishReason.Stop
            };
    }
}
