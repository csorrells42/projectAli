namespace Ali.Core.Coding;

public static class CodingToolRequestParser
{
    private static readonly string[] ConfirmationPrefixes =
    [
        "confirm ",
        "confirmed ",
        "yes confirm ",
        "yes, confirm ",
        "go ahead and ",
        "go ahead, "
    ];

    private static readonly string[] OpenPrefixes =
    [
        "open ",
        "open file ",
        "open this file ",
        "open in notepad++ ",
        "open with notepad++ ",
        "open in notepad ",
        "open with notepad ",
        "open in visual studio ",
        "open with visual studio ",
        "debug solution ",
        "start debugging "
    ];

    private static readonly string[] SolutionPrefixes =
    [
        "open solution ",
        "open sln ",
        "open this solution ",
        "open project solution ",
        "open in visual studio ",
        "debug solution ",
        "start debugging "
    ];

    private static readonly string[] OpenSolutionRequests =
    [
        "open solution",
        "open sln",
        "open project solution",
        "open coding solution",
        "open visual studio",
        "open project in visual studio",
        "start visual studio",
        "debug solution",
        "start debugging"
    ];

    private static readonly string[] ReadPrefixes =
    [
        "read file ",
        "read this file ",
        "show file ",
        "show this file ",
        "inspect file ",
        "inspect this file "
    ];

    private static readonly string[] CreateFilePrefixes =
    [
        "create file ",
        "create new file ",
        "write file ",
        "write new file "
    ];

    private static readonly string[] AppendFilePrefixes =
    [
        "append to file ",
        "append file "
    ];

    private static readonly string[] ReplaceTextPrefixes =
    [
        "replace in file ",
        "replace text in file ",
        "replace literal in file "
    ];

    private static readonly string[] PreviewReplaceTextPrefixes =
    [
        "preview replace in file ",
        "preview replace text in file ",
        "preview replace literal in file ",
        "preview patch in file ",
        "dry run replace in file ",
        "dry-run replace in file "
    ];

    private static readonly string[] PreviewPatchBundlePrefixes =
    [
        "preview patch bundle",
        "preview patch set",
        "preview multi-file patch",
        "preview multifile patch",
        "preview multiple file patch",
        "dry run patch bundle",
        "dry-run patch bundle"
    ];

    private static readonly string[] GeneratePdfPrefixes =
    [
        "create pdf ",
        "create a pdf ",
        "generate pdf ",
        "generate a pdf ",
        "make pdf ",
        "make a pdf ",
        "write pdf ",
        "write a pdf "
    ];

    private static readonly string[] PdfToolStatusRequests =
    [
        "show pdf tool status",
        "show pdf status",
        "pdf tool status",
        "pdf status"
    ];

    private static readonly string[] PdfCommandIndexRequests =
    [
        "show pdf command index",
        "show pdf commands",
        "pdf command index",
        "pdf commands",
        "what can ali do with pdfs",
        "what can ali do with pdf"
    ];

    private static readonly string[] InspectPdfPrefixes =
    [
        "inspect pdf ",
        "inspect the pdf ",
        "analyze pdf ",
        "analyze the pdf ",
        "check pdf ",
        "check the pdf "
    ];

    private static readonly string[] ExtractPdfTextPrefixes =
    [
        "extract text from pdf ",
        "extract pdf text ",
        "read pdf text ",
        "read text from pdf "
    ];

    private static readonly string[] SummarizePdfPrefixes =
    [
        "summarize pdf ",
        "summarize the pdf ",
        "summary of pdf ",
        "summarize document pdf "
    ];

    private static readonly string[] ConvertMarkdownToPdfPrefixes =
    [
        "convert markdown to pdf ",
        "convert md to pdf ",
        "make pdf from markdown ",
        "generate pdf from markdown "
    ];

    private static readonly string[] CombinePdfPrefixes =
    [
        "combine pdfs ",
        "merge pdfs ",
        "assemble pdfs "
    ];

    private static readonly string[] SplitPdfPrefixes =
    [
        "split pdf ",
        "split the pdf ",
        "separate pdf ",
        "separate the pdf "
    ];

    private static readonly string[] GenerateCodingReportRequests =
    [
        "generate coding report",
        "generate coding session report",
        "generate code report",
        "create coding report",
        "create coding session report",
        "create code report",
        "export coding report",
        "export coding session report",
        "write coding report"
    ];

    private static readonly string[] GenerateInstallReportRequests =
    [
        "generate install report pdf",
        "generate installation report pdf",
        "create install report pdf",
        "create installation report pdf",
        "generate project install report",
        "create project install report"
    ];

    private static readonly string[] GenerateTroubleshootingReportRequests =
    [
        "generate troubleshooting report pdf",
        "create troubleshooting report pdf",
        "generate windows troubleshooting report",
        "create windows troubleshooting report",
        "generate computer troubleshooting report"
    ];

    private static readonly string[] ComputerAssistantStatusRequests =
    [
        "show computer assistant status",
        "show general computer assistant status",
        "show computer help status",
        "computer assistant status",
        "computer help status",
        "show ali computer status",
        "what are your programming and data access limitations",
        "what are your programming limitations",
        "what data access do you have",
        "what internet access do you have",
        "what sources can you use"
    ];

    private static readonly string[] UserCommandHelpRequests =
    [
        "show commands",
        "show ali commands",
        "show command explorer",
        "show feature guide",
        "show ali feature guide",
        "explain your commands",
        "explain ali commands",
        "help me understand your commands",
        "what commands do you know",
        "what features do you have",
        "what can you do",
        "what can ali do",
        "what are your abilities",
        "can you tell me about your abilities",
        "tell me about your abilities"
    ];

    private static readonly string[] ComputerAssistantCommandIndexRequests =
    [
        "show computer assistant commands",
        "show computer help commands",
        "show general computer commands",
        "computer assistant commands",
        "computer help commands",
        "what can ali do for computer help",
        "what can ali do with my computer",
        "can you tell me about your abilities",
        "tell me about your abilities",
        "what are your abilities",
        "what can you do",
        "what can you do on this computer",
        "what are your local computer abilities",
        "what are some of your abilities on this local computer"
    ];

    private static readonly string[] FileOrganizationPlanPrefixes =
    [
        "plan file organization ",
        "plan folder organization ",
        "plan organize files ",
        "plan organize folder ",
        "organize files plan ",
        "organize folder plan ",
        "plan downloads cleanup ",
        "plan documents cleanup "
    ];

    private static readonly string[] DiskCleanupPlanRequests =
    [
        "plan disk cleanup",
        "plan computer cleanup",
        "plan storage cleanup",
        "plan pc cleanup",
        "disk cleanup plan",
        "storage cleanup plan"
    ];

    private static readonly string[] DiskCleanupPlanPrefixes =
    [
        "plan disk cleanup ",
        "plan computer cleanup ",
        "plan storage cleanup ",
        "plan pc cleanup "
    ];

    private static readonly string[] AppInstallTroubleshootingPlanPrefixes =
    [
        "plan app install troubleshooting ",
        "plan install troubleshooting ",
        "troubleshoot app install ",
        "troubleshoot installer ",
        "plan installer fix ",
        "plan installation troubleshooting "
    ];

    private static readonly string[] PeripheralSetupPlanPrefixes =
    [
        "plan peripheral setup ",
        "plan device setup ",
        "plan audio setup ",
        "plan microphone setup ",
        "plan interface setup ",
        "troubleshoot peripheral setup ",
        "troubleshoot device setup "
    ];

    private static readonly string[] ComputerTroubleshootingCommandIndexRequests =
    [
        "show computer troubleshooting commands",
        "show pc troubleshooting commands",
        "show windows help commands",
        "computer troubleshooting commands",
        "pc troubleshooting commands",
        "windows help commands"
    ];

    private static readonly (string Request, string Scenario)[] ComputerTroubleshootingPlanRequests =
    [
        ("plan slow computer troubleshooting", "Slow computer"),
        ("plan network troubleshooting", "Network"),
        ("plan wifi troubleshooting", "Wi-Fi"),
        ("plan wi-fi troubleshooting", "Wi-Fi"),
        ("plan printer troubleshooting", "Printer"),
        ("plan audio troubleshooting", "Audio"),
        ("plan microphone troubleshooting", "Microphone"),
        ("plan camera troubleshooting", "Camera"),
        ("plan webcam troubleshooting", "Camera"),
        ("plan bluetooth troubleshooting", "Bluetooth"),
        ("plan usb troubleshooting", "USB device"),
        ("plan usb device troubleshooting", "USB device"),
        ("plan display troubleshooting", "Display"),
        ("plan monitor troubleshooting", "Display"),
        ("plan windows update troubleshooting", "Windows Update"),
        ("plan app crash troubleshooting", "App crash"),
        ("plan startup cleanup", "Startup cleanup"),
        ("plan browser troubleshooting", "Browser"),
        ("plan onedrive troubleshooting", "OneDrive sync"),
        ("plan onedrive sync troubleshooting", "OneDrive sync"),
        ("plan backup strategy", "Backup"),
        ("plan driver troubleshooting", "Driver"),
        ("plan suspicious activity check", "Suspicious activity"),
        ("plan remote support handoff", "Remote support handoff")
    ];

    private static readonly (string Prefix, string Scenario)[] ComputerTroubleshootingPlanPrefixes =
    [
        ("plan slow computer troubleshooting ", "Slow computer"),
        ("troubleshoot slow computer ", "Slow computer"),
        ("plan network troubleshooting ", "Network"),
        ("troubleshoot network ", "Network"),
        ("plan wifi troubleshooting ", "Wi-Fi"),
        ("plan wi-fi troubleshooting ", "Wi-Fi"),
        ("troubleshoot wifi ", "Wi-Fi"),
        ("troubleshoot wi-fi ", "Wi-Fi"),
        ("plan printer troubleshooting ", "Printer"),
        ("troubleshoot printer ", "Printer"),
        ("plan audio troubleshooting ", "Audio"),
        ("troubleshoot audio ", "Audio"),
        ("plan microphone troubleshooting ", "Microphone"),
        ("troubleshoot microphone ", "Microphone"),
        ("plan camera troubleshooting ", "Camera"),
        ("plan webcam troubleshooting ", "Camera"),
        ("troubleshoot camera ", "Camera"),
        ("troubleshoot webcam ", "Camera"),
        ("plan bluetooth troubleshooting ", "Bluetooth"),
        ("troubleshoot bluetooth ", "Bluetooth"),
        ("plan usb troubleshooting ", "USB device"),
        ("plan usb device troubleshooting ", "USB device"),
        ("troubleshoot usb ", "USB device"),
        ("plan display troubleshooting ", "Display"),
        ("plan monitor troubleshooting ", "Display"),
        ("troubleshoot display ", "Display"),
        ("troubleshoot monitor ", "Display"),
        ("plan windows update troubleshooting ", "Windows Update"),
        ("troubleshoot windows update ", "Windows Update"),
        ("plan app crash troubleshooting ", "App crash"),
        ("troubleshoot app crash ", "App crash"),
        ("plan startup cleanup ", "Startup cleanup"),
        ("troubleshoot startup ", "Startup cleanup"),
        ("plan browser troubleshooting ", "Browser"),
        ("troubleshoot browser ", "Browser"),
        ("plan onedrive troubleshooting ", "OneDrive sync"),
        ("plan onedrive sync troubleshooting ", "OneDrive sync"),
        ("troubleshoot onedrive ", "OneDrive sync"),
        ("plan backup strategy ", "Backup"),
        ("plan backup troubleshooting ", "Backup"),
        ("plan driver troubleshooting ", "Driver"),
        ("troubleshoot driver ", "Driver"),
        ("plan suspicious activity check ", "Suspicious activity"),
        ("troubleshoot suspicious activity ", "Suspicious activity"),
        ("plan remote support handoff ", "Remote support handoff")
    ];

    private static readonly string[] ToolIntegrationStatusRequests =
    [
        "show visual studio integration",
        "show visual studio status",
        "show tool integration status",
        "visual studio integration status",
        "visual studio status",
        "show coding tool status",
        "show coding tools",
        "coding tool status",
        "coding tools status",
        "show programming tools",
        "programming tool status"
    ];

    private static readonly string[] VisualStudioHandoffRequests =
    [
        "generate visual studio handoff",
        "generate visual studio integration handoff",
        "generate visual studio integration plan",
        "generate vs integration plan",
        "plan visual studio integration",
        "plan vs integration",
        "show visual studio handoff",
        "show visual studio integration plan",
        "visual studio handoff",
        "visual studio integration plan",
        "vs integration plan"
    ];


    private static readonly string[] ValidationQueueRunnerRequests =
    [
        "show validation queue runner",
        "validation queue runner",
        "show queued validation runner",
        "one click validation queue",
        "show one click validation queue"
    ];

    private static readonly string[] MandatorySymbolDiffAuditRequests =
    [
        "show mandatory symbol diff audit",
        "symbol diff audit",
        "show symbol diff enforcement",
        "before after symbol diff audit"
    ];

    private static readonly string[] MultiFileRefactorPlanPrefixes =
    [
        "multi file refactor plan ",
        "plan multi file refactor ",
        "plan multifile refactor ",
        "coordinated refactor plan "
    ];

    private static readonly string[] TestFailurePatchLoopRequests =
    [
        "show test failure patch loop",
        "test failure patch loop",
        "show failing test repair loop",
        "test to patch loop"
    ];

    private static readonly string[] BuildErrorTriageRequests =
    [
        "show build error triage",
        "build error triage",
        "build error auto triage",
        "group build errors"
    ];

    private static readonly string[] CodebaseMemoryIndexRequests =
    [
        "show codebase memory index",
        "codebase memory index",
        "show coding memory index",
        "project memory index"
    ];

    private static readonly string[] CodingNextBestActionRequests =
    [
        "show coding next best action",
        "coding next best action",
        "show next best coding action",
        "next best coding action"
    ];

    private static readonly string[] OwnerSafePatchBatchRequests =
    [
        "show owner safe patch batch",
        "owner safe patch batch",
        "show patch batch approvals",
        "patch batch approvals"
    ];

    private static readonly string[] GeneratedFileGuardRequests =
    [
        "show generated file guard",
        "generated file guard",
        "show designer file guard",
        "generated designer guard"
    ];

    private static readonly string[] MiniCodexReadinessReportRequests =
    [
        "mini codex readiness report",
        "show mini codex readiness report",
        "mini codex report card",
        "show coding report card"
    ];

    private static readonly string[] FeatureIntentPacketPrefixes =
    [
        "feature intent packet ",
        "build feature intent ",
        "plan feature intent ",
        "intent packet "
    ];

    private static readonly string[] BehaviorTestPlanPrefixes =
    [
        "behavior test plan ",
        "plan behavior tests ",
        "feature test plan ",
        "plan feature tests "
    ];

    private static readonly string[] BehaviorTestPatchPreviewPrefixes =
    [
        "preview behavior test patch ",
        "preview behavior test ",
        "preview test patch ",
        "synthesize behavior test ",
        "synthesize test patch ",
        "draft behavior test patch "
    ];

    private static readonly string[] BehaviorTestPatchPreviewRequests =
    [
        "preview behavior test patch",
        "preview behavior test",
        "preview test patch",
        "synthesize behavior test",
        "synthesize test patch",
        "draft behavior test patch"
    ];

    private static readonly string[] ImplementationSlicePlanPrefixes =
    [
        "implementation slice plan ",
        "plan implementation slices ",
        "feature implementation slices ",
        "slice feature "
    ];

    private static readonly string[] TestStubGeneratorPlanPrefixes =
    [
        "test stub generator plan ",
        "plan test stubs ",
        "preview test stubs ",
        "test stub plan "
    ];

    private static readonly string[] PatchBundleBuilderRequests =
    [
        "show patch bundle builder",
        "patch bundle builder",
        "multi file patch bundle builder",
        "multi-file patch bundle builder"
    ];

    private static readonly string[] FailureLoopStateRequests =
    [
        "show failure loop state",
        "failure loop state",
        "repair loop state",
        "show repair loop state"
    ];

    private static readonly string[] StopConditionDetectorRequests =
    [
        "show stop condition detector",
        "stop condition detector",
        "when should ali stop editing",
        "show coding stop conditions"
    ];

    private static readonly string[] SliceRiskScoringRequests =
    [
        "show slice risk scoring",
        "slice risk scoring",
        "risk score implementation slices",
        "show implementation risk scores"
    ];

    private static readonly string[] FeatureCompletionReceiptRequests =
    [
        "feature completion receipt",
        "show feature completion receipt",
        "completion receipt",
        "show completion receipt"
    ];

    private static readonly string[] FeatureExecutionPacketPrefixes =
    [
        "feature execution packet ",
        "show feature execution packet ",
        "build feature execution packet ",
        "feature safe execution packet ",
        "plain request execution packet "
    ];

    private static readonly string[] FeatureExecutionPacketRequests =
    [
        "feature execution packet",
        "show feature execution packet",
        "build feature execution packet",
        "feature safe execution packet",
        "plain request execution packet"
    ];

    private static readonly string[] FeatureWorkContextPrefixes =
    [
        "feature work context ",
        "show feature work context ",
        "build feature work context "
    ];

    private static readonly string[] FeatureWorkContextRequests =
    [
        "feature work context",
        "show feature work context",
        "build feature work context"
    ];

    private static readonly string[] BehaviorContractPrefixes =
    [
        "behavior contract ",
        "show behavior contract ",
        "feature behavior contract ",
        "build behavior contract "
    ];

    private static readonly string[] BehaviorContractRequests =
    [
        "behavior contract",
        "show behavior contract",
        "feature behavior contract",
        "build behavior contract"
    ];

    private static readonly string[] PatchSlicePlanPrefixes =
    [
        "patch slice plan ",
        "show patch slice plan ",
        "feature patch slices ",
        "plan patch slices "
    ];

    private static readonly string[] PatchSlicePlanRequests =
    [
        "patch slice plan",
        "show patch slice plan",
        "feature patch slices",
        "plan patch slices"
    ];

    private static readonly string[] ApplyGatePrefixes =
    [
        "apply gate ",
        "show apply gate ",
        "feature apply gate ",
        "patch apply gate "
    ];

    private static readonly string[] ApplyGateRequests =
    [
        "apply gate",
        "show apply gate",
        "feature apply gate",
        "patch apply gate"
    ];

    private static readonly string[] FeaturePatchDraftPlanPrefixes =
    [
        "feature patch draft ",
        "show feature patch draft ",
        "patch draft plan ",
        "draft feature patch ",
        "build patch draft ",
        "plain english patch draft "
    ];

    private static readonly string[] FeaturePatchDraftPlanRequests =
    [
        "feature patch draft",
        "show feature patch draft",
        "patch draft plan",
        "draft feature patch",
        "build patch draft",
        "plain english patch draft"
    ];

    private static readonly string[] ExactPatchSynthesisPrefixes =
    [
        "exact patch synthesis ",
        "synthesize exact patch ",
        "synthesize patch ",
        "exact feature patch ",
        "draft exact patch ",
        "make exact patch "
    ];

    private static readonly string[] ExactPatchSynthesisRequests =
    [
        "exact patch synthesis",
        "synthesize exact patch",
        "synthesize patch",
        "exact feature patch",
        "draft exact patch",
        "make exact patch"
    ];

    private static readonly string[] PreviewSynthesizedFeaturePatchPrefixes =
    [
        "preview synthesized feature patch ",
        "preview synthesized patch ",
        "preview exact feature patch ",
        "preview exact patch ",
        "preview generated patch "
    ];

    private static readonly string[] PreviewSynthesizedFeaturePatchRequests =
    [
        "preview synthesized feature patch",
        "preview synthesized patch",
        "preview exact feature patch",
        "preview exact patch",
        "preview generated patch"
    ];

    private static readonly string[] GuidedFeatureBundlePreviewPrefixes =
    [
        "preview guided feature bundle ",
        "preview feature bundle ",
        "preview paired feature patch ",
        "preview code and test patch ",
        "preview code plus test ",
        "paired preview "
    ];

    private static readonly string[] GuidedFeatureBundlePreviewRequests =
    [
        "preview guided feature bundle",
        "preview feature bundle",
        "preview paired feature patch",
        "preview code and test patch",
        "preview code plus test",
        "paired preview"
    ];

    private static readonly string[] AutonomousPatchLoopPrefixes =
    [
        "autonomous patch loop ",
        "show autonomous patch loop ",
        "feature patch loop ",
        "show feature patch loop ",
        "feature build loop ",
        "show feature build loop "
    ];

    private static readonly string[] AutonomousPatchLoopRequests =
    [
        "autonomous patch loop",
        "show autonomous patch loop",
        "feature patch loop",
        "show feature patch loop",
        "feature build loop",
        "show feature build loop"
    ];

    private static readonly string[] FeatureSessionLedgerPrefixes =
    [
        "feature session ledger ",
        "show feature session ledger ",
        "coding feature ledger ",
        "feature checkpoint ",
        "show feature checkpoint ",
        "build checkpoint ",
        "show build checkpoint "
    ];

    private static readonly string[] FeatureSessionLedgerRequests =
    [
        "feature session ledger",
        "show feature session ledger",
        "coding feature ledger",
        "feature checkpoint",
        "show feature checkpoint",
        "build checkpoint",
        "show build checkpoint"
    ];

    private static readonly string[] ValidationRepairRunnerPrefixes =
    [
        "validation repair runner ",
        "show validation repair runner ",
        "repair validation failure ",
        "repair latest validation failure ",
        "repair latest build failure ",
        "build repair runner ",
        "test repair runner "
    ];

    private static readonly string[] ValidationRepairRunnerRequests =
    [
        "validation repair runner",
        "show validation repair runner",
        "repair validation failure",
        "repair latest validation failure",
        "repair latest build failure",
        "build repair runner",
        "test repair runner",
        "run repair runner"
    ];

    private static readonly string[] FeatureRunControllerPrefixes =
    [
        "feature run controller ",
        "show feature run controller ",
        "feature run state ",
        "show feature run state ",
        "coding run controller ",
        "coding run state ",
        "mini codex run state ",
        "current feature state "
    ];

    private static readonly string[] FeatureRunControllerRequests =
    [
        "feature run controller",
        "show feature run controller",
        "feature run state",
        "show feature run state",
        "coding run controller",
        "coding run state",
        "mini codex run state",
        "current feature state"
    ];

    private static readonly string[] PostPatchValidationRouterPrefixes =
    [
        "post patch validation ",
        "post patch validation router ",
        "show post patch validation ",
        "validation router "
    ];

    private static readonly string[] PostPatchValidationRouterRequests =
    [
        "post patch validation",
        "post patch validation router",
        "show post patch validation",
        "validation router"
    ];

    private static readonly string[] PatchPreviewIntelligencePrefixes =
    [
        "patch intelligence ",
        "show patch intelligence ",
        "patch preview intelligence ",
        "show patch preview intelligence ",
        "feature patch intelligence "
    ];

    private static readonly string[] PatchPreviewIntelligenceRequests =
    [
        "patch intelligence",
        "show patch intelligence",
        "patch preview intelligence",
        "show patch preview intelligence",
        "feature patch intelligence"
    ];

    private static readonly string[] GuidedFeatureWorkflowPrefixes =
    [
        "guided feature workflow ",
        "feature build workflow ",
        "build feature workflow ",
        "start feature build ",
        "start guided build ",
        "tell ali to build ",
        "build this feature "
    ];

    private static readonly string[] GuidedFeatureWorkflowRequests =
    [
        "guided feature workflow",
        "feature build workflow",
        "build feature workflow",
        "start feature build",
        "start guided build",
        "tell ali to build",
        "build this feature"
    ];

    private static readonly string[] FeatureImplementationPlannerPrefixes =
    [
        "feature implementation planner ",
        "implementation planner ",
        "multi file implementation planner ",
        "multi-file implementation planner ",
        "feature implementation plan ",
        "multi file feature plan ",
        "multi-file feature plan "
    ];

    private static readonly string[] FeatureImplementationPlannerRequests =
    [
        "feature implementation planner",
        "implementation planner",
        "multi file implementation planner",
        "multi-file implementation planner",
        "feature implementation plan",
        "multi file feature plan",
        "multi-file feature plan"
    ];

    private static readonly string[] FeatureIntakeNormalizerPrefixes =
    [
        "feature intake ",
        "feature intake normalizer ",
        "normalize feature request ",
        "clarify feature request ",
        "build request intake ",
        "request intake "
    ];

    private static readonly string[] FeatureIntakeNormalizerRequests =
    [
        "feature intake",
        "feature intake normalizer",
        "normalize feature request",
        "clarify feature request",
        "build request intake",
        "request intake"
    ];

    private static readonly string[] AutonomousFeatureOrchestratorPrefixes =
    [
        "autonomous feature orchestrator ",
        "mini codex orchestrator ",
        "feature orchestrator ",
        "feature autopilot ",
        "feature control tower ",
        "build orchestration "
    ];

    private static readonly string[] AutonomousFeatureOrchestratorRequests =
    [
        "autonomous feature orchestrator",
        "mini codex orchestrator",
        "feature orchestrator",
        "feature autopilot",
        "feature control tower",
        "build orchestration"
    ];

    private static readonly string[] ImplementationEvidencePackPrefixes =
    [
        "implementation evidence pack ",
        "feature evidence pack ",
        "coding evidence pack ",
        "closeout evidence pack ",
        "release evidence pack ",
        "evidence pack "
    ];

    private static readonly string[] ImplementationEvidencePackRequests =
    [
        "implementation evidence pack",
        "feature evidence pack",
        "coding evidence pack",
        "closeout evidence pack",
        "release evidence pack",
        "evidence pack"
    ];

    private static readonly string[] BuildThisFeaturePrefixes =
    [
        "build this for me ",
        "build this feature ",
        "mini codex build ",
        "autonomous build ",
        "drive the build ",
        "build it for me "
    ];

    private static readonly string[] BuildThisFeatureRequests =
    [
        "build this for me",
        "build this feature",
        "mini codex build",
        "autonomous build",
        "drive the build",
        "build it for me"
    ];

    private static readonly string[] RoslynEditPlannerV2Prefixes =
    [
        "roslyn edit planner ",
        "roslyn edit planner v2 ",
        "symbol edit planner ",
        "targeted edit planner ",
        "semantic edit planner v2 "
    ];

    private static readonly string[] RoslynEditPlannerV2Requests =
    [
        "roslyn edit planner",
        "roslyn edit planner v2",
        "symbol edit planner",
        "targeted edit planner",
        "semantic edit planner v2"
    ];

    private static readonly string[] MultiFilePatchSynthesisV2Prefixes =
    [
        "multi file patch synthesis ",
        "multi-file patch synthesis ",
        "patch synthesis v2 ",
        "multi file patch synthesis v2 ",
        "multi-file patch synthesis v2 ",
        "source viewmodel xaml patch "
    ];

    private static readonly string[] MultiFilePatchSynthesisV2Requests =
    [
        "multi file patch synthesis",
        "multi-file patch synthesis",
        "patch synthesis v2",
        "multi file patch synthesis v2",
        "multi-file patch synthesis v2",
        "source viewmodel xaml patch"
    ];

    private static readonly string[] PatternCopyPlanPrefixes =
    [
        "pattern copy ",
        "pattern copier ",
        "copy nearby pattern ",
        "mirror existing pattern ",
        "pattern copy plan "
    ];

    private static readonly string[] PatternCopyPlanRequests =
    [
        "pattern copy",
        "pattern copier",
        "copy nearby pattern",
        "mirror existing pattern",
        "pattern copy plan"
    ];

    private static readonly string[] BehaviorTestGeneratorV2Prefixes =
    [
        "behavior test generator ",
        "behavior test generator v2 ",
        "generate behavior test ",
        "test generator v2 "
    ];

    private static readonly string[] BehaviorTestGeneratorV2Requests =
    [
        "behavior test generator",
        "behavior test generator v2",
        "generate behavior test",
        "test generator v2"
    ];

    private static readonly string[] ImplementationSliceStatePrefixes =
    [
        "implementation slice state ",
        "slice state ",
        "feature slice state ",
        "slice tracker ",
        "implementation tracker "
    ];

    private static readonly string[] ImplementationSliceStateRequests =
    [
        "implementation slice state",
        "slice state",
        "feature slice state",
        "slice tracker",
        "implementation tracker"
    ];

    private static readonly string[] PostApplyRepairLoopV2Prefixes =
    [
        "post apply repair loop ",
        "post-apply repair loop ",
        "repair loop v2 ",
        "post apply repair loop v2 ",
        "after apply repair "
    ];

    private static readonly string[] PostApplyRepairLoopV2Requests =
    [
        "post apply repair loop",
        "post-apply repair loop",
        "repair loop v2",
        "post apply repair loop v2",
        "after apply repair"
    ];

    private static readonly string[] SemanticDiffSummaryPrefixes =
    [
        "semantic diff summary ",
        "semantic diff ",
        "summarize semantic diff ",
        "explain code changes ",
        "symbol diff summary "
    ];

    private static readonly string[] SemanticDiffSummaryRequests =
    [
        "semantic diff summary",
        "semantic diff",
        "summarize semantic diff",
        "explain code changes",
        "symbol diff summary"
    ];

    private static readonly string[] MiniCodexScoreV3Prefixes =
    [
        "mini codex score v3 ",
        "readiness score v3 ",
        "score v3 ",
        "mini codex percentages ",
        "coding percentages "
    ];

    private static readonly string[] MiniCodexScoreV3Requests =
    [
        "mini codex score v3",
        "readiness score v3",
        "score v3",
        "mini codex percentages",
        "coding percentages"
    ];

    private static readonly string[] ConcretePatchAuthoringPrefixes =
    [
        "concrete patch authoring ",
        "patch authoring ",
        "author concrete patch ",
        "draft concrete patch ",
        "implementation patch authoring "
    ];

    private static readonly string[] ConcretePatchAuthoringRequests =
    [
        "concrete patch authoring",
        "patch authoring",
        "author concrete patch",
        "draft concrete patch",
        "implementation patch authoring"
    ];

    private static readonly string[] PatchBodyGeneratorPrefixes =
    [
        "patch body generator ",
        "generate patch body ",
        "patch body ",
        "draft patch body ",
        "exact patch body "
    ];

    private static readonly string[] PatchBodyGeneratorRequests =
    [
        "patch body generator",
        "generate patch body",
        "patch body",
        "draft patch body",
        "exact patch body"
    ];

    private static readonly string[] PatternCommandScaffolderPrefixes =
    [
        "pattern command scaffolder ",
        "command scaffolder ",
        "scaffold command pattern ",
        "scaffold parser service dashboard test ",
        "parser service dashboard test scaffold "
    ];

    private static readonly string[] PatternCommandScaffolderRequests =
    [
        "pattern command scaffolder",
        "command scaffolder",
        "scaffold command pattern",
        "scaffold parser service dashboard test",
        "parser service dashboard test scaffold"
    ];

    private static readonly string[] UiBundlePlannerPrefixes =
    [
        "ui bundle planner ",
        "source viewmodel xaml bundle ",
        "viewmodel xaml bundle ",
        "dashboard bundle planner ",
        "ui patch bundle "
    ];

    private static readonly string[] UiBundlePlannerRequests =
    [
        "ui bundle planner",
        "source viewmodel xaml bundle",
        "viewmodel xaml bundle",
        "dashboard bundle planner",
        "ui patch bundle"
    ];

    private static readonly string[] PatchConfidenceScorePrefixes =
    [
        "patch confidence score ",
        "score patch confidence ",
        "patch confidence ",
        "edit confidence score ",
        "confidence score "
    ];

    private static readonly string[] PatchConfidenceScoreRequests =
    [
        "patch confidence score",
        "score patch confidence",
        "patch confidence",
        "edit confidence score",
        "confidence score"
    ];

    private static readonly string[] SliceExecutorPreviewPrefixes =
    [
        "slice executor preview ",
        "slice preview ",
        "preview implementation slice ",
        "slice 1 preview ",
        "slice apply preview "
    ];

    private static readonly string[] SliceExecutorPreviewRequests =
    [
        "slice executor preview",
        "slice preview",
        "preview implementation slice",
        "slice 1 preview",
        "slice apply preview"
    ];

    private static readonly string[] FailureToPatchV3Prefixes =
    [
        "failure to patch v3 ",
        "failure-to-patch v3 ",
        "failure to patch ",
        "build failure to patch ",
        "test failure to patch "
    ];

    private static readonly string[] FailureToPatchV3Requests =
    [
        "failure to patch v3",
        "failure-to-patch v3",
        "failure to patch",
        "build failure to patch",
        "test failure to patch"
    ];

    private static readonly string[] SemanticChangeReceiptPrefixes =
    [
        "semantic change receipt ",
        "change receipt ",
        "human change receipt ",
        "implementation receipt ",
        "semantic receipt "
    ];

    private static readonly string[] SemanticChangeReceiptRequests =
    [
        "semantic change receipt",
        "change receipt",
        "human change receipt",
        "implementation receipt",
        "semantic receipt"
    ];

    private static readonly string[] ValidationChainPlannerPrefixes =
    [
        "validation chain planner ",
        "validation chain ",
        "build test chain ",
        "post patch validation chain ",
        "validation order planner "
    ];

    private static readonly string[] ValidationChainPlannerRequests =
    [
        "validation chain planner",
        "validation chain",
        "build test chain",
        "post patch validation chain",
        "validation order planner"
    ];

    private static readonly string[] DataSystemsGuidePrefixes =
    [
        "data systems guide",
        "show data systems guide",
        "data structure guide",
        "data structures guide",
        "database design guide",
        "sql design guide",
        "fast sql guide",
        "service design guide"
    ];

    private static readonly string[] DataSystemsGuideRequests =
    [
        "data systems guide",
        "show data systems guide",
        "data structure guide",
        "data structures guide",
        "database design guide",
        "sql design guide",
        "fast sql guide",
        "service design guide"
    ];

    private static readonly string[] ConsoleCodingGuidePrefixes =
    [
        "console app guide",
        "console coding guide",
        "console program guide",
        "console guide",
        "cli app guide",
        "command line app guide"
    ];

    private static readonly string[] ConsoleCodingGuideRequests =
    [
        "console app guide",
        "console coding guide",
        "console program guide",
        "console guide",
        "cli app guide",
        "command line app guide"
    ];

    private static readonly string[] WpfCodingGuidePrefixes =
    [
        "wpf app guide",
        "wpf coding guide",
        "wpf guide",
        "xaml guide",
        "mvvm guide",
        "desktop ui guide"
    ];

    private static readonly string[] WpfCodingGuideRequests =
    [
        "wpf app guide",
        "wpf coding guide",
        "wpf guide",
        "xaml guide",
        "mvvm guide",
        "desktop ui guide"
    ];

    private static readonly string[] ActiveWorkspaceProjectPrefixes =
    [
        "active workspace project ",
        "active coding workspace ",
        "current coding workspace ",
        "current workspace project ",
        "which project are we working on "
    ];

    private static readonly string[] ActiveWorkspaceProjectRequests =
    [
        "active workspace project",
        "active coding workspace",
        "current coding workspace",
        "current workspace project",
        "which project are we working on",
        "how do you know the current project"
    ];

    private static readonly string[] ProjectControlCenterPrefixes =
    [
        "project control center ",
        "current project control center ",
        "coding project control center ",
        "project control packet ",
        "current project packet "
    ];

    private static readonly string[] ProjectControlCenterRequests =
    [
        "project control center",
        "current project control center",
        "coding project control center",
        "project control packet",
        "current project packet"
    ];

    private static readonly string[] CurrentProjectMemoryPrefixes =
    [
        "project memory ",
        "show project memory ",
        "current project memory ",
        "show current project memory "
    ];

    private static readonly string[] CurrentProjectMemoryRequests =
    [
        "project memory",
        "show project memory",
        "current project memory",
        "show current project memory"
    ];

    private static readonly string[] SaveCurrentProjectMemoryPrefixes =
    [
        "remember for this project ",
        "remember for current project ",
        "save project memory ",
        "save current project memory ",
        "project remember "
    ];

    private static readonly string[] OpenCurrentProjectFolderRequests =
    [
        "open current project folder",
        "open project folder",
        "open selected project folder",
        "open current coding folder"
    ];

    private static readonly string[] OwnerApprovedApplyPacketPrefixes =
    [
        "owner approved apply packet ",
        "apply packet ",
        "owner apply packet ",
        "review apply packet ",
        "safe apply packet "
    ];

    private static readonly string[] OwnerApprovedApplyPacketRequests =
    [
        "owner approved apply packet",
        "apply packet",
        "owner apply packet",
        "review apply packet",
        "safe apply packet"
    ];

    private static readonly string[] RoslynInsertionPlannerPrefixes =
    [
        "roslyn insertion planner ",
        "insertion planner ",
        "method insertion planner ",
        "property insertion planner ",
        "class insertion planner "
    ];

    private static readonly string[] RoslynInsertionPlannerRequests =
    [
        "roslyn insertion planner",
        "insertion planner",
        "method insertion planner",
        "property insertion planner",
        "class insertion planner"
    ];

    private static readonly string[] IntentDiffComposerPrefixes =
    [
        "intent diff composer ",
        "intent to diff ",
        "compose intent diff ",
        "multi file intent diff ",
        "feature diff composer "
    ];

    private static readonly string[] IntentDiffComposerRequests =
    [
        "intent diff composer",
        "intent to diff",
        "compose intent diff",
        "multi file intent diff",
        "feature diff composer"
    ];

    private static readonly string[] BehaviorSpecTestScaffoldPrefixes =
    [
        "behavior spec test scaffold ",
        "behavior spec tests ",
        "spec test scaffold ",
        "acceptance test scaffold ",
        "test scaffold from behavior "
    ];

    private static readonly string[] BehaviorSpecTestScaffoldRequests =
    [
        "behavior spec test scaffold",
        "behavior spec tests",
        "spec test scaffold",
        "acceptance test scaffold",
        "test scaffold from behavior"
    ];

    private static readonly string[] RepeatFailureMemoryPrefixes =
    [
        "repeat failure memory ",
        "recurring failure memory ",
        "failure memory ",
        "remembered failures ",
        "build failure memory "
    ];

    private static readonly string[] RepeatFailureMemoryRequests =
    [
        "repeat failure memory",
        "recurring failure memory",
        "failure memory",
        "remembered failures",
        "build failure memory"
    ];

    private static readonly string[] FirstDiagnosticRepairRoutePrefixes =
    [
        "first diagnostic repair route ",
        "diagnostic repair route ",
        "first diagnostic route ",
        "route first diagnostic ",
        "repair route from diagnostic "
    ];

    private static readonly string[] FirstDiagnosticRepairRouteRequests =
    [
        "first diagnostic repair route",
        "diagnostic repair route",
        "first diagnostic route",
        "route first diagnostic",
        "repair route from diagnostic"
    ];

    private static readonly string[] ValidationCommandMinimizerPrefixes =
    [
        "validation command minimizer ",
        "minimize validation command ",
        "smallest validation command ",
        "targeted validation minimizer ",
        "validation minimizer "
    ];

    private static readonly string[] ValidationCommandMinimizerRequests =
    [
        "validation command minimizer",
        "minimize validation command",
        "smallest validation command",
        "targeted validation minimizer",
        "validation minimizer"
    ];

    private static readonly string[] UiBindingRepairPlannerPrefixes =
    [
        "ui binding repair planner ",
        "binding repair planner ",
        "xaml binding repair ",
        "command binding repair ",
        "ui command binding repair "
    ];

    private static readonly string[] UiBindingRepairPlannerRequests =
    [
        "ui binding repair planner",
        "binding repair planner",
        "xaml binding repair",
        "command binding repair",
        "ui command binding repair"
    ];

    private static readonly string[] AuthoringSequenceFlowPrefixes =
    [
        "authoring sequence flow ",
        "patch authoring sequence ",
        "coding authoring flow ",
        "mini codex sequence ",
        "apply packet sequence "
    ];

    private static readonly string[] AuthoringSequenceFlowRequests =
    [
        "authoring sequence flow",
        "patch authoring sequence",
        "coding authoring flow",
        "mini codex sequence",
        "apply packet sequence"
    ];

    private static readonly string[] PlainEnglishCodingCapabilityCardPrefixes =
    [
        "coding capability card ",
        "plain english coding capability ",
        "what can you do for coding ",
        "explain coding abilities ",
        "mini codex capability card "
    ];

    private static readonly string[] PlainEnglishCodingCapabilityCardRequests =
    [
        "coding capability card",
        "plain english coding capability",
        "what can you do for coding",
        "explain coding abilities",
        "mini codex capability card"
    ];

    private static readonly string[] PlainEnglishFeatureBuilderPrefixes =
    [
        "feature builder ",
        "show feature builder ",
        "plain english feature builder ",
        "guided feature builder ",
        "coding feature builder ",
        "build request packet ",
        "feature build packet "
    ];

    private static readonly string[] PlainEnglishFeatureBuilderRequests =
    [
        "feature builder",
        "show feature builder",
        "plain english feature builder",
        "guided feature builder",
        "coding feature builder",
        "build request packet",
        "feature build packet"
    ];

    private static readonly string[] PlainEnglishBuildRequestPrefixes =
    [
        "help me build ",
        "i want to build ",
        "i need to build ",
        "build me ",
        "implement ",
        "add feature ",
        "add a feature ",
        "code up "
    ];

    private static readonly string[] CodingBuildTerms =
    [
        "app",
        "program",
        "code",
        "feature",
        "button",
        "dashboard",
        "setting",
        "settings",
        "toggle",
        "command",
        "screen",
        "window",
        "page",
        "workflow",
        "tool",
        "api",
        "service",
        "class",
        "test",
        "parser",
        "installer",
        "voice",
        "source"
    ];

    private static readonly string[] BuildFeatureLaneRequests =
    [
        "show build feature lane",
        "build feature lane",
        "feature build lane",
        "show feature build lane"
    ];
    private static readonly string[] ApplyLastPatchPreviewRequests =
    [
        "apply last patch preview",
        "apply the last patch preview",
        "apply patch preview",
        "apply the patch preview",
        "apply last preview",
        "apply the last preview",
        "apply preview",
        "apply the preview"
    ];

    private static readonly string[] ShowLastPatchPreviewRequests =
    [
        "show last patch preview",
        "show the last patch preview",
        "show pending patch preview",
        "show the pending patch preview",
        "show pending patch",
        "show the pending patch",
        "what patch is pending",
        "what is the pending patch"
    ];

    private static readonly string[] DiscardLastPatchPreviewRequests =
    [
        "discard last patch preview",
        "discard the last patch preview",
        "discard pending patch preview",
        "discard the pending patch preview",
        "clear last patch preview",
        "clear the last patch preview",
        "clear pending patch",
        "clear the pending patch"
    ];

    private static readonly string[] ShowLastRoadmapRequests =
    [
        "show last roadmap",
        "show the last roadmap",
        "show pending roadmap",
        "show the pending roadmap",
        "show approved roadmap",
        "what roadmap is pending",
        "what is the pending roadmap"
    ];

    private static readonly string[] DiscardLastRoadmapRequests =
    [
        "discard last roadmap",
        "discard the last roadmap",
        "discard pending roadmap",
        "discard the pending roadmap",
        "clear last roadmap",
        "clear pending roadmap"
    ];

    private static readonly string[] ApproveLastRoadmapRequests =
    [
        "approve last roadmap",
        "approve the last roadmap",
        "approve pending roadmap",
        "approve the pending roadmap",
        "approve roadmap",
        "accept last roadmap",
        "accept pending roadmap"
    ];

    private static readonly string[] StartApprovedRoadmapRequests =
    [
        "start approved roadmap",
        "start the approved roadmap",
        "begin approved roadmap",
        "begin the approved roadmap",
        "execute approved roadmap",
        "execute the approved roadmap",
        "start roadmap execution",
        "begin roadmap execution",
        "lets do the approved roadmap",
        "let's do the approved roadmap"
    ];

    private static readonly string[] ShowActiveRoadmapStepRequests =
    [
        "show active roadmap step",
        "show current roadmap step",
        "show roadmap step",
        "where are we in the roadmap",
        "roadmap status"
    ];

    private static readonly string[] ShowNextRoadmapActionRequests =
    [
        "show next roadmap action",
        "show next coding action",
        "next roadmap action",
        "next coding action",
        "what should ali do next",
        "what should we do next",
        "what is the next coding action",
        "what is the next roadmap action"
    ];

    private static readonly string[] ShowRoadmapExecutionPacketRequests =
    [
        "show execution packet",
        "show coding execution packet",
        "show roadmap execution packet",
        "generate execution packet",
        "generate coding execution packet",
        "prepare execution packet",
        "prepare next step packet",
        "package next coding step",
        "build execution packet"
    ];

    private static readonly string[] ApproveRoadmapExecutionPacketRequests =
    [
        "approve execution packet",
        "approve coding execution packet",
        "approve roadmap execution packet",
        "approve step packet",
        "approve current packet",
        "approve last packet"
    ];

    private static readonly string[] ShowApprovedRoadmapExecutionPacketRequests =
    [
        "show approved packet",
        "show approved execution packet",
        "show approved coding execution packet",
        "show active packet",
        "show active execution packet"
    ];

    private static readonly string[] DiscardApprovedRoadmapExecutionPacketRequests =
    [
        "discard approved packet",
        "discard approved execution packet",
        "discard active packet",
        "clear approved packet",
        "clear active packet"
    ];

    private static readonly string[] ShowRoadmapExecutionPacketProgressRequests =
    [
        "show packet progress",
        "show execution packet progress",
        "show approved packet progress",
        "packet progress",
        "execution packet progress"
    ];

    private static readonly string[] ShowApprovedPacketCommandsRequests =
    [
        "show packet commands",
        "show approved packet commands",
        "list packet commands",
        "list approved packet commands",
        "show packet console",
        "packet console"
    ];

    private static readonly string[] ShowPacketRunLedgerRequests =
    [
        "show packet ledger",
        "show packet run ledger",
        "packet ledger",
        "packet run ledger",
        "show execution ledger"
    ];

    private static readonly string[] RunApprovedPacketItemPrefixes =
    [
        "run packet item",
        "run approved packet item",
        "run packet command",
        "run approved packet command",
        "execute packet item",
        "execute approved packet item"
    ];

    private static readonly string[] PackageLookupPrefixes =
    [
        "plan package lookup",
        "plan library lookup",
        "lookup package candidates",
        "lookup library candidates",
        "suggest package candidates for",
        "suggest library candidates for",
        "show dependency risk cards",
        "dependency risk cards"
    ];

    private static readonly string[] BuildGoalInterpreterPrefixes =
    [
        "interpret build goal",
        "interpret coding goal",
        "understand build goal",
        "understand coding goal",
        "i want to build",
        "help me build",
        "what should i build for"
    ];

    private static readonly string[] ArchitectureOptionPrefixes =
    [
        "show architecture options",
        "compare architecture options",
        "architecture options for",
        "option cards for",
        "show option cards for",
        "compare build paths for"
    ];

    private static readonly string[] AcceptanceCriteriaPrefixes =
    [
        "write acceptance criteria",
        "draft acceptance criteria",
        "define done for",
        "done means",
        "acceptance checklist for"
    ];

    private static readonly string[] FeatureTestPrefixes =
    [
        "suggest tests for",
        "test plan for",
        "recommend tests for",
        "what tests for"
    ];

    private static readonly string[] CodebasePatternRequests =
    [
        "detect codebase patterns",
        "show codebase patterns",
        "inspect codebase patterns",
        "detect project patterns",
        "show project patterns"
    ];

    private static readonly string[] FeatureFilePlanPrefixes =
    [
        "plan feature files",
        "plan files for",
        "which files for",
        "new feature files for",
        "file plan for"
    ];

    private static readonly string[] RefactorSafetyPrefixes =
    [
        "show refactor safety checklist",
        "refactor safety checklist",
        "refactor safety for",
        "risk checklist for",
        "safety checklist for"
    ];

    private static readonly string[] DependencyInstallPacketPrefixes =
    [
        "plan dependency install packet",
        "dependency install packet",
        "package install packet",
        "plan package install",
        "plan dependency install"
    ];

    private static readonly string[] PostEditValidationRequests =
    [
        "validation plan",
        "show validation plan",
        "plan validation",
        "plan post edit validation",
        "post edit validation",
        "after edit validation",
        "what should i validate",
        "what should i test",
        "show validation after edits",
        "show post edit build loop"
    ];

    private static readonly string[] ProjectScaffoldPrefixes =
    [
        "preview project scaffold",
        "plan project scaffold",
        "preview scaffold",
        "plan scaffold",
        "scaffold project",
        "draft scaffold"
    ];

    private static readonly string[] ScaffoldApplyPrefixes =
    [
        "plan scaffold apply",
        "show scaffold apply flow",
        "scaffold apply flow",
        "apply scaffold plan",
        "scaffold apply"
    ];

    private static readonly string[] ResumeBuildPlanRequests =
    [
        "resume build plan",
        "resume last build plan",
        "resume approved packet",
        "resume packet",
        "recover and resume build",
        "recover and resume packet"
    ];

    private static readonly string[] GenerateMorningReportRequests =
    [
        "generate morning report",
        "generate morning build report",
        "create morning report",
        "export morning report",
        "write morning report"
    ];

    private static readonly string[] BuilderCommandIndexRequests =
    [
        "show coding skill command index",
        "show builder command index",
        "show programming powers",
        "show ali programming powers",
        "show coding powers",
        "coding skill command index",
        "builder command index"
    ];

    private static readonly string[] CodingSessionSummaryRequests =
    [
        "show coding session summary",
        "show session summary",
        "summarize coding session",
        "what changed this session",
        "morning session summary"
    ];

    private static readonly string[] StartCodingSessionPrefixes =
    [
        "start coding session ",
        "start coding task ",
        "start current coding task ",
        "begin coding session ",
        "begin coding task ",
        "launch coding session ",
        "launch coding task "
    ];

    private static readonly string[] CurrentCodingSessionRequests =
    [
        "current coding task",
        "show current coding task",
        "current coding session",
        "show current coding session",
        "show task panel",
        "task panel"
    ];

    private static readonly string[] ContinueCurrentCodingSessionRequests =
    [
        "continue current task",
        "continue current coding task",
        "continue coding task",
        "continue coding session",
        "continue current coding session",
        "resume current coding task",
        "resume coding session"
    ];

    private static readonly string[] ClearCurrentCodingSessionRequests =
    [
        "clear current coding task",
        "clear current coding session",
        "finish current coding session",
        "discard current coding session"
    ];

    private static readonly string[] CodingSessionHistoryRequests =
    [
        "coding session history",
        "show coding session history",
        "current coding session history",
        "show current coding session history",
        "task history",
        "show task history"
    ];

    private static readonly string[] ProjectCommandDefaultsRequests =
    [
        "project command defaults",
        "show project command defaults",
        "current project command defaults",
        "show current project command defaults"
    ];

    private static readonly string[] SaveProjectCommandDefaultsPrefixes =
    [
        "save project command defaults ",
        "save current project command defaults ",
        "set project command defaults ",
        "set current project command defaults "
    ];

    private static readonly string[] SaveProjectCommandDefaultsRequests =
    [
        "save project command defaults",
        "save current project command defaults",
        "set project command defaults",
        "set current project command defaults"
    ];

    private static readonly string[] WindowsTroubleshootingToolkitRequests =
    [
        "show windows troubleshooting toolkit",
        "show powershell troubleshooting toolkit",
        "show computer troubleshooting toolkit",
        "show powershell cookbook",
        "show cmd cookbook",
        "windows troubleshooting toolkit",
        "powershell troubleshooting toolkit",
        "computer troubleshooting toolkit"
    ];

    private static readonly string[] RogueProcessHuntPrefixes =
    [
        "plan rogue process hunt",
        "plan process hunt",
        "find rogue process",
        "hunt rogue process",
        "troubleshoot rogue process",
        "troubleshoot locked process",
        "find process locking",
        "find port owner"
    ];

    private static readonly string[] ProcessEvidencePrefixes =
    [
        "collect process evidence",
        "show process evidence",
        "can you look at the processes running",
        "look at the processes running",
        "look at running processes",
        "show running processes",
        "list running processes",
        "what processes are running",
        "inspect process",
        "inspect processes",
        "diagnose process",
        "diagnose processes"
    ];

    private static readonly string[] PortOwnerPrefixes =
    [
        "diagnose port",
        "find port",
        "find port owner",
        "who owns port",
        "what owns port",
        "show port owner"
    ];

    private static readonly string[] FileLockPrefixes =
    [
        "diagnose file lock",
        "find file lock",
        "find locking process",
        "who is locking",
        "what is locking",
        "diagnose locked file"
    ];

    private static readonly string[] ServicesStartupRequests =
    [
        "inspect services startup",
        "inspect services and startup",
        "show services startup",
        "show services and startup",
        "inspect startup entries",
        "show startup entries"
    ];

    private static readonly string[] EventLogTriageRequests =
    [
        "triage event logs",
        "triage windows event logs",
        "show event log triage",
        "show recent event errors",
        "inspect event logs",
        "inspect windows event logs"
    ];

    private static readonly string[] ProcessStopPlanPrefixes =
    [
        "plan process stop",
        "plan stop process",
        "preview process stop",
        "preview stop process"
    ];

    private static readonly string[] ProcessStopPrefixes =
    [
        "stop process",
        "stop pid",
        "taskkill pid"
    ];

    private static readonly string[] BuildLockDiagnosisRequests =
    [
        "diagnose build lock",
        "diagnose locked build",
        "diagnose build file lock",
        "recover build lock",
        "find build lock"
    ];

    private static readonly string[] ClassifyLastFailureRequests =
    [
        "classify last failure",
        "classify last build failure",
        "classify last dotnet failure",
        "classify build failure"
    ];

    private static readonly string[] RoadmapStepChecklistRequests =
    [
        "show roadmap step checklist",
        "show step acceptance checklist",
        "roadmap step checklist",
        "step acceptance checklist",
        "can we mark roadmap step complete"
    ];

    private static readonly string[] InstallDoctorRequests =
    [
        "show install doctor",
        "run install doctor",
        "install doctor",
        "diagnose install",
        "diagnose ali install",
        "check installation",
        "check ali installation"
    ];

    private static readonly string[] AdvanceRoadmapStepRequests =
    [
        "advance roadmap step",
        "advance the roadmap step",
        "mark roadmap step complete",
        "mark current roadmap step complete",
        "complete roadmap step",
        "complete current roadmap step",
        "next roadmap step"
    ];

    private static readonly string[] PauseRoadmapRequests =
    [
        "pause roadmap",
        "pause active roadmap",
        "pause roadmap execution"
    ];

    private static readonly string[] ResumeRoadmapRequests =
    [
        "resume roadmap",
        "resume active roadmap",
        "resume roadmap execution"
    ];

    private static readonly string[] FinishRoadmapRequests =
    [
        "finish roadmap",
        "finish active roadmap",
        "complete roadmap",
        "complete active roadmap"
    ];

    private static readonly string[] RecoverRoadmapStateRequests =
    [
        "recover roadmap",
        "recover roadmap state",
        "recover active roadmap",
        "restore roadmap state",
        "show recovered roadmap"
    ];

    private static readonly string[] DiagnoseRecoveryStateRequests =
    [
        "diagnose recovery state",
        "show recovery state",
        "show crash recovery",
        "show crash recovery status",
        "diagnose crash recovery",
        "diagnose interrupted build",
        "recover build state",
        "show recovery guidance"
    ];

    private static readonly string[] SearchPrefixes =
    [
        "search workspace for ",
        "search coding workspace for ",
        "search code for ",
        "find in workspace ",
        "find in coding workspace "
    ];

    private static readonly string[] InspectWorkspaceRequests =
    [
        "inspect workspace",
        "inspect coding workspace",
        "analyze workspace",
        "analyze coding workspace",
        "summarize workspace",
        "summarize coding workspace",
        "show project map",
        "show coding project map",
        "list solutions",
        "list projects"
    ];

    private static readonly string[] AnalyzeArchitectureRequests =
    [
        "analyze solution architecture",
        "analyze project architecture",
        "analyze coding architecture",
        "inspect solution architecture",
        "inspect project architecture",
        "show solution architecture",
        "show project architecture",
        "show architecture map",
        "architecture map",
        "solution architecture",
        "project architecture"
    ];

    private static readonly string[] ProjectIntelligenceRequests =
    [
        "project intelligence",
        "show project intelligence",
        "scan project intelligence",
        "repo intelligence",
        "show repo intelligence",
        "repository intelligence",
        "show repository intelligence",
        "coding intelligence scan",
        "programming intelligence",
        "show programming intelligence",
        "understand this project",
        "understand coding project",
        "summarize project intelligence"
    ];

    private static readonly string[] ProjectIndexRequests =
    [
        "project index",
        "show project index",
        "build project index",
        "rebuild project index",
        "coding project index",
        "persistent project index",
        "mini codex project index",
        "mini-codex project index"
    ];

    private static readonly string[] ProjectDependencyMapPrefixes =
    [
        "project dependency map",
        "show project dependency map",
        "dependency map",
        "show dependency map",
        "project reference map",
        "show project reference map"
    ];

    private static readonly string[] RepoUnderstandingRequests =
    [
        "understand repo",
        "understand this repo",
        "understand repository",
        "understand this repository",
        "understand project",
        "understand this project",
        "repo understanding",
        "repository understanding",
        "project understanding",
        "scan repo",
        "scan repository",
        "scan this repo"
    ];

    private static readonly string[] CodingContextPacketPrefixes =
    [
        "coding context packet",
        "show coding context packet",
        "build coding context packet",
        "prepare coding context",
        "prepare coding context packet",
        "mini codex context",
        "mini-codex context"
    ];

    private static readonly string[] SafeCommitRequests =
    [
        "can i safely commit",
        "safe to commit",
        "am i safe to commit",
        "is this safe to commit",
        "commit readiness",
        "show commit readiness",
        "check commit readiness",
        "pre commit check",
        "pre-commit check",
        "ready to commit"
    ];

    private static readonly string[] WorkspaceHealthScoreRequests =
    [
        "workspace health score",
        "show workspace health score",
        "coding health score",
        "show coding health score",
        "repo health score",
        "repository health score"
    ];

    private static readonly string[] FullCodingReadinessRequests =
    [
        "full coding readiness",
        "show full coding readiness",
        "run full coding readiness",
        "coding readiness",
        "full readiness",
        "mini codex readiness",
        "mini-codex readiness"
    ];

    private static readonly string[] MiniCodexStatusRequests =
    [
        "mini codex status",
        "mini-codex status",
        "show mini codex status",
        "show mini-codex status",
        "mini codex score",
        "mini-codex score",
        "show mini codex score",
        "show mini-codex score",
        "how close are we to codex",
        "how close are we to mini codex",
        "coding ability score",
        "programming ability score"
    ];

    private static readonly string[] ValidationLedgerRequests =
    [
        "show validation ledger",
        "validation ledger",
        "before after validation ledger",
        "before/after validation ledger",
        "show before after validation"
    ];

    private static readonly string[] CSharpSymbolIndexRequests =
    [
        "show csharp symbol index",
        "show c# symbol index",
        "csharp symbol index",
        "c# symbol index",
        "show code symbol index"
    ];

    private static readonly string[] OwnershipMapPrefixes =
    [
        "show ownership map",
        "ownership map",
        "code ownership map",
        "explain ownership",
        "who owns",
        "what owns",
        "where does"
    ];

    private static readonly string[] XamlBindingCheckRequests =
    [
        "xaml binding check",
        "verify xaml bindings",
        "check xaml bindings",
        "binding check",
        "wpf binding check"
    ];

    private static readonly string[] CommandBindingCheckRequests =
    [
        "command binding check",
        "verify command bindings",
        "check command bindings",
        "button command check",
        "wpf command check"
    ];

    private static readonly string[] DeadCommandScanRequests =
    [
        "dead command scan",
        "scan dead commands",
        "find dead commands",
        "dead button scan",
        "command surface scan"
    ];

    private static readonly string[] CommandSurfaceDoctorRequests =
    [
        "command surface doctor",
        "show command surface doctor",
        "check command surface",
        "coding command doctor",
        "command surface check",
        "mini codex command doctor",
        "mini-codex command doctor"
    ];

    private static readonly string[] DraftCommitMessageRequests =
    [
        "draft commit message",
        "suggest commit message",
        "write commit message",
        "generate commit message"
    ];

    private static readonly string[] DraftReleaseNotesRequests =
    [
        "draft release notes",
        "generate release notes",
        "write release notes",
        "summarize release notes"
    ];

    private static readonly string[] CodingSessionTimelineRequests =
    [
        "show coding session timeline",
        "coding session timeline",
        "show coding timeline",
        "what happened this coding session"
    ];

    private static readonly string[] RollbackPlanRequests =
    [
        "show rollback plan",
        "rollback plan",
        "how do i roll this back",
        "undo plan",
        "show undo plan"
    ];

    private static readonly string[] UiChangeChecklistPrefixes =
    [
        "ui change checklist",
        "show ui checklist",
        "wpf checklist",
        "frontend checklist",
        "screen change checklist"
    ];

    private static readonly string[] TypedPatchComposerPrefixes =
    [
        "compose typed patch",
        "draft typed patch",
        "typed patch plan",
        "compose multi file patch",
        "compose multi-file patch",
        "draft multi file patch",
        "draft multi-file patch"
    ];

    private static readonly string[] FileRiskLabelRequests =
    [
        "show file risk labels",
        "file risk labels",
        "risk label files",
        "risk labels for changes",
        "show change risk labels"
    ];

    private static readonly string[] SymbolFinderPrefixes =
    [
        "find symbol",
        "find code symbol",
        "search symbol",
        "locate symbol",
        "where is symbol"
    ];

    private static readonly string[] CrossReferencePrefixes =
    [
        "cross reference",
        "cross-reference",
        "show references for",
        "find references for",
        "who uses",
        "where is used"
    ];

    private static readonly string[] CallGraphPrefixes =
    [
        "show call graph",
        "call graph",
        "callgraph",
        "who calls",
        "what calls"
    ];

    private static readonly string[] SemanticSymbolPrefixes =
    [
        "resolve symbol",
        "semantic symbol",
        "explain symbol",
        "what is symbol"
    ];

    private static readonly string[] ImpactedTestsPrefixes =
    [
        "show impacted tests",
        "impacted tests",
        "impact report",
        "what tests should i run for",
        "tests for change"
    ];

    private static readonly string[] TestTargetPrefixes =
    [
        "resolve test target",
        "test target",
        "smallest test target",
        "targeted test command",
        "which tests for",
        "smallest tests for"
    ];

    private static readonly string[] SemanticEditPlanPrefixes =
    [
        "semantic edit plan",
        "plan semantic edit",
        "safe edit plan",
        "plan edit"
    ];

    private static readonly string[] SafeEditWorkflowPrefixes =
    [
        "safe edit workflow",
        "prepare safe edit",
        "safe edit next step",
        "safe edit runway",
        "mini codex edit plan"
    ];

    private static readonly string[] DiagnosticMapperPrefixes =
    [
        "map compiler diagnostic",
        "map build diagnostic",
        "diagnostic mapper",
        "map diagnostic",
        "explain diagnostic"
    ];

    private static readonly string[] TestGapRequests =
    [
        "show test gap report",
        "test gap report",
        "detect test gaps",
        "check test gaps",
        "changed files without tests"
    ];

    private static readonly string[] KnownErrorPrefixes =
    [
        "explain known error",
        "known error",
        "diagnose error pattern",
        "explain compiler error",
        "explain build error"
    ];

    private static readonly string[] RollbackPatchRequests =
    [
        "preview rollback patch",
        "show rollback patch",
        "draft rollback patch",
        "preview undo patch",
        "show undo patch"
    ];

    private static readonly string[] PlanTaskPrefixes =
    [
        "plan coding task",
        "plan this coding task",
        "plan code task",
        "plan code change",
        "plan coding change",
        "make a coding plan",
        "make coding plan",
        "draft coding plan",
        "plan the fix",
        "plan fix"
    ];

    private static readonly string[] ExploreBuildIdeaPrefixes =
    [
        "explore build idea",
        "explore coding idea",
        "scout build idea",
        "scout coding idea",
        "suggest build paths for",
        "suggest coding paths for",
        "suggest architecture for",
        "suggest software libraries for",
        "help me choose a stack for",
        "help me choose stack for"
    ];

    private static readonly string[] ImplementationRoadmapPrefixes =
    [
        "draft implementation roadmap",
        "make implementation roadmap",
        "create implementation roadmap",
        "plan implementation roadmap",
        "draft coding roadmap",
        "make coding roadmap",
        "create coding roadmap",
        "plan coding roadmap",
        "break down implementation",
        "break down coding task",
        "break down build",
        "roadmap coding task",
        "roadmap for"
    ];

    private static readonly string[] ReceiptRequests =
    [
        "show coding receipts",
        "show code receipts",
        "show recent coding receipts",
        "show recent code receipts",
        "show coding actions",
        "show recent coding actions",
        "coding receipts",
        "coding status",
        "what did you do in coding"
    ];

    private static readonly string[] OpenLastDiagnosticRequests =
    [
        "open last diagnostic",
        "open last diagnostic file",
        "open first diagnostic",
        "open first diagnostic file",
        "open last build error",
        "open build error",
        "open last error file",
        "open failing file",
        "open compiler error",
        "open last compiler error"
    ];

    private static readonly string[] DiagnoseLastFailureRequests =
    [
        "diagnose last failure",
        "diagnose last build failure",
        "diagnose last test failure",
        "diagnose last dotnet failure",
        "explain last failure",
        "explain last build error",
        "explain last compiler error",
        "show last failure",
        "show last build failure",
        "show last dotnet failure",
        "summarize last failure",
        "summarize last build error",
        "what failed last"
    ];

    private static readonly string[] SuggestLastFailurePatchRequests =
    [
        "suggest patch from last failure",
        "suggest patch for last failure",
        "suggest patch from last build failure",
        "suggest patch for last build failure",
        "suggest fix from last failure",
        "suggest fix for last failure",
        "preview patch from last failure",
        "preview fix from last failure"
    ];

    private static readonly string[] PackagePrefixes =
    [
        "list packages",
        "list package references",
        "list dependencies",
        "inspect packages",
        "inspect dependencies",
        "show packages",
        "show dependencies"
    ];

    private static readonly string[] OutdatedPackagePrefixes =
    [
        "dotnet list package --outdated",
        "list outdated packages",
        "check outdated packages",
        "inspect outdated packages",
        "check package updates",
        "check dependency updates"
    ];

    private static readonly string[] AddPackagePrefixes =
    [
        "dotnet add package",
        "add package",
        "install package",
        "add nuget package",
        "install nuget package"
    ];

    private static readonly string[] BuildPrefixes =
    [
        "dotnet build",
        "build workspace",
        "build coding workspace",
        "build solution",
        "build project"
    ];

    private static readonly string[] TestPrefixes =
    [
        "dotnet test",
        "test workspace",
        "test coding workspace",
        "test solution",
        "test project",
        "run tests"
    ];

    private static readonly string[] RestorePrefixes =
    [
        "dotnet restore",
        "restore packages",
        "restore project",
        "restore solution",
        "restore workspace"
    ];

    private static readonly string[] RunPrefixes =
    [
        "dotnet run",
        "run project",
        "run app",
        "run application"
    ];

    private static readonly string[] GitPrefixes =
    [
        "git status",
        "git diff",
        "git log",
        "git add",
        "git commit",
        "git merge",
        "git pull",
        "git push"
    ];

    private static readonly string[] ReviewCurrentChangesRequests =
    [
        "review changes",
        "review current changes",
        "review git changes",
        "review workspace changes",
        "review uncommitted changes",
        "review diff",
        "summarize changes",
        "summarize current changes",
        "summarize git diff",
        "check current changes",
        "check uncommitted changes"
    ];

    public static bool TryParse(string userText, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var trimmed = userText.Trim();
        var userConfirmed = StripConfirmationPrefix(ref trimmed);
        if (IsWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsListWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ListWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsInspectWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.InspectWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsAnalyzeArchitectureRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.AnalyzeArchitecture, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsProjectIntelligenceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowProjectIntelligence, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsProjectIndexRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowProjectIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, ProjectDependencyMapPrefixes, CodingToolAction.ShowProjectDependencyMap, userConfirmed, out request))
        {
            return true;
        }

        if (IsRepoUnderstandingRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRepoUnderstanding, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, CodingContextPacketPrefixes, CodingToolAction.ShowCodingContextPacket, userConfirmed, out request))
        {
            return true;
        }

        if (IsSafeCommitRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSafeCommitCheck, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsWorkspaceHealthScoreRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowWorkspaceHealthScore, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFullCodingReadinessRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFullCodingReadiness, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsMiniCodexStatusRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowMiniCodexStatus, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsValidationLedgerRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowValidationLedger, null, UserConfirmed: userConfirmed);
            return true;
        }


        if (IsValidationQueueRunnerRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowValidationQueueRunner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsMandatorySymbolDiffAuditRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowMandatorySymbolDiffAudit, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, MultiFileRefactorPlanPrefixes, CodingToolAction.PlanMultiFileRefactor, userConfirmed, out request))
        {
            return true;
        }

        if (IsTestFailurePatchLoopRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowTestFailurePatchLoop, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBuildErrorTriageRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBuildErrorTriage, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCodebaseMemoryIndexRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCodebaseMemoryIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCodingNextBestActionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCodingNextBestAction, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOwnerSafePatchBatchRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowOwnerSafePatchBatch, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsGeneratedFileGuardRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowGeneratedFileGuard, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsMiniCodexReadinessReportRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowMiniCodexReadinessReport, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseFeatureBuildLaneCommand(trimmed, userConfirmed, out request))
        {
            return true;
        }
        if (IsCSharpSymbolIndexRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCSharpSymbolIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsXamlBindingCheckRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.VerifyXamlBindings, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCommandBindingCheckRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.VerifyCommandBindings, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDeadCommandScanRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ScanDeadCommands, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCommandSurfaceDoctorRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCommandSurfaceDoctor, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDraftCommitMessageRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DraftCommitMessage, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDraftReleaseNotesRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DraftReleaseNotes, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCodingSessionTimelineRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCodingSessionTimeline, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRollbackPlanRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRollbackPlan, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, UiChangeChecklistPrefixes, CodingToolAction.ShowUiChangeChecklist, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, TypedPatchComposerPrefixes, CodingToolAction.ComposeTypedPatch, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, SymbolFinderPrefixes, CodingToolAction.FindSymbol, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, CrossReferencePrefixes, CodingToolAction.ShowCrossReferenceMap, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, OwnershipMapPrefixes, CodingToolAction.ShowOwnershipMap, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, CallGraphPrefixes, CodingToolAction.ShowCallGraph, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, SemanticSymbolPrefixes, CodingToolAction.ResolveSemanticSymbol, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, ImpactedTestsPrefixes, CodingToolAction.ShowImpactedTests, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, TestTargetPrefixes, CodingToolAction.ResolveTestTarget, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, SemanticEditPlanPrefixes, CodingToolAction.PlanSemanticEdit, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, SafeEditWorkflowPrefixes, CodingToolAction.PlanSafeEditWorkflow, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, DiagnosticMapperPrefixes, CodingToolAction.MapCompilerDiagnostic, userConfirmed, out request)
            || TryParsePrefixedQuery(trimmed, KnownErrorPrefixes, CodingToolAction.ExplainKnownError, userConfirmed, out request))
        {
            return true;
        }

        if (IsFileRiskLabelRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFileRiskLabels, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsTestGapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowTestGapReport, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRollbackPatchRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.PreviewRollbackPatch, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseBuilderPlanningCommand(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsOpenSolutionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenSolution, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePlanTask(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseExploreBuildIdea(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseImplementationRoadmap(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsReceiptRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowReceipts, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOpenLastDiagnosticRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenLastDiagnostic, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiagnoseLastFailureRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiagnoseLastFailure, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSuggestLastFailurePatchRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.SuggestLastFailurePatch, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseSearch(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePackages(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseRead(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseGenerateCodingReport(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePdfCommand(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseComputerAssistantCommand(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsToolIntegrationStatusRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowToolIntegrationStatus, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsVisualStudioHandoffRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.GenerateVisualStudioHandoff, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseGeneratePdf(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePatchBundle(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsShowLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiscardLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiscardLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsApplyLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ApplyLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowLastRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowLastRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiscardLastRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiscardLastRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsApproveLastRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ApproveLastRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsStartApprovedRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.StartApprovedRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowActiveRoadmapStepRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowActiveRoadmapStep, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowNextRoadmapActionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowNextRoadmapAction, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowRoadmapExecutionPacketRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRoadmapExecutionPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsApproveRoadmapExecutionPacketRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ApproveRoadmapExecutionPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowApprovedRoadmapExecutionPacketRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowApprovedRoadmapExecutionPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiscardApprovedRoadmapExecutionPacketRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiscardApprovedRoadmapExecutionPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowRoadmapExecutionPacketProgressRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRoadmapExecutionPacketProgress, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsShowApprovedPacketCommandsRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowApprovedPacketCommands, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseRunApprovedPacketItem(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsShowPacketRunLedgerRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPacketRunLedger, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsAdvanceRoadmapStepRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.AdvanceRoadmapStep, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPauseRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.PauseRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsResumeRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ResumeRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFinishRoadmapRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.FinishRoadmap, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRecoverRoadmapStateRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.RecoverRoadmapState, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiagnoseRecoveryStateRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiagnoseRecoveryState, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePackageLookup(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseDependencyInstallPacket(trimmed, userConfirmed, out request)
            || TryParseScaffoldApply(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseProjectScaffold(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsResumeBuildPlanRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ResumeBuildPlan, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseGenerateMorningReport(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsPostEditValidationRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.PlanPostEditValidation, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBuilderCommandIndexRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBuilderCommandIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCodingSessionSummaryRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCodingSessionSummary, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, StartCodingSessionPrefixes, CodingToolAction.StartCodingSession, userConfirmed, out request))
        {
            return true;
        }

        if (IsContinueCurrentCodingSessionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ContinueCurrentCodingSession, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCurrentCodingSessionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCurrentCodingSession, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsClearCurrentCodingSessionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ClearCurrentCodingSession, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCodingSessionHistoryRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCodingSessionHistory, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsProjectCommandDefaultsRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCurrentProjectCommandDefaults, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePrefixedQuery(trimmed, SaveProjectCommandDefaultsPrefixes, CodingToolAction.SaveCurrentProjectCommandDefaults, userConfirmed, out request))
        {
            return true;
        }

        if (IsSaveProjectCommandDefaultsRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.SaveCurrentProjectCommandDefaults, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsWindowsTroubleshootingToolkitRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowWindowsTroubleshootingToolkit, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseRogueProcessHunt(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseProcessEvidence(trimmed, userConfirmed, out request)
            || TryParsePortOwner(trimmed, userConfirmed, out request)
            || TryParseFileLock(trimmed, userConfirmed, out request)
            || TryParseProcessStopPlan(trimmed, userConfirmed, out request)
            || TryParseProcessStop(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsServicesStartupRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.InspectServicesStartup, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsEventLogTriageRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.TriageEventLogs, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBuildLockDiagnosisRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiagnoseBuildLock, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsClassifyLastFailureRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ClassifyLastFailure, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsReviewCurrentChangesRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ReviewCurrentChanges, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRoadmapStepChecklistRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRoadmapStepChecklist, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsInstallDoctorRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowInstallDoctor, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseFileEdit(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseBuildTestRun(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseGit(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (!HasOpenIntent(trimmed))
        {
            return false;
        }

        if (!TryExtractPath(trimmed, OpenPrefixes, out var path, out var lineNumber))
        {
            return false;
        }

        var action = LooksLikeSolutionRequest(trimmed, path)
            ? CodingToolAction.OpenSolution
            : CodingToolAction.OpenFile;
        request = new CodingToolRequest(action, path, lineNumber, ExplicitUserPath: true, UserConfirmed: userConfirmed);
        return true;
    }

    private static bool IsWorkspaceRequest(string text) =>
        text.Equals("open coding workspace", StringComparison.OrdinalIgnoreCase)
        || text.Equals("open programming projects", StringComparison.OrdinalIgnoreCase)
        || text.Equals("open ali coding workspace", StringComparison.OrdinalIgnoreCase);

    private static bool IsListWorkspaceRequest(string text) =>
        text.Equals("list workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("list coding workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("show workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("show coding workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("list programming projects", StringComparison.OrdinalIgnoreCase);

    private static bool IsInspectWorkspaceRequest(string text) =>
        InspectWorkspaceRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsAnalyzeArchitectureRequest(string text) =>
        AnalyzeArchitectureRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectIntelligenceRequest(string text) =>
        ProjectIntelligenceRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectIndexRequest(string text) =>
        ProjectIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRepoUnderstandingRequest(string text) =>
        RepoUnderstandingRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeCommitRequest(string text) =>
        SafeCommitRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsWorkspaceHealthScoreRequest(string text) =>
        WorkspaceHealthScoreRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFullCodingReadinessRequest(string text) =>
        FullCodingReadinessRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsMiniCodexStatusRequest(string text) =>
        MiniCodexStatusRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidationLedgerRequest(string text) =>
        ValidationLedgerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCSharpSymbolIndexRequest(string text) =>
        CSharpSymbolIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsXamlBindingCheckRequest(string text) =>
        XamlBindingCheckRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCommandBindingCheckRequest(string text) =>
        CommandBindingCheckRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDeadCommandScanRequest(string text) =>
        DeadCommandScanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCommandSurfaceDoctorRequest(string text) =>
        CommandSurfaceDoctorRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDraftCommitMessageRequest(string text) =>
        DraftCommitMessageRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDraftReleaseNotesRequest(string text) =>
        DraftReleaseNotesRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodingSessionTimelineRequest(string text) =>
        CodingSessionTimelineRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRollbackPlanRequest(string text) =>
        RollbackPlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFileRiskLabelRequest(string text) =>
        FileRiskLabelRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsTestGapRequest(string text) =>
        TestGapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRollbackPatchRequest(string text) =>
        RollbackPatchRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenSolutionRequest(string text) =>
        OpenSolutionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsReceiptRequest(string text) =>
        ReceiptRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenLastDiagnosticRequest(string text) =>
        OpenLastDiagnosticRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiagnoseLastFailureRequest(string text) =>
        DiagnoseLastFailureRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSuggestLastFailurePatchRequest(string text) =>
        SuggestLastFailurePatchRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));


    private static bool IsValidationQueueRunnerRequest(string text) =>
        ValidationQueueRunnerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsMandatorySymbolDiffAuditRequest(string text) =>
        MandatorySymbolDiffAuditRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsTestFailurePatchLoopRequest(string text) =>
        TestFailurePatchLoopRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildErrorTriageRequest(string text) =>
        BuildErrorTriageRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodebaseMemoryIndexRequest(string text) =>
        CodebaseMemoryIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodingNextBestActionRequest(string text) =>
        CodingNextBestActionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOwnerSafePatchBatchRequest(string text) =>
        OwnerSafePatchBatchRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsGeneratedFileGuardRequest(string text) =>
        GeneratedFileGuardRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsMiniCodexReadinessReportRequest(string text) =>
        MiniCodexReadinessReportRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatchBundleBuilderRequest(string text) =>
        PatchBundleBuilderRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFailureLoopStateRequest(string text) =>
        FailureLoopStateRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsStopConditionDetectorRequest(string text) =>
        StopConditionDetectorRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSliceRiskScoringRequest(string text) =>
        SliceRiskScoringRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureCompletionReceiptRequest(string text) =>
        FeatureCompletionReceiptRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureExecutionPacketRequest(string text) =>
        FeatureExecutionPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureWorkContextRequest(string text) =>
        FeatureWorkContextRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBehaviorContractRequest(string text) =>
        BehaviorContractRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBehaviorTestPatchPreviewRequest(string text) =>
        BehaviorTestPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatchSlicePlanRequest(string text) =>
        PatchSlicePlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsApplyGateRequest(string text) =>
        ApplyGateRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeaturePatchDraftPlanRequest(string text) =>
        FeaturePatchDraftPlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsExactPatchSynthesisRequest(string text) =>
        ExactPatchSynthesisRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPreviewSynthesizedFeaturePatchRequest(string text) =>
        PreviewSynthesizedFeaturePatchRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsGuidedFeatureBundlePreviewRequest(string text) =>
        GuidedFeatureBundlePreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsAutonomousPatchLoopRequest(string text) =>
        AutonomousPatchLoopRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureSessionLedgerRequest(string text) =>
        FeatureSessionLedgerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidationRepairRunnerRequest(string text) =>
        ValidationRepairRunnerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureRunControllerRequest(string text) =>
        FeatureRunControllerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPostPatchValidationRouterRequest(string text) =>
        PostPatchValidationRouterRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatchPreviewIntelligenceRequest(string text) =>
        PatchPreviewIntelligenceRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsGuidedFeatureWorkflowRequest(string text) =>
        GuidedFeatureWorkflowRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureImplementationPlannerRequest(string text) =>
        FeatureImplementationPlannerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFeatureIntakeNormalizerRequest(string text) =>
        FeatureIntakeNormalizerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsAutonomousFeatureOrchestratorRequest(string text) =>
        AutonomousFeatureOrchestratorRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsImplementationEvidencePackRequest(string text) =>
        ImplementationEvidencePackRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildThisFeatureRequest(string text) =>
        BuildThisFeatureRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRoslynEditPlannerV2Request(string text) =>
        RoslynEditPlannerV2Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsMultiFilePatchSynthesisV2Request(string text) =>
        MultiFilePatchSynthesisV2Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatternCopyPlanRequest(string text) =>
        PatternCopyPlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBehaviorTestGeneratorV2Request(string text) =>
        BehaviorTestGeneratorV2Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsImplementationSliceStateRequest(string text) =>
        ImplementationSliceStateRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPostApplyRepairLoopV2Request(string text) =>
        PostApplyRepairLoopV2Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSemanticDiffSummaryRequest(string text) =>
        SemanticDiffSummaryRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsMiniCodexScoreV3Request(string text) =>
        MiniCodexScoreV3Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsConcretePatchAuthoringRequest(string text) =>
        ConcretePatchAuthoringRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatchBodyGeneratorRequest(string text) =>
        PatchBodyGeneratorRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatternCommandScaffolderRequest(string text) =>
        PatternCommandScaffolderRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsUiBundlePlannerRequest(string text) =>
        UiBundlePlannerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatchConfidenceScoreRequest(string text) =>
        PatchConfidenceScoreRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSliceExecutorPreviewRequest(string text) =>
        SliceExecutorPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFailureToPatchV3Request(string text) =>
        FailureToPatchV3Requests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSemanticChangeReceiptRequest(string text) =>
        SemanticChangeReceiptRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidationChainPlannerRequest(string text) =>
        ValidationChainPlannerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveWorkspaceProjectRequest(string text) =>
        ActiveWorkspaceProjectRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectControlCenterRequest(string text) =>
        ProjectControlCenterRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCurrentProjectMemoryRequest(string text) =>
        CurrentProjectMemoryRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenCurrentProjectFolderRequest(string text) =>
        OpenCurrentProjectFolderRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOwnerApprovedApplyPacketRequest(string text) =>
        OwnerApprovedApplyPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRoslynInsertionPlannerRequest(string text) =>
        RoslynInsertionPlannerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsIntentDiffComposerRequest(string text) =>
        IntentDiffComposerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBehaviorSpecTestScaffoldRequest(string text) =>
        BehaviorSpecTestScaffoldRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRepeatFailureMemoryRequest(string text) =>
        RepeatFailureMemoryRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFirstDiagnosticRepairRouteRequest(string text) =>
        FirstDiagnosticRepairRouteRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidationCommandMinimizerRequest(string text) =>
        ValidationCommandMinimizerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsUiBindingRepairPlannerRequest(string text) =>
        UiBindingRepairPlannerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsAuthoringSequenceFlowRequest(string text) =>
        AuthoringSequenceFlowRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlainEnglishCodingCapabilityCardRequest(string text) =>
        PlainEnglishCodingCapabilityCardRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlainEnglishFeatureBuilderRequest(string text) =>
        PlainEnglishFeatureBuilderRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildFeatureLaneRequest(string text) =>
        BuildFeatureLaneRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));
    private static bool IsShowLastPatchPreviewRequest(string text) =>
        ShowLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiscardLastPatchPreviewRequest(string text) =>
        DiscardLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsApplyLastPatchPreviewRequest(string text) =>
        ApplyLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowLastRoadmapRequest(string text) =>
        ShowLastRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiscardLastRoadmapRequest(string text) =>
        DiscardLastRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsApproveLastRoadmapRequest(string text) =>
        ApproveLastRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsStartApprovedRoadmapRequest(string text) =>
        StartApprovedRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowActiveRoadmapStepRequest(string text) =>
        ShowActiveRoadmapStepRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowNextRoadmapActionRequest(string text) =>
        ShowNextRoadmapActionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowRoadmapExecutionPacketRequest(string text) =>
        ShowRoadmapExecutionPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsApproveRoadmapExecutionPacketRequest(string text) =>
        ApproveRoadmapExecutionPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowApprovedRoadmapExecutionPacketRequest(string text) =>
        ShowApprovedRoadmapExecutionPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiscardApprovedRoadmapExecutionPacketRequest(string text) =>
        DiscardApprovedRoadmapExecutionPacketRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowRoadmapExecutionPacketProgressRequest(string text) =>
        ShowRoadmapExecutionPacketProgressRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowApprovedPacketCommandsRequest(string text) =>
        ShowApprovedPacketCommandsRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowPacketRunLedgerRequest(string text) =>
        ShowPacketRunLedgerRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsAdvanceRoadmapStepRequest(string text) =>
        AdvanceRoadmapStepRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPauseRoadmapRequest(string text) =>
        PauseRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsResumeRoadmapRequest(string text) =>
        ResumeRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsFinishRoadmapRequest(string text) =>
        FinishRoadmapRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRecoverRoadmapStateRequest(string text) =>
        RecoverRoadmapStateRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiagnoseRecoveryStateRequest(string text) =>
        DiagnoseRecoveryStateRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsResumeBuildPlanRequest(string text) =>
        ResumeBuildPlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsPostEditValidationRequest(string text) =>
        PostEditValidationRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBuilderCommandIndexRequest(string text) =>
        BuilderCommandIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodingSessionSummaryRequest(string text) =>
        CodingSessionSummaryRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCurrentCodingSessionRequest(string text) =>
        CurrentCodingSessionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsContinueCurrentCodingSessionRequest(string text) =>
        ContinueCurrentCodingSessionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsClearCurrentCodingSessionRequest(string text) =>
        ClearCurrentCodingSessionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodingSessionHistoryRequest(string text) =>
        CodingSessionHistoryRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectCommandDefaultsRequest(string text) =>
        ProjectCommandDefaultsRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSaveProjectCommandDefaultsRequest(string text) =>
        SaveProjectCommandDefaultsRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowsTroubleshootingToolkitRequest(string text) =>
        WindowsTroubleshootingToolkitRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsServicesStartupRequest(string text) =>
        ServicesStartupRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsEventLogTriageRequest(string text) =>
        EventLogTriageRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildLockDiagnosisRequest(string text) =>
        BuildLockDiagnosisRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsClassifyLastFailureRequest(string text) =>
        ClassifyLastFailureRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsRoadmapStepChecklistRequest(string text) =>
        RoadmapStepChecklistRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsInstallDoctorRequest(string text) =>
        InstallDoctorRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsComputerAssistantStatusRequest(string text) =>
        ComputerAssistantStatusRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsComputerAssistantCommandIndexRequest(string text) =>
        ComputerAssistantCommandIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsUserCommandHelpRequest(string text) =>
        UserCommandHelpRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiskCleanupPlanRequest(string text) =>
        DiskCleanupPlanRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsComputerTroubleshootingCommandIndexRequest(string text) =>
        ComputerTroubleshootingCommandIndexRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsToolIntegrationStatusRequest(string text) =>
        ToolIntegrationStatusRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsVisualStudioHandoffRequest(string text) =>
        VisualStudioHandoffRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePlanTask(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PlanTaskPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.PlanTask,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseFeatureBuildLaneCommand(string text, bool userConfirmed, out CodingToolRequest request)
    {
        if (TryParsePrefixedQuery(text, FeatureIntentPacketPrefixes, CodingToolAction.BuildFeatureIntentPacket, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BehaviorTestPlanPrefixes, CodingToolAction.PlanBehaviorTests, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BehaviorTestPatchPreviewPrefixes, CodingToolAction.PreviewBehaviorTestPatch, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ImplementationSlicePlanPrefixes, CodingToolAction.PlanImplementationSlices, userConfirmed, out request)
            || TryParsePrefixedQuery(text, TestStubGeneratorPlanPrefixes, CodingToolAction.ShowTestStubGeneratorPlan, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureExecutionPacketPrefixes, CodingToolAction.ShowFeatureExecutionPacket, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureWorkContextPrefixes, CodingToolAction.ShowFeatureWorkContext, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BehaviorContractPrefixes, CodingToolAction.ShowBehaviorContract, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatchSlicePlanPrefixes, CodingToolAction.ShowPatchSlicePlan, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ApplyGatePrefixes, CodingToolAction.ShowApplyGate, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeaturePatchDraftPlanPrefixes, CodingToolAction.ShowFeaturePatchDraftPlan, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ExactPatchSynthesisPrefixes, CodingToolAction.ShowExactPatchSynthesis, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PreviewSynthesizedFeaturePatchPrefixes, CodingToolAction.PreviewSynthesizedFeaturePatch, userConfirmed, out request)
            || TryParsePrefixedQuery(text, GuidedFeatureBundlePreviewPrefixes, CodingToolAction.PreviewGuidedFeatureBundle, userConfirmed, out request)
            || TryParsePrefixedQuery(text, AutonomousPatchLoopPrefixes, CodingToolAction.ShowAutonomousPatchLoop, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureSessionLedgerPrefixes, CodingToolAction.ShowFeatureSessionLedger, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ValidationRepairRunnerPrefixes, CodingToolAction.ShowValidationRepairRunner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureRunControllerPrefixes, CodingToolAction.ShowFeatureRunController, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PostPatchValidationRouterPrefixes, CodingToolAction.ShowPostPatchValidationRouter, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatchPreviewIntelligencePrefixes, CodingToolAction.ShowPatchPreviewIntelligence, userConfirmed, out request)
            || TryParsePrefixedQuery(text, GuidedFeatureWorkflowPrefixes, CodingToolAction.ShowGuidedFeatureWorkflow, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureImplementationPlannerPrefixes, CodingToolAction.ShowFeatureImplementationPlanner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureIntakeNormalizerPrefixes, CodingToolAction.ShowFeatureIntakeNormalizer, userConfirmed, out request)
            || TryParsePrefixedQuery(text, AutonomousFeatureOrchestratorPrefixes, CodingToolAction.ShowAutonomousFeatureOrchestrator, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ImplementationEvidencePackPrefixes, CodingToolAction.ShowImplementationEvidencePack, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BuildThisFeaturePrefixes, CodingToolAction.ShowBuildThisFeature, userConfirmed, out request)
            || TryParsePrefixedQuery(text, RoslynEditPlannerV2Prefixes, CodingToolAction.ShowRoslynEditPlannerV2, userConfirmed, out request)
            || TryParsePrefixedQuery(text, MultiFilePatchSynthesisV2Prefixes, CodingToolAction.ShowMultiFilePatchSynthesisV2, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatternCopyPlanPrefixes, CodingToolAction.ShowPatternCopyPlan, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BehaviorTestGeneratorV2Prefixes, CodingToolAction.ShowBehaviorTestGeneratorV2, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ImplementationSliceStatePrefixes, CodingToolAction.ShowImplementationSliceState, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PostApplyRepairLoopV2Prefixes, CodingToolAction.ShowPostApplyRepairLoopV2, userConfirmed, out request)
            || TryParsePrefixedQuery(text, SemanticDiffSummaryPrefixes, CodingToolAction.ShowSemanticDiffSummary, userConfirmed, out request)
            || TryParsePrefixedQuery(text, MiniCodexScoreV3Prefixes, CodingToolAction.ShowMiniCodexScoreV3, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ConcretePatchAuthoringPrefixes, CodingToolAction.ShowConcretePatchAuthoring, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatchBodyGeneratorPrefixes, CodingToolAction.ShowPatchBodyGenerator, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatternCommandScaffolderPrefixes, CodingToolAction.ShowPatternCommandScaffolder, userConfirmed, out request)
            || TryParsePrefixedQuery(text, UiBundlePlannerPrefixes, CodingToolAction.ShowUiBundlePlanner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PatchConfidenceScorePrefixes, CodingToolAction.ShowPatchConfidenceScore, userConfirmed, out request)
            || TryParsePrefixedQuery(text, SliceExecutorPreviewPrefixes, CodingToolAction.ShowSliceExecutorPreview, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FailureToPatchV3Prefixes, CodingToolAction.ShowFailureToPatchV3, userConfirmed, out request)
            || TryParsePrefixedQuery(text, SemanticChangeReceiptPrefixes, CodingToolAction.ShowSemanticChangeReceipt, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ValidationChainPlannerPrefixes, CodingToolAction.ShowValidationChainPlanner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, DataSystemsGuidePrefixes, CodingToolAction.ShowDataSystemsGuide, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ConsoleCodingGuidePrefixes, CodingToolAction.ShowConsoleCodingGuide, userConfirmed, out request)
            || TryParsePrefixedQuery(text, WpfCodingGuidePrefixes, CodingToolAction.ShowWpfCodingGuide, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ActiveWorkspaceProjectPrefixes, CodingToolAction.ShowActiveWorkspaceProject, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ProjectControlCenterPrefixes, CodingToolAction.ShowProjectControlCenter, userConfirmed, out request)
            || TryParsePrefixedQuery(text, CurrentProjectMemoryPrefixes, CodingToolAction.ShowCurrentProjectMemory, userConfirmed, out request)
            || TryParsePrefixedQuery(text, SaveCurrentProjectMemoryPrefixes, CodingToolAction.SaveCurrentProjectMemory, userConfirmed, out request)
            || TryParsePrefixedQuery(text, OwnerApprovedApplyPacketPrefixes, CodingToolAction.ShowOwnerApprovedApplyPacket, userConfirmed, out request)
            || TryParsePrefixedQuery(text, RoslynInsertionPlannerPrefixes, CodingToolAction.ShowRoslynInsertionPlanner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, IntentDiffComposerPrefixes, CodingToolAction.ShowIntentDiffComposer, userConfirmed, out request)
            || TryParsePrefixedQuery(text, BehaviorSpecTestScaffoldPrefixes, CodingToolAction.ShowBehaviorSpecTestScaffold, userConfirmed, out request)
            || TryParsePrefixedQuery(text, RepeatFailureMemoryPrefixes, CodingToolAction.ShowRepeatFailureMemory, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FirstDiagnosticRepairRoutePrefixes, CodingToolAction.ShowFirstDiagnosticRepairRoute, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ValidationCommandMinimizerPrefixes, CodingToolAction.ShowValidationCommandMinimizer, userConfirmed, out request)
            || TryParsePrefixedQuery(text, UiBindingRepairPlannerPrefixes, CodingToolAction.ShowUiBindingRepairPlanner, userConfirmed, out request)
            || TryParsePrefixedQuery(text, AuthoringSequenceFlowPrefixes, CodingToolAction.ShowAuthoringSequenceFlow, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PlainEnglishCodingCapabilityCardPrefixes, CodingToolAction.ShowPlainEnglishCodingCapabilityCard, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PlainEnglishFeatureBuilderPrefixes, CodingToolAction.ShowPlainEnglishFeatureBuilder, userConfirmed, out request)
            || TryParsePlainEnglishBuildRequest(text, userConfirmed, out request))
        {
            return true;
        }

        if (IsPatchBundleBuilderRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatchBundleBuilder, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFailureLoopStateRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFailureLoopState, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsStopConditionDetectorRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowStopConditionDetector, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSliceRiskScoringRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSliceRiskScoring, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureCompletionReceiptRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureCompletionReceipt, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDataSystemsGuideRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowDataSystemsGuide, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsConsoleCodingGuideRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowConsoleCodingGuide, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsWpfCodingGuideRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowWpfCodingGuide, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureExecutionPacketRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureExecutionPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureWorkContextRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureWorkContext, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBehaviorContractRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBehaviorContract, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBehaviorTestPatchPreviewRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.PreviewBehaviorTestPatch, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatchSlicePlanRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatchSlicePlan, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsApplyGateRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowApplyGate, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeaturePatchDraftPlanRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeaturePatchDraftPlan, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsExactPatchSynthesisRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowExactPatchSynthesis, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPreviewSynthesizedFeaturePatchRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.PreviewSynthesizedFeaturePatch, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsGuidedFeatureBundlePreviewRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.PreviewGuidedFeatureBundle, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsAutonomousPatchLoopRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowAutonomousPatchLoop, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureSessionLedgerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureSessionLedger, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsValidationRepairRunnerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowValidationRepairRunner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureRunControllerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureRunController, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPostPatchValidationRouterRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPostPatchValidationRouter, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatchPreviewIntelligenceRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatchPreviewIntelligence, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsGuidedFeatureWorkflowRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowGuidedFeatureWorkflow, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureImplementationPlannerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureImplementationPlanner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFeatureIntakeNormalizerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFeatureIntakeNormalizer, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsAutonomousFeatureOrchestratorRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowAutonomousFeatureOrchestrator, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsImplementationEvidencePackRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowImplementationEvidencePack, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBuildThisFeatureRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBuildThisFeature, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRoslynEditPlannerV2Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRoslynEditPlannerV2, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsMultiFilePatchSynthesisV2Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowMultiFilePatchSynthesisV2, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatternCopyPlanRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatternCopyPlan, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBehaviorTestGeneratorV2Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBehaviorTestGeneratorV2, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsImplementationSliceStateRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowImplementationSliceState, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPostApplyRepairLoopV2Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPostApplyRepairLoopV2, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSemanticDiffSummaryRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSemanticDiffSummary, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsMiniCodexScoreV3Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowMiniCodexScoreV3, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsConcretePatchAuthoringRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowConcretePatchAuthoring, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatchBodyGeneratorRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatchBodyGenerator, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatternCommandScaffolderRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatternCommandScaffolder, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsUiBundlePlannerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowUiBundlePlanner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPatchConfidenceScoreRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPatchConfidenceScore, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSliceExecutorPreviewRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSliceExecutorPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFailureToPatchV3Request(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFailureToPatchV3, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSemanticChangeReceiptRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSemanticChangeReceipt, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsValidationChainPlannerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowValidationChainPlanner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsActiveWorkspaceProjectRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowActiveWorkspaceProject, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsProjectControlCenterRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowProjectControlCenter, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsCurrentProjectMemoryRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowCurrentProjectMemory, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOpenCurrentProjectFolderRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.OpenCurrentProjectFolder, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOwnerApprovedApplyPacketRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowOwnerApprovedApplyPacket, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRoslynInsertionPlannerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRoslynInsertionPlanner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsIntentDiffComposerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowIntentDiffComposer, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBehaviorSpecTestScaffoldRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBehaviorSpecTestScaffold, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsRepeatFailureMemoryRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRepeatFailureMemory, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsFirstDiagnosticRepairRouteRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowFirstDiagnosticRepairRoute, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsValidationCommandMinimizerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowValidationCommandMinimizer, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsUiBindingRepairPlannerRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowUiBindingRepairPlanner, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsAuthoringSequenceFlowRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowAuthoringSequenceFlow, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPlainEnglishCodingCapabilityCardRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPlainEnglishCodingCapabilityCard, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsPlainEnglishFeatureBuilderRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPlainEnglishFeatureBuilder, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsBuildFeatureLaneRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowBuildFeatureLane, null, UserConfirmed: userConfirmed);
            return true;
        }

        return false;
    }

    private static bool TryParsePlainEnglishBuildRequest(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PlainEnglishBuildRequestPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        if (string.IsNullOrWhiteSpace(query) || !LooksLikeCodingBuildRequest(query))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.ShowPlainEnglishFeatureBuilder,
            null,
            UserConfirmed: userConfirmed,
            Query: query);
        return true;
    }

    private static bool LooksLikeCodingBuildRequest(string text) =>
        CodingBuildTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseBuilderPlanningCommand(string text, bool userConfirmed, out CodingToolRequest request)
    {
        if (TryParsePrefixedQuery(text, BuildGoalInterpreterPrefixes, CodingToolAction.InterpretBuildGoal, userConfirmed, out request)
            || TryParsePrefixedQuery(text, ArchitectureOptionPrefixes, CodingToolAction.ShowArchitectureOptions, userConfirmed, out request)
            || TryParsePrefixedQuery(text, AcceptanceCriteriaPrefixes, CodingToolAction.WriteAcceptanceCriteria, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureTestPrefixes, CodingToolAction.SuggestFeatureTests, userConfirmed, out request)
            || TryParsePrefixedQuery(text, FeatureFilePlanPrefixes, CodingToolAction.PlanFeatureFiles, userConfirmed, out request)
            || TryParsePrefixedQuery(text, RefactorSafetyPrefixes, CodingToolAction.ShowRefactorSafetyChecklist, userConfirmed, out request))
        {
            return true;
        }

        if (CodebasePatternRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            request = new CodingToolRequest(CodingToolAction.DetectCodebasePatterns, null, UserConfirmed: userConfirmed);
            return true;
        }

        return false;
    }

    private static bool IsDataSystemsGuideRequest(string text) =>
        DataSystemsGuideRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsConsoleCodingGuideRequest(string text) =>
        ConsoleCodingGuideRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsWpfCodingGuideRequest(string text) =>
        WpfCodingGuideRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePrefixedQuery(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = prefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            action,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseImplementationRoadmap(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = ImplementationRoadmapPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.DraftImplementationRoadmap,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseExploreBuildIdea(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = ExploreBuildIdeaPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.ExploreBuildIdea,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseRunApprovedPacketItem(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = RunApprovedPacketItemPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var itemText = text[prefix.Length..].Trim().Trim(':', '-', ' ', '#');
        if (!int.TryParse(itemText, out var itemNumber) || itemNumber < 1)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.RunApprovedPacketItem,
            null,
            UserConfirmed: userConfirmed,
            Query: itemNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static bool TryParsePackageLookup(string text, bool userConfirmed, out CodingToolRequest request)
    {
        return TryParsePrefixedQuery(text, PackageLookupPrefixes, CodingToolAction.PlanPackageLookup, userConfirmed, out request);
    }

    private static bool TryParseDependencyInstallPacket(string text, bool userConfirmed, out CodingToolRequest request)
    {
        return TryParsePrefixedQuery(text, DependencyInstallPacketPrefixes, CodingToolAction.PlanDependencyInstallPacket, userConfirmed, out request);
    }

    private static bool TryParseProjectScaffold(string text, bool userConfirmed, out CodingToolRequest request)
    {
        return TryParsePrefixedQuery(text, ProjectScaffoldPrefixes, CodingToolAction.PreviewProjectScaffold, userConfirmed, out request);
    }

    private static bool TryParseScaffoldApply(string text, bool userConfirmed, out CodingToolRequest request)
    {
        return TryParsePrefixedQuery(text, ScaffoldApplyPrefixes, CodingToolAction.PlanScaffoldApply, userConfirmed, out request);
    }

    private static bool TryParseRogueProcessHunt(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = RogueProcessHuntPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.PlanRogueProcessHunt,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseComputerAssistantCommand(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);

        if (IsUserCommandHelpRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowUserCommandHelp, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsComputerAssistantStatusRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowComputerAssistantStatus, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsComputerAssistantCommandIndexRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowComputerAssistantCommandIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiskCleanupPlanRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.PlanDiskCleanup, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsComputerTroubleshootingCommandIndexRequest(text))
        {
            request = new CodingToolRequest(CodingToolAction.ShowComputerTroubleshootingCommandIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseComputerTroubleshootingPlan(text, userConfirmed, out request))
        {
            return true;
        }

        return TryParsePrefixedQuery(text, FileOrganizationPlanPrefixes, CodingToolAction.PlanFileOrganization, userConfirmed, out request)
            || TryParsePrefixedQuery(text, DiskCleanupPlanPrefixes, CodingToolAction.PlanDiskCleanup, userConfirmed, out request)
            || TryParsePrefixedQuery(text, AppInstallTroubleshootingPlanPrefixes, CodingToolAction.PlanAppInstallTroubleshooting, userConfirmed, out request)
            || TryParsePrefixedQuery(text, PeripheralSetupPlanPrefixes, CodingToolAction.PlanPeripheralSetup, userConfirmed, out request);
    }

    private static bool TryParseComputerTroubleshootingPlan(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var exact = ComputerTroubleshootingPlanRequests
            .FirstOrDefault(candidate => text.Equals(candidate.Request, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact.Request))
        {
            request = new CodingToolRequest(
                CodingToolAction.PlanComputerTroubleshooting,
                null,
                UserConfirmed: userConfirmed,
                Query: exact.Scenario);
            return true;
        }

        var prefixed = ComputerTroubleshootingPlanPrefixes
            .OrderByDescending(candidate => candidate.Prefix.Length)
            .FirstOrDefault(candidate => text.StartsWith(candidate.Prefix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(prefixed.Prefix))
        {
            return false;
        }

        var detail = text[prefixed.Prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        var query = string.IsNullOrWhiteSpace(detail)
            ? prefixed.Scenario
            : $"{prefixed.Scenario}: {detail}";
        request = new CodingToolRequest(
            CodingToolAction.PlanComputerTroubleshooting,
            null,
            UserConfirmed: userConfirmed,
            Query: query);
        return true;
    }

    private static bool TryParseProcessEvidence(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = ProcessEvidencePrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.CollectProcessEvidence,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParsePortOwner(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PortOwnerPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        if (!int.TryParse(query, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.DiagnosePortOwner,
            null,
            UserConfirmed: userConfirmed,
            Query: port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static bool TryParseFileLock(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = FileLockPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.DiagnoseFileLock,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseProcessStopPlan(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = ProcessStopPlanPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.PlanProcessStop,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool TryParseProcessStop(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = ProcessStopPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '#');
        if (!int.TryParse(query, out var pid) || pid < 1)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.ExecuteProcessStop,
            null,
            UserConfirmed: userConfirmed,
            Query: pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static bool HasOpenIntent(string text)
    {
        foreach (var prefix in OpenPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSearch(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        foreach (var prefix in SearchPrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var query = text[prefix.Length..].Trim().Trim('"');
            if (query.Length == 0)
            {
                return false;
            }

            request = new CodingToolRequest(
                CodingToolAction.SearchWorkspace,
                null,
                ExplicitUserPath: false,
                UserConfirmed: userConfirmed,
                Query: query);
            return true;
        }

        return false;
    }

    private static bool TryParsePackages(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseAddPackage(text, userConfirmed, out request))
        {
            return true;
        }

        if (!TryParseWorkspaceCommand(text, PackagePrefixes, CodingToolAction.ListPackages, userConfirmed, out request))
        {
            return TryParseWorkspaceCommand(text, OutdatedPackagePrefixes, CodingToolAction.ListOutdatedPackages, userConfirmed, out request);
        }

        return true;
    }

    private static bool TryParseAddPackage(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = AddPackagePrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count >= 2)
        {
            request = new CodingToolRequest(
                CodingToolAction.AddPackage,
                segments[1].Trim(),
                ExplicitUserPath: true,
                UserConfirmed: userConfirmed,
                Query: segments[0].Trim());
            return !string.IsNullOrWhiteSpace(request.Query)
                   && !string.IsNullOrWhiteSpace(request.Path);
        }

        var remainder = text[prefix.Length..].Trim();
        if (remainder.Length == 0)
        {
            return false;
        }

        var pathPrefix = " to ";
        var pathIndex = remainder.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase);
        if (pathIndex < 0)
        {
            pathPrefix = " in ";
            pathIndex = remainder.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        if (pathIndex < 0)
        {
            request = new CodingToolRequest(
                CodingToolAction.AddPackage,
                null,
                UserConfirmed: userConfirmed,
                Query: remainder.Trim().Trim('"'));
            return true;
        }

        var packageId = remainder[..pathIndex].Trim().Trim('"');
        var path = remainder[(pathIndex + pathPrefix.Length)..].Trim().Trim('"');
        request = new CodingToolRequest(
            CodingToolAction.AddPackage,
            path,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            Query: packageId);
        return !string.IsNullOrWhiteSpace(packageId)
               && !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryParseRead(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, ReadPrefixes))
        {
            return false;
        }

        if (!TryExtractPath(text, ReadPrefixes, out var path, out var lineNumber))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.ReadFile,
            path,
            lineNumber,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool TryParseGeneratePdf(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, GeneratePdfPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 2 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.GeneratePdf,
            segments[0].Trim(),
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed,
            Content: segments[1]);
        return true;
    }

    private static bool TryParsePdfCommand(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (PdfToolStatusRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPdfToolStatus, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (PdfCommandIndexRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            request = new CodingToolRequest(CodingToolAction.ShowPdfCommandIndex, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePdfPathCommand(text, InspectPdfPrefixes, CodingToolAction.InspectPdf, userConfirmed, out request)
            || TryParsePdfPathCommand(text, ExtractPdfTextPrefixes, CodingToolAction.ExtractPdfText, userConfirmed, out request)
            || TryParsePdfPathCommand(text, SummarizePdfPrefixes, CodingToolAction.SummarizePdf, userConfirmed, out request)
            || TryParsePdfPathCommand(text, SplitPdfPrefixes, CodingToolAction.SplitPdf, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseConvertMarkdownToPdf(text, userConfirmed, out request)
            || TryParseCombinePdfs(text, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseNamedReport(text, GenerateInstallReportRequests, CodingToolAction.GenerateInstallReport, "ali-install-report.pdf", userConfirmed, out request)
            || TryParseNamedReport(text, GenerateTroubleshootingReportRequests, CodingToolAction.GenerateTroubleshootingReport, "ali-troubleshooting-report.pdf", userConfirmed, out request))
        {
            return true;
        }

        return false;
    }

    private static bool TryParsePdfPathCommand(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, prefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count == 0 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            action,
            segments[0].Trim(),
            ExplicitUserPath: IsFullyQualifiedLocalPath(segments[0]),
            UserConfirmed: userConfirmed,
            Query: segments.Count > 1 ? segments[1].Trim() : null);
        return true;
    }

    private static bool TryParseConvertMarkdownToPdf(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, ConvertMarkdownToPdfPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 1 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.ConvertMarkdownToPdf,
            segments[0].Trim(),
            ExplicitUserPath: IsFullyQualifiedLocalPath(segments[0]),
            UserConfirmed: userConfirmed,
            Query: segments.Count > 1 && !string.IsNullOrWhiteSpace(segments[1]) ? segments[1].Trim() : null);
        return true;
    }

    private static bool TryParseCombinePdfs(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, CombinePdfPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 3 || string.IsNullOrWhiteSpace(segments[^1]))
        {
            return false;
        }

        var sourcePaths = segments.Take(segments.Count - 1)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (sourcePaths.Length < 2)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.CombinePdfs,
            segments[^1].Trim(),
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed,
            AdditionalPaths: sourcePaths);
        return true;
    }

    private static bool TryParseNamedReport(
        string text,
        IReadOnlyList<string> requests,
        CodingToolAction action,
        string defaultName,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var matched = requests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            || requests.Any(candidate => text.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase));
        if (!matched)
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        var fileName = segments.Count > 0 && !string.IsNullOrWhiteSpace(segments[0])
            ? segments[0].Trim()
            : defaultName;
        request = new CodingToolRequest(action, fileName, ExplicitUserPath: false, UserConfirmed: userConfirmed);
        return true;
    }

    private static bool TryParseGenerateCodingReport(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var matched = GenerateCodingReportRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            || GenerateCodingReportRequests.Any(candidate => text.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase));
        if (!matched)
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        var fileName = segments.Count > 0 && !string.IsNullOrWhiteSpace(segments[0])
            ? segments[0].Trim()
            : "ali-coding-session-report.pdf";
        request = new CodingToolRequest(
            CodingToolAction.GenerateCodingReport,
            fileName,
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool TryParseGenerateMorningReport(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var matched = GenerateMorningReportRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            || GenerateMorningReportRequests.Any(candidate => text.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase));
        if (!matched)
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        var fileName = segments.Count > 0 && !string.IsNullOrWhiteSpace(segments[0])
            ? segments[0].Trim()
            : "ali-morning-build-report.pdf";
        request = new CodingToolRequest(
            CodingToolAction.GenerateMorningReport,
            fileName,
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool TryParsePatchBundle(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PreviewPatchBundlePrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var body = text[prefix.Length..].Trim();
        if (body.Length == 0)
        {
            return false;
        }

        var edits = new List<CodingPatchEdit>();
        foreach (var line in SplitPatchBundleLines(body))
        {
            var segments = ExtractQuotedSegments(line);
            if (segments.Count < 3 || string.IsNullOrWhiteSpace(segments[0]))
            {
                return false;
            }

            edits.Add(new CodingPatchEdit(
                segments[0].Trim(),
                segments[1],
                segments[2]));
        }

        if (edits.Count == 0)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.PreviewPatchBundle,
            null,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            PatchEdits: edits);
        return true;
    }

    private static IReadOnlyList<string> SplitPatchBundleLines(string text)
    {
        var lines = new List<string>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Equals("```", StringComparison.Ordinal)
                || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            line = line.TrimStart('-', '*', ' ');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool TryParseFileEdit(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseTextWrite(text, CreateFilePrefixes, CodingToolAction.CreateFile, userConfirmed, out request)
            || TryParseTextWrite(text, AppendFilePrefixes, CodingToolAction.AppendFile, userConfirmed, out request))
        {
            return true;
        }

        var isPreview = StartsWithAny(text, PreviewReplaceTextPrefixes);
        if (!isPreview && !StartsWithAny(text, ReplaceTextPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 3 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            isPreview ? CodingToolAction.PreviewReplaceText : CodingToolAction.ReplaceText,
            segments[0].Trim(),
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            Content: segments[1],
            Replacement: segments[2]);
        return true;
    }

    private static bool TryParseTextWrite(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, prefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 2 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            action,
            segments[0].Trim(),
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            Content: segments[1]);
        return true;
    }

    private static bool TryParseBuildTestRun(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseWorkspaceCommand(text, BuildPrefixes, CodingToolAction.Build, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, TestPrefixes, CodingToolAction.Test, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, RestorePrefixes, CodingToolAction.Restore, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, RunPrefixes, CodingToolAction.RunProject, userConfirmed, out request))
        {
            return true;
        }

        return false;
    }

    private static bool IsReviewCurrentChangesRequest(string text) =>
        ReviewCurrentChangesRequests.Any(candidate => text.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseGit(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = GitPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var action = prefix.ToLowerInvariant() switch
        {
            "git status" => CodingToolAction.GitStatus,
            "git diff" => CodingToolAction.GitDiff,
            "git log" => CodingToolAction.GitLog,
            "git add" => CodingToolAction.GitAdd,
            "git commit" => CodingToolAction.GitCommit,
            "git merge" => CodingToolAction.GitMerge,
            "git pull" => CodingToolAction.GitPull,
            "git push" => CodingToolAction.GitPush,
            _ => CodingToolAction.GitStatus
        };
        var remainder = text[prefix.Length..].Trim();
        request = new CodingToolRequest(
            action,
            null,
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed,
            Query: NormalizeGitRemainder(action, remainder));
        return true;
    }

    private static bool TryParseWorkspaceCommand(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = prefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var remainder = text[prefix.Length..].Trim();
        if (remainder.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[3..].Trim();
        }

        if (remainder.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[3..].Trim();
        }

        if (remainder.StartsWith("for ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[4..].Trim();
        }

        if (remainder.Length == 0)
        {
            request = new CodingToolRequest(action, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (!TryExtractPathFromRemainder(remainder, out var path, out var lineNumber))
        {
            return false;
        }

        request = new CodingToolRequest(
            action,
            path,
            lineNumber,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool LooksLikeSolutionRequest(string text, string path)
    {
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in SolutionPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPath(string text, IReadOnlyList<string> prefixes, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;

        var working = StripKnownPrefix(text, prefixes);
        if (TryExtractTrailingLineNumber(working, out var withoutLineNumber, out var parsedLineNumber))
        {
            working = withoutLineNumber;
            lineNumber = parsedLineNumber;
        }

        working = working.Trim();
        if (working.Length == 0)
        {
            return false;
        }

        if (TryExtractQuotedPath(working, out var quotedPath))
        {
            path = quotedPath;
            return true;
        }

        return TryExtractPathFromRemainder(working, out path, out _);
    }

    private static bool TryExtractPathFromRemainder(string text, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;
        var working = text.Trim();
        if (TryExtractTrailingLineNumber(working, out var withoutLineNumber, out var parsedLineNumber))
        {
            working = withoutLineNumber;
            lineNumber = parsedLineNumber;
        }

        if (TryExtractQuotedPath(working, out var quotedPath))
        {
            path = quotedPath;
            return true;
        }

        var driveIndex = FindDrivePathStart(working);
        if (driveIndex < 0)
        {
            return false;
        }

        path = working[driveIndex..].Trim().TrimEnd('.', ',', ';');
        return path.Length > 0;
    }

    private static string StripKnownPrefix(string text, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..];
            }
        }

        return text;
    }

    private static string? NormalizeGitRemainder(CodingToolAction action, string remainder)
    {
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return null;
        }

        var normalized = remainder.Trim();
        if (action == CodingToolAction.GitCommit)
        {
            if (normalized.StartsWith("-m ", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[3..].Trim();
            }

            if (TryExtractQuotedPath(normalized, out var quotedMessage))
            {
                return quotedMessage;
            }
        }

        return normalized.Trim('"');
    }

    private static bool TryExtractQuotedPath(string text, out string path)
    {
        path = string.Empty;
        var firstQuote = text.IndexOf('"');
        if (firstQuote < 0)
        {
            return false;
        }

        var secondQuote = text.IndexOf('"', firstQuote + 1);
        if (secondQuote <= firstQuote)
        {
            return false;
        }

        path = text.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
        return path.Length > 0;
    }

    private static bool IsFullyQualifiedLocalPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && System.IO.Path.IsPathFullyQualified(path.Trim().Trim('"'));

    private static IReadOnlyList<string> ExtractQuotedSegments(string text)
    {
        var segments = new List<string>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var firstQuote = text.IndexOf('"', searchIndex);
            if (firstQuote < 0)
            {
                break;
            }

            var secondQuote = text.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
            {
                break;
            }

            segments.Add(text.Substring(firstQuote + 1, secondQuote - firstQuote - 1));
            searchIndex = secondQuote + 1;
        }

        return segments;
    }

    private static int FindDrivePathStart(string text)
    {
        for (var i = 0; i < text.Length - 2; i++)
        {
            if (char.IsLetter(text[i]) && text[i + 1] == ':' && (text[i + 2] == '\\' || text[i + 2] == '/'))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryExtractTrailingLineNumber(string text, out string withoutLineNumber, out int? lineNumber)
    {
        withoutLineNumber = text;
        lineNumber = null;

        var markerIndex = LastIndexOfLineMarker(text, " at line ");
        if (markerIndex < 0)
        {
            markerIndex = LastIndexOfLineMarker(text, " line ");
        }
        if (markerIndex < 0)
        {
            return false;
        }

        var marker = text[markerIndex..];
        var digits = new string(marker.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var parsed) || parsed < 1)
        {
            return false;
        }

        withoutLineNumber = text[..markerIndex].TrimEnd();
        lineNumber = parsed;
        return true;
    }

    private static int LastIndexOfLineMarker(string text, string marker) =>
        text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string text, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StripConfirmationPrefix(ref string text)
    {
        foreach (var prefix in ConfirmationPrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text = text[prefix.Length..].Trim();
            return true;
        }

        return false;
    }
}
