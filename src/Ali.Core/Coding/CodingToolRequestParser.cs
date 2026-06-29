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

        if (IsRepoUnderstandingRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowRepoUnderstanding, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsSafeCommitRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowSafeCommitCheck, null, UserConfirmed: userConfirmed);
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

    private static bool IsRepoUnderstandingRequest(string text) =>
        RepoUnderstandingRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeCommitRequest(string text) =>
        SafeCommitRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

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
