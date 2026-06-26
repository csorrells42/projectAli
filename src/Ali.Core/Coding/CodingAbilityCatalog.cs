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
        "plan post edit validation"
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
                new("Post-edit validation", "plan post edit validation")
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
