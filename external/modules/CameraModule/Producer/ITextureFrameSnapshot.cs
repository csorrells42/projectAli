using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.Producer;

/// <summary>
/// A completed immutable pipeline result that retains its original camera
/// texture. Implementations own the lease and release it on disposal.
/// </summary>
public interface ITextureFrameSnapshot : IFramePipelineSnapshot
{
	TextureFrameReference TextureReference { get; }

	TextureNativeFrameLease OriginalFrame { get; }
}
