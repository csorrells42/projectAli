using System;

namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class VideoFrameDenoiser
{
	private byte[]? _previousFrame;

	public void Reset()
	{
		_previousFrame = null;
	}

	public void Apply(byte[] current, double strength)
	{
		ApplyTemporalDenoise(current, strength, ref _previousFrame);
	}

	public static void ApplyTemporalDenoise(byte[] current, double strength, ref byte[]? previousFrame)
	{
		byte[]? array = previousFrame;
		if (array == null || array.Length != current.Length)
		{
			previousFrame = (byte[])current.Clone();
			return;
		}
		int num = (int)Math.Round(Math.Clamp(strength / 12.0, 0.05, 0.62) * 256.0);
		int currentWeight = 256 - num;
		for (int i = 0; i < current.Length; i += 4)
		{
			current[i] = Blend(current[i], array[i], currentWeight, num);
			current[i + 1] = Blend(current[i + 1], array[i + 1], currentWeight, num);
			current[i + 2] = Blend(current[i + 2], array[i + 2], currentWeight, num);
			current[i + 3] = byte.MaxValue;
		}
		Buffer.BlockCopy(current, 0, array, 0, current.Length);
	}

	private static byte Blend(byte current, byte previous, int currentWeight, int previousWeight)
	{
		return (byte)(current * currentWeight + previous * previousWeight + 128 >> 8);
	}
}
