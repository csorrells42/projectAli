# MediaPipe portable runtime asset

Ali's camera uses an embedded CPU-only Python runtime for the official
MediaPipe Tasks Face Landmarker. The local asset must contain:

- `python\python.exe`
- `python-packages\mediapipe\__init__.py`

The large runtime folders are intentionally ignored by Git. `src\Ali.csproj`
copies them to `runtime\python` and `runtime\python-packages` for build and
publish, and stops a portable publish if either required asset is absent.
