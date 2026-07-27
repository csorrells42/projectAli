# Vendored Ali modules

These module projects are source dependencies of `src/Ali.csproj`. They live
inside this repository so restore and build never depend on sibling folders on
the original development machine.

The snapshot includes all source, project files, shaders, sidecar scripts, and
small runtime assets required by the projects. Optional local model folders for
speaker recognition, wake-word detection, and Parakeet transcription are not
required to compile and remain conditional in their project files; deployment
builds may supply those models separately.

All module-to-module `ProjectReference` paths are relative to this directory.
