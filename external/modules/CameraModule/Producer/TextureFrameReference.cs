using System;
using System.Threading;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.Producer;

/// <summary>
/// One shared immutable native-frame owner for an entire pipeline generation.
/// Module snapshots retain this object by atomic reference count; they do not
/// duplicate the native texture or allocate another frame wrapper.
/// </summary>
public sealed class TextureFrameReference
{
	private TextureNativeFrameLease? _frame;

	private int _referenceCount = 1;

	public TextureNativeFrameLease Frame =>
		Volatile.Read(ref _frame)
		?? throw new ObjectDisposedException(nameof(TextureFrameReference));

	internal TextureFrameReference(TextureNativeFrameLease frame)
	{
		_frame = frame ?? throw new ArgumentNullException(nameof(frame));
	}

	public TextureFrameReference AddReference()
	{
		while (true)
		{
			int references = Volatile.Read(ref _referenceCount);
			if (references <= 0)
			{
				throw new ObjectDisposedException(
					nameof(TextureFrameReference));
			}
			if (references == int.MaxValue)
			{
				throw new InvalidOperationException(
					"The texture reference has too many owners.");
			}
			if (Interlocked.CompareExchange(
				ref _referenceCount,
				references + 1,
				references) == references)
			{
				return this;
			}
		}
	}

	public void Release()
	{
		int remaining = Interlocked.Decrement(ref _referenceCount);
		if (remaining > 0)
		{
			return;
		}
		if (remaining < 0)
		{
			throw new InvalidOperationException(
				"A texture reference was released twice.");
		}
		Interlocked.Exchange(ref _frame, null)?.Dispose();
	}
}
