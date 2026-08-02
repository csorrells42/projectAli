using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using Ali.Modules.WorkstationFiles;
using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class AgentToolPermissionsViewModelTests
{
    [Fact]
    public void PermissionRows_OmitEveryRetiredSingleLoopTool()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ProjectAli.AgentToolPermissionsViewModelTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "Workspace")).FullName;
            var user = new ActiveUser("user-a", "Alice", false, "test");
            var activeUsers = new FixedActiveUserSession(user);
            var store = new AgentToolPermissionStore(root);
            store.SetProfile(AgentPermissionProfile.LockedDown);
            foreach (var retiredToolName in RetiredSingleLoopSurfaceCanary.ToolNames)
            {
                store.Save(
                    user,
                    retiredToolName,
                    AgentToolPermissionScope.Tool,
                    arguments: null);
            }
            store.Save(
                user,
                AliCapabilityCatalog.FileWriteName,
                AgentToolPermissionScope.Tool,
                arguments: null);
            var fileStore = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(root, "RecoverableTrash"));
            var fileAccess = new AliWorkstationFileAccess(
                fileStore,
                new AgentFileActionAuditStore(root, activeUsers),
                store);

            var viewModel = new AgentToolPermissionsViewModel(
                store,
                activeUsers,
                fileAccess,
                new AliAgentWorkMemory(root));

            var persistedNames = store.ListForUser(user.StableId)
                .Select(grant => grant.ToolName)
                .ToArray();
            Assert.All(
                RetiredSingleLoopSurfaceCanary.ToolNames,
                toolName => Assert.Contains(toolName, persistedNames));
            Assert.Contains(
                viewModel.Grants,
                grant => grant.RawToolName == AliCapabilityCatalog.FileWriteName);
            Assert.Contains(
                viewModel.ProtectedTools,
                policy => policy.RawToolName == AliCapabilityCatalog.FileWriteName);
            Assert.DoesNotContain(
                viewModel.Grants,
                grant => RetiredSingleLoopSurfaceCanary.ToolNames.Contains(grant.RawToolName));
            Assert.DoesNotContain(
                viewModel.ProtectedTools,
                policy => RetiredSingleLoopSurfaceCanary.ToolNames.Contains(policy.RawToolName));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FixedActiveUserSession(ActiveUser user) : IActiveUserSession
    {
        public ActiveUser Current { get; } = user;

        public IReadOnlyList<ActiveUser> AvailableUsers => [Current];

        public bool RequiresSelection => false;

        public event EventHandler<ActiveUser>? Changed
        {
            add { }
            remove { }
        }

        public ActiveUser Select(string stableId) => Current;

        public void Refresh()
        {
        }
    }
}
