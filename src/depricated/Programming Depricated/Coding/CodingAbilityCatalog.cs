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

public sealed record CodingCapabilityPath(
    string Name,
    string WhenToUse,
    IReadOnlyList<string> BuildingBlocks,
    IReadOnlyList<string> CommandSequence);

public sealed record CodingKnowledgeSection(
    string Name,
    IReadOnlyList<string> Guidance);

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
    public static IReadOnlyList<CodingCapabilityPath> ProgrammingCapabilityPaths { get; } =
    [
        new(
            "New console app",
            "Use when the user asks for a terminal/console program, command-line utility, simple exercise, calculator, list manager, file-backed notes app, or similar non-UI app.",
            [
                "Choose or create the active project first.",
                "Use Program.cs and the simplest data structure that satisfies input, lookup, ordering, and persistence needs.",
                "Add input validation, clear output, loop/menu behavior when needed, and a keypress hold only when requested.",
                "Validate with the smallest dotnet build/test command that covers the project."
            ],
            [
                "active workspace project",
                "build this for me <goal>",
                "preview synthesized feature patch <goal>",
                "confirm apply last patch preview",
                "post patch validation <goal>"
            ]),
        new(
            "New WPF app or complex window",
            "Use when the user asks for a desktop GUI, window, dashboard, form, tool surface, data grid, navigation shell, modal dialog, or rich WPF layout.",
            [
                "Choose Window/UserControl boundaries before editing.",
                "Use MVVM-style properties/commands for real behavior; avoid code-behind except for WPF-specific window services/events.",
                "Pick Grid, DockPanel, TabControl, ContentControl, ItemsControl/DataGrid/TreeView, GridSplitter, ResourceDictionary, templates, validation, virtualization, and async UI state as needed.",
                "Generate or update App.xaml, MainWindow.xaml, code-behind, view model, styles, dialogs, and user controls as a coherent patch bundle."
            ],
            [
                "active workspace project",
                "wpf complex window guide <goal>",
                "build this for me <goal>",
                "preview synthesized feature patch <goal>",
                "confirm apply last patch preview",
                "post patch validation <goal>"
            ]),
        new(
            "Existing feature or bug fix",
            "Use when the user wants to change current code, add a button/setting/workflow, or repair app behavior in an existing project.",
            [
                "Inspect the active project, ownership map, relevant symbols, and impacted tests.",
                "Use Roslyn targeting for C# symbols and XAML binding checks for WPF surfaces.",
                "Prefer small multi-file patch bundles with explicit before/after behavior and validation.",
                "Do not apply until the owner confirms the preview."
            ],
            [
                "coding context packet <goal>",
                "semantic edit plan <goal>",
                "roslyn edit planner <goal>",
                "feature patch draft <goal>",
                "exact patch synthesis <goal>",
                "multi-file patch synthesis <goal>",
                "concrete patch authoring <goal>",
                "preview synthesized feature patch <goal>",
                "confirm apply last patch preview",
                "post patch validation <goal>"
            ]),
        new(
            "Data, service, or persistence feature",
            "Use when the request involves data structures, files, SQL/database, caching, queues, APIs, background work, or performance-sensitive state.",
            [
                "Choose the data structure/store from lookup, ordering, persistence, concurrency, and scale requirements.",
                "For SQL, plan schema, keys, indexes, migrations, transactions, parameterized queries, and measured validation.",
                "For services, plan boundaries, retries, idempotency, health checks, logging, and configuration.",
                "Connect the chosen plan to target files and tests before editing."
            ],
            [
                "data structure chooser <goal>",
                "data systems guide <goal>",
                "service architecture guide <goal>",
                "feature implementation planner <goal>",
                "multi-file patch synthesis <goal>",
                "post patch validation <goal>"
            ]),
        new(
            "Build/test repair loop",
            "Use when the user reports a compile, test, package, XAML binding, runtime, or validation failure.",
            [
                "Read the latest failure evidence before drafting another patch.",
                "Map diagnostics to file, symbol, route, or binding.",
                "Preview the smallest repair patch and rerun the narrowest validation command.",
                "Escalate only if the same failure repeats after a targeted repair."
            ],
            [
                "diagnose last build failure",
                "first diagnostic repair route <goal>",
                "suggest patch from last failure",
                "validation repair runner <goal>",
                "preview synthesized feature patch <goal>",
                "confirm apply last patch preview",
                "post patch validation <goal>"
            ]),
        new(
            "Closeout and handoff",
            "Use after edits validate and the user asks to wrap up, review, commit, report, or prepare delivery.",
            [
                "Summarize semantic changes, changed files, validation receipts, residual risk, and commit readiness.",
                "Use Git write operations only after explicit owner request.",
                "Keep release notes and reports grounded in actual receipts."
            ],
            [
                "semantic diff summary <goal>",
                "semantic change receipt <goal>",
                "review current changes",
                "can i safely commit",
                "draft commit message"
            ])
    ];

    public static IReadOnlyList<string> FastBuilderPath { get; } =
    [
        "interpret build goal <goal>",
        "build this for me <goal>",
        "feature intake <goal>",
        "autonomous feature orchestrator <goal>",
        "roslyn edit planner <goal>",
        "feature patch draft <goal>",
        "exact patch synthesis <goal>",
        "multi-file patch synthesis <goal>",
        "preview synthesized feature patch <goal>",
        "preview guided feature bundle <goal>",
        "concrete patch authoring <goal>",
        "patch body generator <goal>",
        "patch confidence score <goal>",
        "active workspace project",
        "feature work context <goal>",
        "owner approved apply packet <goal>",
        "confirm apply last patch preview",
        "post patch validation <goal>",
        "validation command minimizer <goal>",
        "authoring sequence flow <goal>",
        "validation chain planner <goal>",
        "diagnose last build failure",
        "validation repair runner <goal>",
        "first diagnostic repair route <goal>",
        "failure to patch v3 <goal>",
        "data systems guide <goal>",
        "data structure chooser <goal>",
        "sql performance guide <goal>",
        "service architecture guide <goal>",
        "cache queue guide <goal>",
        "console app guide <goal>",
        "wpf app guide <goal>",
        "wpf layout guide <goal>",
        "wpf controls guide <goal>",
        "wpf styling guide <goal>",
        "wpf complex window guide <goal>",
        "show architecture options <goal>",
        "write acceptance criteria <goal>",
        "suggest tests for <goal>",
        "draft implementation roadmap <goal>",
        "approve last roadmap",
        "start approved roadmap",
        "project index",
        "coding context packet",
        "show next coding action",
        "semantic diff summary <goal>",
        "semantic change receipt <goal>",
        "review current changes",
        "can i safely commit",
        "implementation evidence pack <goal>",
        "mini codex score v3 <goal>",
        "show execution packet",
        "validation plan"
    ];

    public static IReadOnlySet<string> PatchPreviewToolTemplates { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "build this for me <goal>",
        "feature patch draft <goal>",
        "exact patch synthesis <goal>",
        "multi-file patch synthesis <goal>",
        "preview synthesized feature patch <goal>",
        "preview guided feature bundle <goal>",
        "concrete patch authoring <goal>",
        "patch body generator <goal>"
    };

    public static IReadOnlyList<string> DataSystemsKnowledge { get; } =
    [
        "Data structures: arrays/lists, stacks, queues/deques, dictionaries/hash maps, sets, sorted maps/sets, heaps/priority queues, trees/tries, graphs, LRU caches, Bloom filters, and when each fits lookup, ordering, memory, and concurrency needs.",
        "C# collections: List<T>, Dictionary<TKey,TValue>, HashSet<T>, SortedDictionary<TKey,TValue>, SortedSet<T>, Queue<T>, Stack<T>, PriorityQueue<TElement,TPriority>, ConcurrentDictionary, Channel<T>, Immutable collections, and Span/Memory for hot paths.",
        "SQL data stores: SQL Server, PostgreSQL, MySQL/MariaDB, and SQLite; choose by deployment shape, transactions, concurrency, indexing, full-text/search needs, operations burden, and local/offline requirements.",
        "Fast SQL design: schema normalization where useful, correct keys and constraints, covering/composite indexes, query plans, pagination, batching, parameterized queries, transactions, connection pooling, migrations, and measured performance baselines.",
        "Service patterns: repository/unit-of-work only when it reduces coupling, background workers, hosted services, HTTP APIs, message queues, retry/idempotency, caching boundaries, health checks, structured logging, and configuration/secrets separation.",
        "Caching and queues: Redis/in-memory caches, cache invalidation rules, TTLs, pub/sub, durable queues, outbox pattern, backpressure, and dead-letter handling.",
        "Validation approach: unit-test pure data-structure logic, integration-test database mappings and migrations, load-test hot queries/services, and prove failure behavior for retries, timeouts, and duplicate messages."
    ];

    public static IReadOnlyList<CodingKnowledgeSection> WpfObjectLayoutKnowledge { get; } =
    [
        new(
            "Shell and regions",
            [
                "Use Window for the shell, UserControl for reusable workflow regions, ContentControl for active view regions, and dialog windows/services for modal decisions.",
                "For complex windows, compose menu/tool/status rows around a resizable main Grid or DockPanel rather than placing everything in one flat panel."
            ]),
        new(
            "Layout containers",
            [
                "Prefer Grid for advanced windows; use Auto/star/fixed rows deliberately, SharedSizeGroup for aligned forms, and GridSplitter with MinWidth/MinHeight for owner-resizable panes.",
                "Use DockPanel for shell framing, StackPanel only for small one-axis groups, WrapPanel/UniformGrid for compact repeated controls, and ScrollViewer only around the region that should scroll."
            ]),
        new(
            "Data controls",
            [
                "Use ItemsControl/ListBox/ListView for repeated models, DataGrid for tabular review/edit screens, TreeView for hierarchy, TabControl for bounded modes, and CollectionViewSource for sorting, filtering, grouping, and current item state.",
                "Enable VirtualizingPanel virtualization and deferred scrolling for large lists/grids; keep SelectedItem and selection-dependent command state explicit."
            ]),
        new(
            "Binding and view models",
            [
                "Use INotifyPropertyChanged, ObservableCollection<T>, ICommand/CanExecute, INotifyDataErrorInfo, and small model/view-model types instead of putting behavior in XAML names or code-behind.",
                "Use DependencyProperty for reusable controls, Freezable BindingProxy for binding across namescopes, WeakEventManager for long-lived event subscriptions, and DispatcherTimer/Dispatcher only for UI-thread coordination."
            ]),
        new(
            "Resources and templates",
            [
                "Use ResourceDictionary files for brushes, spacing, styles, DataTemplates, and ControlTemplates; prefer StaticResource unless runtime theme switching requires DynamicResource.",
                "Use DataTemplate, HierarchicalDataTemplate, DataTemplateSelector, Style BasedOn, triggers, and converters for presentation decisions; keep business decisions in view-model properties."
            ]),
        new(
            "Validation and interaction",
            [
                "Make invalid input visible with validation bindings, ErrorTemplate/Adorner feedback, disabled unsafe commands, status text, and focus behavior.",
                "Use RoutedCommand/InputBindings only for shell-level keyboard commands; keep feature actions in view-model ICommand properties when possible."
            ]),
        new(
            "Async and performance",
            [
                "Represent slow work with async commands, CancellationTokenSource, IsBusy, ProgressText, and cancel commands; never block the UI thread while loading, saving, or refreshing data.",
                "For large views, combine virtualization, paging/filtering, debounce timers, measured validation, and small observable updates instead of rebuilding the whole visual tree."
            ]),
        new(
            "Diagnostics and build order",
            [
                "Build order: shell layout, one bound workflow, view-model state/commands, validation, styles/templates, secondary panes/dialogs, then polish.",
                "Validation order: dotnet build, XAML binding check, command binding check, narrow UI smoke path, then repair compiler/binding/resource/event errors before adding more surface area.",
                "Integrity checks: keep x:Class, namespaces, partial classes, code-behind files, ResourceDictionary Source paths, resource keys, DataTemplates, converters, template selectors, and event handlers aligned."
            ]),
        new(
            "Final WPF reasoning lanes",
            [
                "Advanced binding diagnostics: identify the DataContext owner, add targeted diag:PresentationTraceSources.TraceLevel=High while repairing suspect bindings, and use BindingProxy, PlacementTarget, RelativeSource, or named sources across ContextMenu/template namescope boundaries.",
                "MVVM implementation: create observable properties, ObservableCollection<T>/ICollectionView state, ICommand/CanExecute actions, async/cancel flow, validation state, and dialog/service boundaries before expanding XAML.",
                "Complex controls: pick DataGrid columns/templates/grouping, TreeView with HierarchicalDataTemplate, TabControl/ContentControl regions, dialogs/wizards, virtualization, and templates from the actual workflow shape.",
                "Patch synthesis: preview WPF work as a coherent multi-file bundle that keeps XAML, view models, resources, converters, selectors, and minimal code-behind bridges aligned.",
                "Completion audit: after applying WPF edits, run build, XAML binding check, command binding check, a narrow UI smoke path, and remove obsolete helper routes before closeout."
            ])
    ];

    public static IReadOnlyList<string> WpfComplexWindowConstructionRoute { get; } =
    [
        "Identify the requested window shape first: simple form, dashboard, data manager, wizard/dialog, inspector, or tool surface.",
        "Select the shell and region pattern: Window shell, Grid/DockPanel frame, optional menu/tool/status rows, and ContentControl/TabControl/UserControl regions only where the workflow needs them.",
        "Choose the data surface from the actual data shape: DataGrid for editable rows, ListView/ItemsControl for cards, TreeView for hierarchy, TabControl for bounded modes, and detail panes for selected-item work.",
        "Define view-model state before XAML bindings: properties, ObservableCollection<T>/ICollectionView, selected item, ICommand/CanExecute, validation state, busy/progress text, and cancel state when work may be slow.",
        "Place reusable visuals in ResourceDictionary styles/templates and keep keys, Source paths, converters, template selectors, and DataTemplates defined before any XAML references them.",
        "Keep code-behind limited to WPF shell services, lifecycle events, focus/scroll behavior, dependency properties, or dialog bridges that cannot cleanly live in the view model.",
        "For large or changing surfaces, add virtualization, deferred scrolling, filtering/search debounce, paging, or small observable updates before increasing visual complexity.",
        "Validate in order: dotnet build, XAML binding check, command binding check, narrow UI smoke path, then repair errors before expanding scope."
    ];

    public static IReadOnlyList<CodingAbilityGroup> BuilderGroups { get; } =
    [
        new(
            "Scout and choose",
            "Understand the workspace and compare build paths before editing.",
            [
                new("Interpret goal", "interpret build goal <goal>"),
                new("Project intelligence", "show project intelligence"),
                new("Project index", "project index"),
                new("Understand repo", "understand repo"),
                new("Context packet", "coding context packet <goal>"),
                new("Health score", "workspace health score"),
                new("Full readiness", "full coding readiness"),
                new("C# symbol index", "show csharp symbol index"),
                new("Call graph", "show call graph <name>"),
                new("Resolve symbol", "resolve symbol <name>"),
                new("Impacted tests", "show impacted tests <name>"),
                new("Semantic edit plan", "semantic edit plan <goal>"),
                new("Diagnostic mapper", "map compiler diagnostic <error>"),
                new("XAML bindings", "xaml binding check"),
                new("Command bindings", "command binding check"),
                new("Dead commands", "dead command scan"),
                new("Find symbol", "find symbol <name>"),
                new("Cross references", "cross reference <name>"),
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
                new("Build this", "build this for me <goal>"),
                new("Feature intake", "feature intake <goal>"),
                new("Feature orchestrator", "autonomous feature orchestrator <goal>"),
                new("Roslyn planner", "roslyn edit planner <goal>"),
                new("Pattern copy", "pattern copy <goal>"),
                new("Implementation planner", "feature implementation planner <goal>"),
                new("Slice state", "implementation slice state <goal>"),
                new("Evidence pack", "implementation evidence pack <goal>"),
                new("Score v3", "mini codex score v3 <goal>"),
                new("Active workspace", "active workspace project"),
                new("Patch authoring", "concrete patch authoring <goal>"),
                new("Patch body", "patch body generator <goal>"),
                new("Scaffolder", "pattern command scaffolder <goal>"),
                new("UI bundle", "ui bundle planner <goal>"),
                new("Confidence score", "patch confidence score <goal>"),
                new("Slice preview", "slice executor preview <goal>"),
                new("Insertion planner", "roslyn insertion planner <goal>"),
                new("Intent diff", "intent diff composer <goal>"),
                new("Spec scaffold", "behavior spec test scaffold <goal>"),
                new("Apply packet", "owner approved apply packet <goal>"),
                new("Validation minimizer", "validation command minimizer <goal>"),
                new("Binding repair", "ui binding repair planner <goal>"),
                new("Authoring flow", "authoring sequence flow <goal>"),
                new("Capability card", "coding capability card <goal>"),
                new("Failure to patch", "failure to patch v3 <goal>"),
                new("Failure memory", "repeat failure memory <goal>"),
                new("Diagnostic route", "first diagnostic repair route <goal>"),
                new("Change receipt", "semantic change receipt <goal>"),
                new("Validation chain", "validation chain planner <goal>"),
                new("Acceptance criteria", "write acceptance criteria <goal>"),
                new("Focused tests", "suggest tests for <goal>"),
                new("Codebase patterns", "detect codebase patterns"),
                new("Feature files", "plan feature files <goal>"),
                new("Refactor safety", "show refactor safety checklist <goal>")
            ]),
        new(
            "Data systems and services",
            "Plan data structures, persistence, caches, queues, APIs, and SQL-backed services before implementation.",
            [
                new("Data systems guide", "data systems guide <goal>"),
                new("Data structure chooser", "data structure chooser <goal>"),
                new("SQL performance guide", "sql performance guide <goal>"),
                new("Service architecture guide", "service architecture guide <goal>"),
                new("Cache/queue guide", "cache queue guide <goal>"),
                new("Data structure choice", "show architecture options <goal>"),
                new("Storage model", "coding context packet <goal>"),
                new("SQL/service plan", "feature implementation planner <goal>"),
                new("Package lookup", "plan package lookup <goal>"),
                new("Dependency install packet", "plan dependency install packet <goal>"),
                new("Validation chain", "validation chain planner <goal>")
            ]),
        new(
            "Console and WPF app craft",
            "Plan console apps and WPF desktop apps with usable input, output, advanced layout, controls, styling, binding, and validation habits.",
            [
                new("Console guide", "console app guide <goal>"),
                new("WPF guide", "wpf app guide <goal>"),
                new("WPF layout guide", "wpf layout guide <goal>"),
                new("WPF controls guide", "wpf controls guide <goal>"),
                new("WPF styling guide", "wpf styling guide <goal>"),
                new("WPF complex window guide", "wpf complex window guide <goal>"),
                new("Build front door", "build this for me <goal>"),
                new("Starter preview", "preview synthesized feature patch <goal>"),
                new("Apply packet", "owner approved apply packet <goal>"),
                new("Validation chain", "validation chain planner <goal>")
            ]),
        new(
            "Execute through gates",
            "Apply reviewed edits and run approved work through Ali's normal confirmation gates.",
            [
                new("Patch bundle", "preview patch bundle"),
                new("Patch synthesis v2", "multi-file patch synthesis <goal>"),
                new("Starter app preview", "preview synthesized feature patch <goal>"),
                new("Concrete patch authoring", "concrete patch authoring <goal>"),
                new("Patch body", "patch body generator <goal>"),
                new("Patch confidence", "patch confidence score <goal>"),
                new("Owner apply packet", "owner approved apply packet <goal>"),
                new("Intent diff", "intent diff composer <goal>"),
                new("Validation minimizer", "validation command minimizer <goal>"),
                new("Behavior test generator", "behavior test generator <goal>"),
                new("Spec scaffold", "behavior spec test scaffold <goal>"),
                new("Semantic diff", "semantic diff summary <goal>"),
                new("Validation chain", "validation chain planner <goal>"),
                new("Semantic receipt", "semantic change receipt <goal>"),
                new("Repair loop v2", "post apply repair loop <goal>"),
                new("Typed patch", "compose typed patch <goal>"),
                new("Apply patch", "confirm apply last patch preview", RequiresConfirmation: true),
                new("File risk labels", "show file risk labels"),
                new("Test gaps", "test gap report"),
                new("Packet console", "show packet commands"),
                new("Run packet item", "confirm run packet item N", RequiresConfirmation: true),
                new("Review changes", "review current changes"),
                new("Safe commit", "can i safely commit"),
                new("Commit message", "draft commit message"),
                new("Release notes", "draft release notes"),
                new("Rollback plan", "show rollback plan"),
                new("Rollback patch", "preview rollback patch"),
                new("Validation ledger", "show validation ledger"),
                new("Known error", "known error <compiler-or-build-error>"),
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
            "Internet backend settings and the local document library.",
            [
                new("Internet backend", "Configure Tavily search and Firecrawl extraction keys.", "Use Settings -> Internet."),
                new("Local library", "Choose the approved local RAG folder and scan it.", "Use the Local Library button under maintenance.")
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
                new("Project intelligence", "Summarize project shape, likely app/test targets, safe commands, and risk notes.", "show project intelligence", "show project intelligence"),
                new("Project index", "Build a persistent local index of project files, roles, symbols, stacks, and validation commands.", "project index", "project index"),
                new("Context packet", "Build a compact coding handoff with workspace shape, git state, targeted tests, and guardrails for the current goal.", "coding context packet Save button", "coding context packet <goal>"),
                new("Dependency map", "Show project references, reverse dependents, transitive impact, and build order for a project.", "project dependency map Ali.Core", "project dependency map <project>"),
                new("Understand repo", "Run Ali's orientation rollup: intelligence, architecture, patterns, validation, and commit readiness.", "understand repo", "understand repo"),
                new("Health score", "Score workspace readiness across target, projects, tests, git, and validation.", "workspace health score", "workspace health score"),
                new("Full readiness", "Run the one-click coding readiness rollup with workspace, bindings, command surface, symbols, commit gate, and validation ledger.", "full coding readiness", "full coding readiness"),
                new("Mini-Codex status", "Show Ali's current coding capability scores, missing rails, and next upgrade priorities.", "mini codex status", "mini codex status"),
                new("C# symbol index", "Show a Roslyn-backed index of likely C# types, methods, and properties.", "show csharp symbol index", "show csharp symbol index"),
                new("Ownership map", "Explain likely owning project, primary files, related files, tests, and validation commands for a symbol or file.", "ownership map Save", "ownership map <symbol-or-file>"),
                new("Call graph", "Show Roslyn-discovered caller-to-callee edges, optionally filtered by name.", "show call graph Save", "show call graph <name>"),
                new("Resolve symbol", "Use Roslyn semantic resolution to explain matching declarations and references.", "resolve symbol Save", "resolve symbol <name>"),
                new("Impacted tests", "Suggest likely source and test files affected by a symbol or current changed files.", "show impacted tests Save", "show impacted tests <name>"),
                new("Test target", "Resolve the smallest practical build/test target for a goal, symbol, or changed file.", "resolve test target Save", "resolve test target <goal>"),
                new("Semantic edit plan", "Plan a guarded edit from goal terms, semantic symbols, file risk, and validation needs.", "semantic edit plan settings command", "semantic edit plan <goal>"),
                new("Safe edit workflow", "Bridge a goal into inspect targets, patch preview gates, impacted tests, and validation commands without changing files.", "safe edit workflow settings command", "safe edit workflow <goal>"),
                new("Diagnostic mapper", "Map compiler diagnostics to file, line, nearest symbol, and fix lane.", "map compiler diagnostic CS0103", "map compiler diagnostic <error>"),
                new("XAML bindings", "Check WPF binding names against Roslyn-discovered code symbols.", "xaml binding check", "xaml binding check"),
                new("Command bindings", "Check button command bindings against view-model command properties.", "command binding check", "command binding check"),
                new("Command surface doctor", "Check action, parser, policy, service, test, and dashboard alignment before adding more coding tools.", "command surface doctor", "command surface doctor"),
                new("Dead commands", "Scan coding actions and dashboard bindings for missing handlers or targets.", "dead command scan", "dead command scan"),
                new("Analyze solution", "Analyze solution architecture.", "analyze solution architecture", "analyze solution architecture"),
                new("Find symbol", "Find likely declarations and matches for a class, method, property, or command name.", "find symbol LocalCodingToolService", "find symbol <name>"),
                new("Cross references", "Show likely declarations and usage lines for a symbol.", "cross reference CodingToolRequestParser", "cross reference <name>"),
                new("List packages", "List package references.", "list packages", "list packages"),
                new("Plan coding task", "Draft a guarded implementation plan.", "plan coding task add a settings button", "plan coding task <goal>"),
                new("Build", "Run a confirmed dotnet build.", "confirm dotnet build \"C:\\path\\to\\solution.sln\"", "confirm dotnet build \"C:\\path\\to\\solution.sln\""),
                new("Test", "Run a confirmed dotnet test.", "confirm dotnet test \"C:\\path\\to\\solution-or-project\"", "confirm dotnet test \"C:\\path\\to\\solution-or-project\""),
                new("Review changes", "Summarize uncommitted files, diff check status, risk hints, and next validation.", "review current changes", "review current changes"),
                new("Validation plan", "Show the next build, test, review, and commit checks after edits.", "validation plan", "validation plan"),
                new("Safe commit", "Give a simple yes/no commit readiness check.", "can i safely commit", "can i safely commit"),
                new("Commit message", "Draft a commit message from changed file areas.", "draft commit message", "draft commit message"),
                new("Release notes", "Draft release notes from changed file areas.", "draft release notes", "draft release notes"),
                new("Timeline", "Show recent coding receipts in order.", "show coding session timeline", "show coding session timeline"),
                new("Rollback plan", "Plan how to undo current changes without taking destructive action.", "show rollback plan", "show rollback plan"),
                new("Diagnose failure", "Summarize the last failed confirmed build or test.", "diagnose last build failure", "diagnose last build failure"),
                new("Suggest fix", "Preview a deterministic patch for simple compiler failures without changing files.", "suggest patch from last failure", "suggest patch from last failure"),
                new("Patch preview", "Show the pending patch preview before applying it.", "show pending patch preview", "show pending patch preview"),
                new("Typed patch", "Draft a structured multi-file patch plan without changing files.", "compose typed patch add settings validation", "compose typed patch <goal>"),
                new("File risks", "Label changed files by risk before applying or committing.", "show file risk labels", "show file risk labels"),
                new("Test gaps", "Report changed source files without obvious test updates.", "test gap report", "test gap report"),
                new("Known error", "Explain common compiler, NuGet, SDK, and XAML error patterns.", "known error CS0103", "known error <error>"),
                new("Rollback patch", "Preview what a rollback would affect without reverting anything.", "preview rollback patch", "preview rollback patch"),
                new("Apply preview", "Apply the reviewed pending patch preview through the confirmed edit gate.", "confirm apply last patch preview", "confirm apply last patch preview"),
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
        builder.AppendLine("Programming capability paths:");
        AppendCapabilityPaths(builder, ProgrammingCapabilityPaths);
        AppendGroups(builder, BuilderGroups);
        builder.AppendLine("Data systems knowledge:");
        AppendBullets(builder, DataSystemsKnowledge);
        builder.AppendLine("Advanced WPF object/layout decision map:");
        AppendKnowledgeSections(builder, WpfObjectLayoutKnowledge);
        builder.AppendLine("Ability-index maintenance rule:");
        builder.AppendLine("- Each new feature should be surfaced in this shared catalog, in the helper/VS command buttons when useful, and in the user/engineering docs.");
        builder.AppendLine("Prototype/future lane:");
        builder.AppendLine("- Screenshot bug diagnosis can use existing temporary image attachments and local vision proof, but reliable screenshot-to-source debugging still needs a dedicated evidence/triage workflow.");
        return builder.ToString().TrimEnd();
    }

    public static string BuildWpfObjectLayoutPlannerGuide()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Advanced WPF object/layout decision map:");
        AppendKnowledgeSections(builder, WpfObjectLayoutKnowledge);
        builder.AppendLine("Dynamic WPF construction route:");
        AppendNumbered(builder, WpfComplexWindowConstructionRoute);
        return builder.ToString().TrimEnd();
    }

    public static string BuildProgrammingCapabilityPathGuide()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Programming capability paths:");
        AppendCapabilityPaths(builder, ProgrammingCapabilityPaths);
        return builder.ToString().TrimEnd();
    }

    public static string BuildUserCommandHelpGuide()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Here is how I can help:");
        builder.AppendLine("- Chat: ask normal questions, brainstorm, write, summarize, and plan.");
        builder.AppendLine("- Current info: ask for internet-backed answers such as current weather, official government facts, news/source checks, and saved local library material when Tavily/Firecrawl or local sources are configured.");
        builder.AppendLine("- Weather: ask \"what is the weather in Tullahoma, TN\" or tell me your current city/state first. Multi-day forecasts are still being reworked.");
        builder.AppendLine("- Computer maintenance: use the Maintenance button for health checks, repair checks, process/window clues, startup/service clues, cleanup plans, and receipts.");
        builder.AppendLine("- Programming: use the Programming button or ask me to build a feature; I can show the active workspace/project, create simple console and WPF starter apps, reason about common data structures and service/database choices, advanced WPF object/layout choices, run intake, Roslyn targeting, pattern-copy planning, concrete patch authoring, patch body generation, owner apply packets, confidence scoring, patch/test previews, validation minimization, validation routing, semantic diff summaries, and guarded apply steps.");
        builder.AppendLine("- Voice: use push-to-talk, local transcription, local speech, and voice settings when the local voice pack is installed.");
        builder.AppendLine("- Internet and local library: use Settings > Internet for Tavily/Firecrawl keys, and Local Library under maintenance to manage approved local documents.");
        builder.AppendLine("- PDFs and documents: create, inspect, extract, summarize, combine, and split PDFs inside the approved workspace.");
        builder.AppendLine("- Memory/reminders: save useful local memories and review reminders in Settings.");
        builder.AppendLine();
        builder.AppendLine("How to use me:");
        builder.AppendLine("- Say what you want in plain language, or click Maintenance/Programming for button-based workflows.");
        builder.AppendLine("- For location-based weather, include the city and state unless you have already asked me to remember your current location.");
        builder.AppendLine("- For anything that changes files, installs software, stops processes, or alters Windows, I will ask for confirmation first.");
        builder.AppendLine();
        builder.AppendLine("More detailed command examples:");
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
        builder.AppendLine("- Source-backed answers: Ali can use the configured internet backend for Tavily search and Firecrawl extraction, plus approved local library documents when source lookup is needed.");
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
        builder.AppendLine("- Ali should not claim she has no internet/source access when configured source lookup is available.");
        builder.AppendLine("- Ali should say source access uses configured internet search/extraction and approved local library lookup, not a free-form browser or autonomous web agent.");
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

    private static void AppendCapabilityPaths(StringBuilder builder, IEnumerable<CodingCapabilityPath> paths)
    {
        foreach (var path in paths)
        {
            builder.AppendLine($"{path.Name}:");
            builder.AppendLine($"- When to use: {path.WhenToUse}");
            builder.AppendLine("- Building blocks:");
            AppendBullets(builder, path.BuildingBlocks);
            builder.AppendLine("- Command sequence:");
            AppendNumbered(builder, path.CommandSequence);
        }
    }

    private static void AppendNumbered(StringBuilder builder, IReadOnlyList<string> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {commands[index]}");
        }
    }

    private static void AppendBullets(StringBuilder builder, IEnumerable<string> rows)
    {
        foreach (var row in rows)
        {
            builder.AppendLine($"- {row}");
        }
    }

    private static void AppendKnowledgeSections(StringBuilder builder, IEnumerable<CodingKnowledgeSection> sections)
    {
        foreach (var section in sections)
        {
            builder.AppendLine($"- {section.Name}: {string.Join(" ", section.Guidance)}");
        }
    }
}
