using System.Runtime.CompilerServices;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coordinator;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class AgentWorkMemoryTests
{
    [Fact]
    public void CapabilityCatalog_MatchesEveryFrameworkFileMemoryToolName()
    {
        var expected = new[]
        {
            FileMemoryProvider.WriteToolName,
            FileMemoryProvider.ReadFileToolName,
            FileMemoryProvider.DeleteFileToolName,
            FileMemoryProvider.LsToolName,
            FileMemoryProvider.GrepToolName,
            FileMemoryProvider.ReplaceToolName,
            FileMemoryProvider.ReplaceLinesToolName
        };
        var catalog = AliCapabilityCatalog.Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expected, name => Assert.Contains(name, catalog.Keys));
        Assert.All(expected, name => Assert.Equal("Microsoft Agent Framework file memory", catalog[name].Source));
        Assert.Equal(FileMemoryProvider.WriteToolName, AliCapabilityCatalog.WorkMemoryWriteName);
        Assert.Equal(FileMemoryProvider.ReadFileToolName, AliCapabilityCatalog.WorkMemoryReadName);
        Assert.Equal(FileMemoryProvider.DeleteFileToolName, AliCapabilityCatalog.WorkMemoryDeleteName);
        Assert.Equal(FileMemoryProvider.LsToolName, AliCapabilityCatalog.WorkMemoryListName);
        Assert.Equal(FileMemoryProvider.GrepToolName, AliCapabilityCatalog.WorkMemorySearchName);
        Assert.Equal(FileMemoryProvider.ReplaceToolName, AliCapabilityCatalog.WorkMemoryReplaceName);
        Assert.Equal(FileMemoryProvider.ReplaceLinesToolName, AliCapabilityCatalog.WorkMemoryReplaceLinesName);
    }

    [Fact]
    public async Task Store_PersistsWithinConversationAndIsolatesUsersAndConversations()
    {
        await WithMemoryAsync(async memory =>
        {
            var alice = User("alice", "Alice");
            var bob = User("bob", "Bob");
            using (memory.EnterScope("conversation-one", alice))
            {
                await memory.Store.WriteAsync("research.md", "Alice private work", TestContext.Current.CancellationToken);
            }

            using (memory.EnterScope("conversation-two", alice))
            {
                Assert.Null(await memory.Store.ReadAsync("research.md", TestContext.Current.CancellationToken));
            }

            using (memory.EnterScope("conversation-one", bob))
            {
                Assert.Null(await memory.Store.ReadAsync("research.md", TestContext.Current.CancellationToken));
            }

            using (memory.EnterScope("conversation-one", alice))
            {
                Assert.Equal(
                    "Alice private work",
                    await memory.Store.ReadAsync("research.md", TestContext.Current.CancellationToken));
            }

            Assert.NotEqual(
                memory.GetWorkspacePath(alice.StableId, "conversation-one"),
                memory.GetWorkspacePath(bob.StableId, "conversation-one"));
            Assert.NotEqual(
                memory.GetWorkspacePath(alice.StableId, "conversation-one"),
                memory.GetWorkspacePath(alice.StableId, "conversation-two"));
        });
    }

    [Fact]
    public async Task Store_FailsClosedOutsideConversationScope()
    {
        await WithMemoryAsync(async memory =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                memory.Store.ReadAsync("notes.md", TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task Store_DeletesRecoverablyAndAuditsMetadataWithoutContent()
    {
        const string secret = "work-memory-content-must-never-enter-the-audit";
        await WithMemoryAsync(async memory =>
        {
            using (memory.EnterScope("conversation", User("alice", "Alice")))
            {
                await memory.Store.WriteAsync("notes.md", secret, TestContext.Current.CancellationToken);
                Assert.True(await memory.Store.DeleteAsync("notes.md", TestContext.Current.CancellationToken));
                Assert.Null(await memory.Store.ReadAsync("notes.md", TestContext.Current.CancellationToken));
            }

            Assert.Single(Directory.EnumerateFiles(memory.RecoverableTrashPath, "notes.md", SearchOption.AllDirectories));
            var audit = await File.ReadAllTextAsync(memory.AuditPath, TestContext.Current.CancellationToken);
            Assert.Contains("conversation", audit, StringComparison.Ordinal);
            Assert.Contains("alice", audit, StringComparison.Ordinal);
            Assert.Contains("notes.md", audit, StringComparison.Ordinal);
            Assert.Contains("delete", audit, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Harness_ExecutesAllSevenFrameworkFileMemoryToolsAgainstAliStore()
    {
        await WithMemoryAsync(async memory =>
        {
            using var client = new ScriptedChatClient(
            [
                ToolCall(FileMemoryProvider.WriteToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "notes.md",
                    ["content"] = "alpha\nbeta\n",
                    ["description"] = "Two-line integration test note"
                }),
                ToolCall(FileMemoryProvider.ReadFileToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "notes.md"
                }),
                ToolCall(FileMemoryProvider.GrepToolName, new Dictionary<string, object?>
                {
                    ["regexPattern"] = "beta",
                    ["globPattern"] = "*.md"
                }),
                ToolCall(FileMemoryProvider.ReplaceToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "notes.md",
                    ["oldString"] = "beta",
                    ["newString"] = "gamma",
                    ["replaceAll"] = false
                }),
                ToolCall(FileMemoryProvider.ReplaceLinesToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "notes.md",
                    ["edits"] = new List<FileLineEdit>
                    {
                        new() { LineNumber = 1, NewLine = "delta\n" }
                    }
                }),
                ToolCall(FileMemoryProvider.LsToolName, new Dictionary<string, object?>
                {
                    ["globPattern"] = "*.md"
                }),
                ToolCall(FileMemoryProvider.DeleteFileToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "notes.md"
                }),
                FinalAnswer("all file-memory tools completed")
            ]);
            var agent = client.AsHarnessAgent(new HarnessAgentOptions
            {
                MaximumIterationsPerRequest = 12,
                DisableWebSearch = true,
                DisableFileMemory = false,
                FileMemoryStore = memory.Store,
                DisableAgentSkillsProvider = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true
            });

            using (memory.EnterScope("framework-integration", User("alice", "Alice")))
            {
                var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
                var response = await agent.RunAsync(
                    "Exercise the private work-memory tools.",
                    session,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Contains("completed", response.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Null(await memory.Store.ReadAsync("notes.md", TestContext.Current.CancellationToken));
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                FileMemoryProvider.WriteToolName,
                FileMemoryProvider.ReadFileToolName,
                FileMemoryProvider.DeleteFileToolName,
                FileMemoryProvider.LsToolName,
                FileMemoryProvider.GrepToolName,
                FileMemoryProvider.ReplaceToolName,
                FileMemoryProvider.ReplaceLinesToolName
            };
            Assert.True(expected.IsSubsetOf(client.ObservedToolNames));
            Assert.Equal(8, client.CallCount);
            Assert.True(Directory.EnumerateFiles(memory.RecoverableTrashPath, "notes.md", SearchOption.AllDirectories).Any());
        });
    }

    [Fact]
    public async Task ProductionScopedProvider_DoesNotMutateBeforeANoToolTurn()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliAgentWorkMemoryProductionProviderTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new AliAgentWorkMemory(
                Path.Combine(root, "UserData"),
                Path.Combine(root, "OrchestrationV2"),
                "agent-work-memory-production-provider-test");
            using var client = new ScriptedChatClient([FinalAnswer("BRIDGE_HELLO_OK")]);
            using var provider = memory.CreateFrameworkProvider();
            var agent = client.AsHarnessAgent(new HarnessAgentOptions
            {
                MaximumIterationsPerRequest = 2,
                DisableWebSearch = true,
                DisableFileMemory = true,
                AIContextProviders = [provider],
                DisableAgentSkillsProvider = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableOpenTelemetry = true
            });

            using (memory.EnterScope("bridge-no-tool", User("alice", "Alice")))
            {
                var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
                var response = await agent.RunAsync(
                    "Reply without calling a tool.",
                    session,
                    cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal("BRIDGE_HELLO_OK", response.Text);
            }

            if (File.Exists(memory.AuditPath))
            {
                var audit = await File.ReadAllTextAsync(
                    memory.AuditPath,
                    TestContext.Current.CancellationToken);
                Assert.DoesNotContain("\"operation\":\"create-directory\"", audit, StringComparison.Ordinal);
                Assert.DoesNotContain("\"operation\":\"write\"", audit, StringComparison.Ordinal);
                Assert.DoesNotContain("\"operation\":\"delete\"", audit, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ChatResponse ToolCall(string name, IDictionary<string, object?> arguments)
    {
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent($"call-{Guid.NewGuid():N}", name, arguments));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse FinalAnswer(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    private static ActiveUser User(string id, string name) => new(id, name, false, "test");

    private static async Task WithMemoryAsync(Func<AliAgentWorkMemory, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliAgentWorkMemoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            await action(new AliAgentWorkMemory(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ScriptedChatClient(IEnumerable<ChatResponse> responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public HashSet<string> ObservedToolNames { get; } = new(StringComparer.Ordinal);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            foreach (var tool in options?.Tools?.OfType<AIFunctionDeclaration>() ?? [])
            {
                ObservedToolNames.Add(tool.Name);
            }
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : FinalAnswer("script exhausted"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
