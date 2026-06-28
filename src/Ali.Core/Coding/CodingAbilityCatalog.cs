using System.Text;

namespace Ali.Core.Coding;

public sealed record CodingAbilityCommand(
    string Label,
    string Command,
    bool RequiresConfirmation = false);

public sealed record CodingAbilityGroup(
    string Name,
    string Summary,
    IReadOnlyList<CodingAbilityCommand> Commands);

public sealed record UserCommandHelpEntry(
    string Title,
    string Summary,
    string Usage,
    string? Command = null);

public sealed record UserCommandHelpTopic(
    string Name,
    string Summary,
    IReadOnlyList<UserCommandHelpEntry> Entries);

public static class CodingAbilityCatalog
{
    public static IReadOnlyList<string> FastBuilderPath { get; } =
    [
        "interpret build goal <goal>",
        "show architecture options <goal>",
        "write acceptance criteria <goal>",
        "suggest tests for <goal>",
        "draft implementation roadmap <goal>",
        "approve last roadmap",
        "start approved roadmap",
        "show next coding action",
        "show execution packet",
        "validation plan"
    ];

    public static IReadOnlyList<CodingAbilityGroup> BuilderGroups { get; } =
    [
        new(
            "Scout and choose",
            "Understand the workspace and compare build paths before editing.",
            [
                new("Interpret goal", "interpret build goal <goal>"),
                new("Explore idea", "explore build idea <goal>"),
                new("Architecture options", "show architecture options <goal>"),
                new("Package lookup plan", "plan package lookup <goal>"),
                new("Dependency install packet", "plan dependency install packet <goal>")
            ]),
        new(
            "Plan and guard",
            "Turn an idea into an owner-reviewable roadmap, tests, and file plan.",
            [
                new("Roadmap", "draft implementation roadmap <goal>"),
                new("Acceptance criteria", "write acceptance criteria <goal>"),
                new("Focused tests", "suggest tests for <goal>"),
                new("Codebase patterns", "detect codebase patterns"),
                new("Feature files", "plan feature files <goal>"),
                new("Refactor safety", "show refactor safety checklist <goal>")
            ]),
        new(
            "Execute through gates",
            "Apply reviewed edits and run approved work through Ali's normal confirmation gates.",
            [
                new("Patch bundle", "preview patch bundle"),
                new("Apply patch", "confirm apply last patch preview", RequiresConfirmation: true),
                new("Packet console", "show packet commands"),
                new("Run packet item", "confirm run packet item N", RequiresConfirmation: true),
                new("Review changes", "review current changes"),
                new("Validation plan", "validation plan")
            ]),
        new(
            "VS and reports",
            "Surface integration status and owner-readable receipts.",
            [
                new("VS status", "show visual studio integration"),
                new("Session summary", "show coding session summary"),
                new("Coding report", "generate coding report"),
                new("Morning report", "generate morning report")
            ]),
        new(
            "PDF tools",
            "Create, inspect, extract, summarize, and assemble local PDF outputs through PDF permission gates.",
            [
                new("PDF status", "show pdf tool status"),
                new("PDF commands", "show pdf commands"),
                new("Create PDF", "generate pdf \"name.pdf\" with text \"...\""),
                new("Inspect PDF", "inspect pdf \"path-or-name.pdf\""),
                new("Extract PDF text", "extract text from pdf \"path-or-name.pdf\""),
                new("Summarize PDF", "summarize pdf \"path-or-name.pdf\""),
                new("Markdown to PDF", "convert markdown to pdf \"notes.md\" \"notes.pdf\""),
                new("Combine PDFs", "confirm combine pdfs \"one.pdf\" \"two.pdf\" \"combined.pdf\"", RequiresConfirmation: true),
                new("Split PDF", "confirm split pdf \"source.pdf\" \"split-output.pdf\"", RequiresConfirmation: true),
                new("Install report", "generate install report pdf"),
                new("Troubleshooting report", "generate troubleshooting report pdf")
            ]),
        new(
            "Computer assistant",
            "Plan everyday local computer help while keeping risky changes gated.",
            [
                new("Computer status", "show computer assistant status"),
                new("Computer commands", "show computer assistant commands"),
                new("File organization", "plan file organization \"C:\\Users\\<you>\\Downloads\""),
                new("Disk cleanup", "plan disk cleanup"),
                new("Install troubleshooting", "plan app install troubleshooting <app-or-error>"),
                new("Peripheral setup", "plan peripheral setup <device-or-symptom>")
            ]),
        new(
            "Windows diagnostics",
            "Collect read-only troubleshooting context before proposing repair options.",
            [
                new("Process evidence", "collect process evidence <name-or-pid>"),
                new("Port owner", "diagnose port <port>"),
                new("Build lock", "diagnose build lock"),
                new("Install doctor", "show install doctor")
            ])
    ];

    public static IReadOnlyList<CodingAbilityGroup> ComputerGroups { get; } =
    [
        new(
            "Start here",
            "Deterministic front doors for local ability questions.",
            [
                new("Status", "show computer assistant status"),
                new("Index", "show computer assistant commands"),
                new("Coding skills", "show coding skill command index")
            ]),
        new(
            "Ability questions",
            "Natural questions that route to this deterministic ability surface.",
            [
                new("What can you do", "what can you do"),
                new("Abilities", "can you tell me about your abilities"),
                new("Limits", "what are your programming and data access limitations")
            ]),
        new(
            "Everyday computer planning",
            "Plan common local-computer cleanup and setup tasks before changing anything.",
            [
                new("File organization", "plan file organization \"C:\\Users\\<you>\\Downloads\""),
                new("Disk cleanup", "plan disk cleanup"),
                new("Install help", "plan app install troubleshooting Visual Studio installer crash"),
                new("Peripheral setup", "plan peripheral setup Scarlett Solo microphone gain")
            ]),
        new(
            "Troubleshooting planners",
            "Read-only diagnostic planning for common computer problems.",
            [
                new("Troubleshooting index", "show computer troubleshooting commands"),
                new("Slow PC", "plan slow computer troubleshooting"),
                new("Network", "plan network troubleshooting"),
                new("Printer", "plan printer troubleshooting"),
                new("Windows Update", "plan windows update troubleshooting")
            ]),
        new(
            "Windows diagnostics",
            "Owner-visible commands for process, port, service/startup, event-log, and install-readiness triage.",
            [
                new("Toolkit", "show windows troubleshooting toolkit"),
                new("Process evidence", "collect process evidence <name-or-pid>"),
                new("Port owner", "diagnose port <port>"),
                new("Services/startup", "inspect services and startup"),
                new("Event logs", "triage event logs"),
                new("Install doctor", "show install doctor")
            ]),
        new(
            "PDF/document work",
            "Work in the configured PDF workspace and preserve originals.",
            [
                new("PDF commands", "show pdf commands"),
                new("Create PDF", "generate pdf \"name.pdf\" with text \"...\""),
                new("Inspect PDF", "inspect pdf \"document.pdf\""),
                new("Summarize PDF", "summarize pdf \"document.pdf\"")
            ]),
        new(
            "Coding and Visual Studio",
            "Use the local coding assistant and Visual Studio companion through approval gates.",
            [
                new("VS status", "show visual studio integration"),
                new("Workspace", "inspect coding workspace"),
                new("Build goal", "interpret build goal <goal>"),
                new("Roadmap", "draft implementation roadmap <goal>"),
                new("Execution packet", "show execution packet"),
                new("Run packet", "confirm run packet item N", RequiresConfirmation: true)
            ])
    ];

    public static IReadOnlyList<CodingAbilityGroup> PdfGroups { get; } =
    [
        new(
            "Create",
            "Create new local PDFs from text, Markdown, and report generators.",
            [
                new("Text PDF", "generate pdf \"owner-demo.pdf\" with text \"Ali demo ready.\""),
                new("Markdown to PDF", "convert markdown to pdf \"notes.md\" \"notes.pdf\""),
                new("Install report", "generate install report pdf"),
                new("Troubleshooting report", "generate troubleshooting report pdf")
            ]),
        new(
            "Inspect and read",
            "Inspect and summarize PDFs that expose readable text.",
            [
                new("PDF status", "show pdf tool status"),
                new("Inspect", "inspect pdf \"document.pdf\""),
                new("Extract text", "extract text from pdf \"document.pdf\""),
                new("Summarize", "summarize pdf \"document.pdf\"")
            ]),
        new(
            "Assemble with confirmation",
            "Create derived PDFs while preserving originals.",
            [
                new("Combine", "confirm combine pdfs \"first.pdf\" \"second.pdf\" \"combined.pdf\"", RequiresConfirmation: true),
                new("Split", "confirm split pdf \"source.pdf\" \"split-output.pdf\"", RequiresConfirmation: true)
            ])
    ];

    public static IReadOnlyList<UserCommandHelpTopic> UserCommandHelpTopics { get; } =
    [
        new(
            "Chat",
            "Conversation controls for starting fresh or clearing saved local chats.",
            [
                new("New chat", "Start a fresh local conversation.", "Use the New chat button in the left sidebar."),
                new("Erase history", "Erase saved local conversations after confirmation.", "Use the Erase History button in the left sidebar.")
            ]),
        new(
            "Sources",
            "Approved web sources, topic labels, and the local document library.",
            [
                new("Sources and topics", "Manage source URLs and the topics they are useful for.", "Use the Sources button in the top bar."),
                new("Local library", "Choose the approved local RAG folder and scan it.", "Use the Local Library button in the top bar.")
            ]),
        new(
            "Voice",
            "Speech, push-to-talk, microphone, and local voice controls.",
            [
                new("Voice settings", "Set microphone, voice engine, selected voice, PTT, and speech speed.", "Use Settings -> Voice / Mic."),
                new("Hear sample", "Play a sample of the selected local voice.", "Use Settings -> Voice / Mic -> Hear Sample.")
            ]),
        new(
            "Runtime",
            "Local model health, model selection, and install readiness.",
            [
                new("Check runtime", "Run local model health checks.", "Use Settings -> Runtime -> Check."),
                new("Install doctor", "Report install, runtime, model, VSIX, and dependency readiness.", "show install doctor", "show install doctor")
            ]),
        new(
            "Programming",
            "Coding workspace inspection, planning, packages, guarded builds, tests, and reports.",
            [
                new("Command index", "Show deterministic coding commands Ali supports.", "show coding skill command index", "show coding skill command index"),
                new("Inspect workspace", "Inspect the approved coding workspace.", "inspect coding workspace", "inspect coding workspace"),
                new("Analyze solution", "Analyze solution architecture.", "analyze solution architecture", "analyze solution architecture"),
                new("List packages", "List package references.", "list packages", "list packages"),
                new("Plan coding task", "Draft a guarded implementation plan.", "plan coding task add a settings button", "plan coding task <goal>"),
                new("Build", "Run a confirmed dotnet build.", "confirm dotnet build \"C:\\path\\to\\solution.sln\"", "confirm dotnet build \"C:\\path\\to\\solution.sln\""),
                new("Test", "Run a confirmed dotnet test.", "confirm dotnet test \"C:\\path\\to\\solution-or-project\"", "confirm dotnet test \"C:\\path\\to\\solution-or-project\""),
                new("Review changes", "Summarize uncommitted files, diff check status, risk hints, and next validation.", "review current changes", "review current changes"),
                new("Validation plan", "Show the next build, test, review, and commit checks after edits.", "validation plan", "validation plan"),
                new("Diagnose failure", "Summarize the last failed confirmed build or test.", "diagnose last build failure", "diagnose last build failure"),
                new("Suggest fix", "Preview a deterministic patch for simple compiler failures without changing files.", "suggest patch from last failure", "suggest patch from last failure"),
                new("Coding report", "Generate a coding session report.", "generate coding report", "generate coding report")
            ]),
        new(
            "Computer",
            "Read-only diagnostics and plan-first local computer troubleshooting.",
            [
                new("Status", "Show local computer help boundaries.", "show computer assistant status", "show computer assistant status"),
                new("Command index", "List deterministic computer-management commands.", "show computer assistant commands", "show computer assistant commands"),
                new("Running processes", "Read-only snapshot of top local processes by memory.", "collect process evidence", "collect process evidence"),
                new("Build lock check", "Find common build helper processes holding files.", "diagnose build lock", "diagnose build lock"),
                new("Port owner", "Check which process owns a local port.", "diagnose port 8765", "diagnose port 8765"),
                new("Services and startup", "Inspect service and startup evidence.", "inspect services and startup", "inspect services and startup"),
                new("Event logs", "Triage recent Windows event log clues.", "triage event logs", "triage event logs"),
                new("Slow computer plan", "Plan safe first checks for performance issues.", "plan slow computer troubleshooting", "plan slow computer troubleshooting"),
                new("Wi-Fi plan", "Plan safe checks for connection drops.", "troubleshoot wifi dropping connection", "troubleshoot wifi dropping connection"),
                new("Suspicious activity plan", "Plan evidence gathering for unknown startup/process activity.", "plan suspicious activity check unknown startup item", "plan suspicious activity check unknown startup item"),
                new("Disk cleanup plan", "Plan cleanup without deleting files automatically.", "plan disk cleanup", "plan disk cleanup")
            ]),
        new(
            "PDF",
            "Local PDF create, inspect, extract, summarize, combine, and split commands.",
            [
                new("PDF status", "Show PDF tool readiness.", "show pdf tool status", "show pdf tool status"),
                new("PDF commands", "Show PDF command index.", "show pdf commands", "show pdf commands"),
                new("Generate PDF", "Create a PDF in the approved PDF workspace.", "generate pdf \"demo.pdf\" with text \"One page summary.\"", "generate pdf \"name.pdf\" with text \"content\""),
                new("Inspect PDF", "Inspect a PDF document.", "inspect pdf \"demo.pdf\"", "inspect pdf \"file.pdf\""),
                new("Extract text", "Extract text from a PDF.", "extract text from pdf \"demo.pdf\"", "extract text from pdf \"file.pdf\""),
                new("Combine PDFs", "Combine PDFs after confirmation.", "confirm combine pdfs \"a.pdf\" \"b.pdf\" \"combined.pdf\"", "confirm combine pdfs \"a.pdf\" \"b.pdf\" \"combined.pdf\"")
            ]),
        new(
            "Memory / Reminders",
            "Review saved local memories and reminder items.",
            [
                new("Review memories", "Open Settings to review saved local memories.", "Use Settings -> Memory / Reminders."),
                new("Review reminders", "Open Settings to review reminders.", "Use Settings -> Memory / Reminders.")
            ]),
        new(
            "Visual Studio",
            "Ali Companion VSIX status and loopback bridge helpers.",
            [
                new("Integration status", "Show Visual Studio integration and bridge status.", "show visual studio integration", "show visual studio integration"),
                new("VS handoff", "Generate a Visual Studio integration plan.", "generate visual studio integration plan", "generate visual studio integration plan")
            ])
    ];

    public static string BuildBuilderCommandIndex()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali coding skill command index:");
        builder.AppendLine("No files were changed.");
        builder.AppendLine("Fast builder path:");
        AppendNumbered(builder, FastBuilderPath);
        AppendGroups(builder, BuilderGroups);
        builder.AppendLine("Ability-index maintenance rule:");
        builder.AppendLine("- Each new feature should be surfaced in this shared catalog, in the helper/VS command buttons when useful, and in the user/engineering docs.");
        builder.AppendLine("Prototype/future lane:");
        builder.AppendLine("- Screenshot bug diagnosis can use existing temporary image attachments and local vision proof, but reliable screenshot-to-source debugging still needs a dedicated evidence/triage workflow.");
        return builder.ToString().TrimEnd();
    }

    public static string BuildUserCommandHelpGuide()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali feature guide:");
        builder.AppendLine("Here are the main things I can help with. The Commands button shows this same guide as a clickable tree.");
        builder.AppendLine("Nothing was changed on your computer.");
        foreach (var topic in UserCommandHelpTopics)
        {
            builder.AppendLine();
            builder.AppendLine($"{topic.Name}: {topic.Summary}");
            foreach (var entry in topic.Entries.Take(5))
            {
                builder.AppendLine($"- {entry.Title}: {entry.Summary}");
                if (!string.IsNullOrWhiteSpace(entry.Command))
                {
                    builder.AppendLine($"  Try: {entry.Command}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("Safety rule:");
        builder.AppendLine("- Commands that build, test, edit files, install packages, combine PDFs, stop processes, or change the computer still require the normal owner confirmation.");
        return builder.ToString().TrimEnd();
    }

    public static string BuildComputerAssistantStatus(string workspaceRoot, string pdfWorkspaceRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali computer assistant status:");
        builder.AppendLine($"Coding workspace: {workspaceRoot}");
        builder.AppendLine($"PDF workspace: {pdfWorkspaceRoot}");
        builder.AppendLine("Visible helper lanes:");
        builder.AppendLine("- Coding/Visual Studio companion: workspace inspection, planning, gated edits, builds, tests, packages, Git, reports.");
        builder.AppendLine("- PDF workspace: create/export, inspect/extract/summarize, Markdown conversion, gated combine/split.");
        builder.AppendLine("- Windows troubleshooting: processes, ports, services/startup, event logs, build locks, install readiness.");
        builder.AppendLine("- General computer planning: file organization, disk cleanup, app install troubleshooting, peripheral setup.");
        builder.AppendLine("- Source-backed answers: Ali can use approved curated web/source entries when the app performs a source lookup; this is not unrestricted browsing.");
        builder.AppendLine("- Audio setup sources: Focusrite Scarlett Solo/2i2, AT2040, FetHead, and Shure SH-BROADCAST2 source links are available as reference material.");
        builder.AppendLine("Guardrails:");
        builder.AppendLine("- Status and planning commands are read-only.");
        builder.AppendLine("- File moves, deletes, installers, services, startup entries, registry, firewall, PATH, drivers, and process stops require explicit approval through narrower commands.");
        builder.AppendLine("- If Ali is uncertain, she should stop with options instead of pretending a fix is deterministic.");
        builder.AppendLine("Fast commands:");
        foreach (var command in ComputerGroups.SelectMany(group => group.Commands).Take(12))
        {
            builder.AppendLine($"- {command.Command}");
        }

        builder.AppendLine("Truth boundary:");
        builder.AppendLine("- Ali should not claim she has no internet/source access when approved source lookup is available.");
        builder.AppendLine("- Ali should say source access is curated/approved-source lookup, not a free-form browser or autonomous web agent.");
        return builder.ToString().TrimEnd();
    }

    public static string BuildComputerAssistantCommandIndex()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali computer assistant command index:");
        builder.AppendLine("No files, apps, services, or settings were changed.");
        AppendGroups(builder, ComputerGroups);
        builder.AppendLine("Future executor lane:");
        builder.AppendLine("- File cleanup execution should be previewed as a move/copy plan first, then applied only after confirmation.");
        builder.AppendLine("- Driver, installer, registry, trust-store, and service repairs stay owner-approved.");
        return builder.ToString().TrimEnd();
    }

    public static string BuildPdfCommandIndex(string pdfWorkspaceRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali PDF command index:");
        builder.AppendLine("No files were changed.");
        AppendGroups(builder, PdfGroups);
        builder.AppendLine("Folder rule:");
        builder.AppendLine($"- Relative names use the configured PDF workspace: {pdfWorkspaceRoot}");
        builder.AppendLine("Limits:");
        builder.AppendLine("- Ali preserves originals and writes new derived PDFs.");
        builder.AppendLine("- Text extraction works best on Ali-generated/simple text PDFs.");
        builder.AppendLine("- Scanned/image-only PDFs need OCR in a later phase.");
        return builder.ToString().TrimEnd();
    }

    private static void AppendGroups(StringBuilder builder, IEnumerable<CodingAbilityGroup> groups)
    {
        foreach (var group in groups)
        {
            builder.AppendLine($"{group.Name}:");
            foreach (var command in group.Commands)
            {
                var suffix = command.RequiresConfirmation ? " (confirmation required)" : string.Empty;
                builder.AppendLine($"- {command.Command}{suffix}");
            }
        }
    }

    private static void AppendNumbered(StringBuilder builder, IReadOnlyList<string> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {commands[index]}");
        }
    }
}
