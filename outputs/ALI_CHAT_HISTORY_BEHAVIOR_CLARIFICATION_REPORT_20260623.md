# Ali Chat History Behavior Clarification Report

Date: 2026-06-23

Code commit: `87e99c8 Clarify ChatGPT-like conversation behavior`

## Scope

This pass finished the chat-history behavior clarification only. No voice certification, runtime feature expansion, search redesign, or new UI feature work was performed.

## Implemented

- App launch now starts a fresh empty chat session when no saved conversation is explicitly selected.
- The startup bootstrap assistant message was removed from the live chat surface.
- New Chat creates a new unsaved conversation id and does not overwrite old saved chats.
- Selecting a saved chat from Recents reopens that exact conversation.
- Reopened conversations restore messages in created-time order.
- Continuing a reopened chat saves back to the same conversation id.
- Search results can be opened into the correct saved chat.
- Searching does not mutate conversation storage.
- Active saved chat highlighting was added in the left sidebar.
- Search filtering no longer clears the logical active chat when the current chat is not visible in filtered results.

## Files Changed

- `src/Ali.Core/Conversations/ConversationSessionFactory.cs`
- `src/Ali.App.Wpf/MainWindow.xaml`
- `src/Ali.App.Wpf/ViewModels/ConversationHistoryItemViewModel.cs`
- `src/Ali.App.Wpf/ViewModels/MainWindowViewModel.cs`
- `tests/Ali.Tests/Program.cs`

## Conversation Behavior

Startup behavior:

- Ali opens to a fresh empty chat.
- Saved chats remain available in the sidebar.
- Old conversations are not auto-opened unless the user explicitly selects one.

New Chat behavior:

- Clicking New Chat resets composer, attachments, temporary transcript fields, and visible messages.
- The new chat gets a fresh conversation id.
- Existing saved conversations remain untouched.

Saved chat behavior:

- Selecting a recent chat loads that chat's saved messages.
- Continuing the loaded chat appends to that same saved conversation.
- Deleting the active saved chat returns Ali to a fresh chat.

Search behavior:

- Search remains local to saved conversation titles/messages.
- Search result selection opens the matching conversation.
- Search itself does not edit, delete, rename, or reorder stored conversations.

Erase wording:

- Erase History is still scoped to saved conversations and recent chat entries.
- It does not delete local models, settings, voice resources, correction reports, memories, reminders, or the app.

## Validation

Build:

- Command: `dotnet build .\Ali.sln`
- Result: PASS
- Warnings: 0
- Errors: 0

Tests:

- Command: `dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-build`
- Result: PASS
- Count: 70/70

WPF launch check:

- Executable started successfully.
- Process stayed alive for 5 seconds.
- Test process was then stopped intentionally.

NuGet lock note:

- Sandboxed full build still reported the NuGet temp lock path.
- The exact lock file was not present outside the sandbox.
- Running the full build with normal NuGet temp/cache access passed cleanly.
- Conclusion: this was a sandbox access issue, not a code failure.

## Owner Review Status

Ready for Chris owner visual review.

Do not call the chat cockpit fully accepted until Chris inspects the actual running UI.

Remaining owner-review items:

- Overall dark chat-first feel.
- Sidebar spacing/readability.
- Conversation area readability.
- Bottom composer usability.
- Attach, mic, voice, stop, and send button clarity.
- Runtime/settings area not being noisy.
- New Chat and Recents behavior.
- Search behavior wording and expectations.
- Erase History wording.

## Known Limitations

- Live mic/voice certification is still not complete.
- Piper playback and Stop Speaking still require the next live owner-assisted certification gate.
- Search is intentionally basic local search.
- No cloud services were added.

## Verdict

Chat-history behavior is now code-backed, tested, and ready for owner inspection.
