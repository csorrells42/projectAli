using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Viewports.Contracts;

/// <summary>
/// The single synchronous package accepted by a viewport: one immutable
/// original frame plus its completed decorator stack.
/// </summary>
public interface IOverlayFrameSnapshot : ITextureFrameSnapshot
{
	PreviewOverlayStack Overlays { get; }
}
