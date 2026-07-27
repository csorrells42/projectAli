# Vision MediaPipe

Namespace: `AvatarBuilder.Modules.Vision.MediaPipe`

This folder owns the MediaPipe Face Landmarker integration behind the shared landmark tracker contracts. Avatar Builder uses MediaPipe as a local sidecar/backend, not as UI logic.

Reference material:

- `https://developers.google.com/edge/mediapipe/solutions/guide`: official MediaPipe Solutions guide.
- `https://developers.google.com/edge/mediapipe/solutions/vision/face_landmarker`: official Face Landmarker guide.
- The current MediaPipe Solutions direction is Tasks plus packaged models. Legacy Face Mesh and Iris are listed as upgraded into Face Landmark detection, so new work should target Face Landmarker rather than older legacy APIs.
- Face Landmarker supports still images, decoded video frames, and live video streams. The live stream mode returns results asynchronously, which matches this folder's sidecar/client boundary.
- Face Landmarker can output a face mesh, blendshape scores, and facial transformation matrices. The app uses those outputs for feature and mesh review, blink, jaw, and mouth measurements, overlays, capture quality, and measured multiview geometry.

Implementation rules:

- Keep Python, MediaPipe Tasks, and model-bundle details inside this module.
- Keep the model file under `dependencies/vision/dense-face-landmarks` so the app remains portable.
- The sole inference implementation is the official MediaPipe Tasks Face Landmarker on the CPU. Windows GPU/DirectML experiments are not part of this module.
- The module reads the immutable camera-owned D3D12 NV12 texture on its own worker, converts only the accepted frame, and submits that private bitmap to MediaPipe Tasks.
- Inference startup and recovery happen on the MediaPipe worker, never on camera acquisition or display.
- The sidecar intentionally uses explicit, slightly tolerant face detection/presence/tracking thresholds so glasses, partially closed eyes, lower-resolution frames, and camera movement do not drop dense lock as quickly as MediaPipe's defaults did in proof clips.
- Live image transport uses one reusable Windows named-memory surface per tracker. C# writes raw BGRA pixels directly into that surface and sends only dimensions, timing, and the mapping name through the JSON control channel. Do not reintroduce JPEG/Base64 frame transport; it adds compression latency, allocation pressure, and roughly one-third wire expansion before inference.
- The MediaPipe tracker owns its sidecar client and named-memory surface; it never shares mutable image buffers or retains a frame backlog.
- A tracker accepts work only while its analysis slot is empty. Busy arrivals are discarded before pixel conversion or shared-memory copying, and accepted work is never replaced. Do not add a latest-frame mailbox or a waiting queue.
- Treat blendshape evidence as corroboration unless quality/reliability gates say it is safe to use.
- If future code uses transformation matrices for 3D preview or avatar alignment, expose them through `Vision.Common` or `Vision.Reconstruction` DTOs rather than leaking sidecar JSON into app code.
Validation:

- The module self-test validates atomic latest-value publication and immutable camera-output lineage without requiring a physical camera.
- Avatar Builder's VisionSmoke suite validates the official tracker contracts, geometry mapping, pipeline timing, and architecture boundaries.
