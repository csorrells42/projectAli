using System;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Viewports.Contracts;

/// <summary>
/// Immutable decorator chain. Empty means no overlay; each call to Decorate
/// adds one independent layer without modifying any existing layer.
/// </summary>
public sealed record PreviewOverlayStack : IVisionOverlay
{
	public static PreviewOverlayStack Empty { get; } = new(null, null);

	public IPreviewOverlay? Layer { get; }

	public PreviewOverlayStack? Next { get; }

	public bool HasContent
	{
		get
		{
			for (PreviewOverlayStack? node = this;
				node?.Layer is not null;
				node = node.Next)
			{
				if (node.Layer.IsFresh
					&& node.Layer.HasContent)
				{
					return true;
				}
			}
			return false;
		}
	}

	public bool IsBoundToFrame(long frameId)
	{
		for (PreviewOverlayStack? node = this;
			node?.Layer is not null;
			node = node.Next)
		{
			if (node.Layer.FrameId != frameId)
			{
				return false;
			}
		}
		return true;
	}

	private PreviewOverlayStack(
		IPreviewOverlay? layer,
		PreviewOverlayStack? next)
	{
		Layer = layer;
		Next = next;
	}

	public PreviewOverlayStack Decorate(IPreviewOverlay layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		return new PreviewOverlayStack(layer, this);
	}

	public bool TryGet<TOverlay>(out TOverlay? overlay)
		where TOverlay : class, IPreviewOverlay
	{
		for (PreviewOverlayStack? node = this;
			node?.Layer is not null;
			node = node.Next)
		{
			if (node.Layer is TOverlay match)
			{
				overlay = match;
				return true;
			}
		}
		overlay = null;
		return false;
	}
}
