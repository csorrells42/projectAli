# Viewport Module

Namespace root: `AvatarBuilder.Modules.Viewports`

The viewport is an optional reusable terminal module. It consumes one final
immutable `IOverlayFrameSnapshot`, presents its original GPU texture, and draws
its completed `PreviewOverlayStack`.

Overlay rules:

- Every layer implements `IPreviewOverlay`.
- `PreviewOverlayStack.Empty` means camera with no overlay.
- The composite overlay module adds the selected immutable layers; existing layers are never modified.
- The renderer walks the decorator chain bottom-to-top.
- Tracking, identity, face mesh, the always-on MediaPipe-head-pose Attention indicator, and future layers use the same viewport contract and arrive in one final frame package.
- The viewport never calls camera, MediaPipe, identity, or application code.

The optional `PipelineTimingWindow` requests one controller report per second
while open and displays each module's current `TimeWaited` and `TimeWorked`.
Closing the popup stops all timing reads.

GPU lifetime rule:

The D3D11/D3D12 shared source lease and its private NV12 shader-descriptor pair
belong to a fixed DX12 frame-resource slot until that slot's completion fence
passes. CPU command submission is not GPU completion, so neither may be reused
when `ExecuteCommandList` returns.

`PreviewOverlayStackSelfTest` verifies the empty configuration, common interface,
decorator ordering, and layer lookup.
