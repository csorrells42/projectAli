namespace AvatarBuilder.Modules.Viewports.Contracts;

/// <summary>
/// One immutable viewport decoration. Renderers and viewports consume only
/// this contract; feature modules remain unknown to them.
/// </summary>
public interface IPreviewOverlay
{
	long FrameId { get; }

	bool IsFresh { get; }

	bool HasContent { get; }
}
