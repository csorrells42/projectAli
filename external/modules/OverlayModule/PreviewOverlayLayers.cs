using System;
using System.Threading;

namespace AvatarBuilder.Modules.Vision.Overlays;

[Flags]
public enum PreviewOverlayLayers
{
	None = 0,
	Tracking = 1 << 0,
	FaceMesh = 1 << 1
}

/// <summary>
/// One atomic overlay-selection word. The first overlay stage captures it into
/// each frame so every later decorator makes its decision from the same
/// selection, even when the user changes the menu while a frame is in flight.
/// </summary>
public sealed class PreviewOverlaySelection
{
	private int _enabledLayers;

	public PreviewOverlayLayers EnabledLayers =>
		(PreviewOverlayLayers)Volatile.Read(ref _enabledLayers);

	public PreviewOverlaySelection(
		PreviewOverlayLayers enabledLayers)
	{
		_enabledLayers = (int)enabledLayers;
	}

	public bool IsEnabled(PreviewOverlayLayers layer)
	{
		return (EnabledLayers & layer) != 0;
	}

	public void Set(
		PreviewOverlayLayers layer,
		bool enabled)
	{
		int layerBits = (int)layer;
		while (true)
		{
			int current = Volatile.Read(ref _enabledLayers);
			int updated = enabled
				? current | layerBits
				: current & ~layerBits;
			if (current == updated
				|| Interlocked.CompareExchange(
					ref _enabledLayers,
					updated,
					current) == current)
			{
				return;
			}
		}
	}

	public void Replace(PreviewOverlayLayers enabledLayers)
	{
		Volatile.Write(
			ref _enabledLayers,
			(int)enabledLayers);
	}
}
