# Ali editor integrations

Ali treats editors as replaceable clients and keeps language intelligence, builds, tests, debugging, file permissions, and MCP tools in Ali's modular developer stack. Updating an editor therefore does not remove Ali's coding capabilities.

## Notepad++

Open **Settings > Integrations**, close Notepad++, and choose **Install / repair Notepad++ toolkit**. Ali:

1. backs up the existing Notepad++ user configuration;
2. reads the official Notepad++ x64 plugin catalog;
3. verifies each downloaded package against the catalog SHA-256;
4. installs or repairs ComparePlus, JSON Tools, XML Tools, MarkdownViewer++, NppExec, DSpellCheck, CSV Lint, and Explorer; and
5. leaves themes, shortcuts, sessions, and editing preferences unchanged.

After a Notepad++ update, use **Refresh status**. If a plugin was removed or became incompatible, rerun **Install / repair**. Ali falls back to pinned, checksum-verified packages if the official catalog is temporarily unavailable.

## Visual Studio

Ali discovers Visual Studio through Microsoft's `vswhere` inventory instead of binding to a particular year or installation directory. A Visual Studio update or side-by-side installation is discovered by **Refresh status**.

Ali's coding layer already provides Roslyn semantic analysis, solution/project inspection, MSBuild, tests, coverage, profiling, CLR debugging, CMake, MSVC, LLVM tooling, LSP, and DAP. The Visual Studio IDE remains the interactive editor and can be launched by Ali for an approved solution, project, or document.

No Visual Studio registry hive or editor binary is patched. This keeps the connection serviceable across Visual Studio updates and preserves the IDE's own extension safety model.
