# Ali Multi-Language Coding Architecture

## Purpose

Ali's C# implementation remains the proven reference provider. Other languages plug into a
shared protocol and evidence foundation instead of duplicating Roslyn-specific classes or
adding English-routing rules.

## Shared foundation

- `Languages/AliLanguageProjectResolver` resolves only approved workstation files, identifies
  recognized manifests or source documents, and prevents project-document escape.
- `Languages/AliLanguageProviderRegistry` selects a provider from the resolved manifest and
  language. The model does not select an executable or arbitrary command.
- `Protocols/LanguageServer` hosts trusted provider-resolved Language Server Protocol adapters.
- `Protocols/DebugAdapter` hosts trusted provider-resolved Debug Adapter Protocol adapters.
- `Indexing/AliSourceIndexService` provides a bounded structural fallback for incomplete code
  and projects whose semantic server is unavailable.
- `Infrastructure/AliBoundedProcessRunner` executes fixed toolchains without a command shell.

## Stable model and MCP surface

The shared high-level tools are:

- `coding_list_capabilities`
- `coding_inspect_project`
- `coding_index_project`
- `coding_search_symbols`
- `coding_analyze_project`
- `coding_format_project`
- `coding_build_project`
- `coding_test_project`

Provider-specific expert tools may coexist when a language offers unique functionality, such as
Roslyn solution refactoring. Generic tools stay stable as Python, web, Java, and C++ providers are
added.

## Security and permissions

- Project and document paths must resolve through Ali's approved virtual workstation mounts.
- Reparse points and project-root escapes are rejected.
- Toolchain executables are resolved by provider code from environment variables, project-local
  wrappers, bundled runtime assets, or `PATH`; the model never supplies an executable.
- Process arguments use `ProcessStartInfo.ArgumentList`; no shell is invoked.
- Protocol messages are limited to 16 MiB.
- Source indexing is limited to 5,000 files, 64 MiB total, and 2 MiB per file, and skips generated,
  dependency, cache, and build folders.
- Format, build, and test operations retain Agent Framework permission gates and are exposed to
  MCP disabled by default.

## Provider contract

Each provider declares its languages, capabilities, and current toolchain availability, and
implements analyze, format, build, and test. Execution, debugging, profiling, architecture, and
release operations use companion interfaces as those shared checkpoints are completed.

The live capability report is authoritative. Ali must consult it before claiming she cannot
inspect, build, test, run, or debug code.
