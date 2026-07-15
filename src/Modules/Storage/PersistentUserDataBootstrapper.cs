using Ali.Modules.Identity;
using Ali.Modules.Runtime;
using Ali.Modules.Internet;
using Ali.Modules.RAG;
using Ali.Modules.Voice;

namespace Ali.Modules.Storage;

public static class PersistentUserDataBootstrapper
{
    public static void EnsureCreated(
        string dataRoot,
        string profileDataRoot,
        AssistantProfile assistantProfile,
        FileConversationStore conversations,
        FileMemoryStore memories,
        FileReminderStore reminders,
        FileCorrectionQueueStore corrections)
    {
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(profileDataRoot);

        if (!AssistantProfileStore.Exists(dataRoot))
        {
            AssistantProfileStore.Save(dataRoot, assistantProfile);
        }

        RuntimeSettingsStore.WriteDefaultIfMissing(dataRoot);
        RuntimeSettingsStore.WriteExample(dataRoot);
        VoiceRuntimeSettingsStore.WriteDefaultIfMissing(dataRoot);
        WebSourceBackendSettingsStore.WriteDefaultIfMissing(dataRoot);
        WebSourceBackendSettingsStore.WriteExample(dataRoot);
        LocalVectorLibrarySettingsStore.WriteExample(dataRoot);
        LocalVectorLibrarySettingsStore.MoveLegacyDefaultRootIfNeeded(dataRoot);

        conversations.ListSummaries();
        memories.EnsureCreated();
        reminders.EnsureCreated();
        corrections.EnsureCreated();
    }
}
