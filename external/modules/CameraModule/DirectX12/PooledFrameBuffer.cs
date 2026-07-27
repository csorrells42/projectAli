using System;
using System.Buffers;
using System.Threading;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal sealed class PooledFrameBuffer : IDisposable
{
	private byte[]? _bytes;

	private int _referenceCount = 1;

	public byte[] Bytes => _bytes ?? throw new ObjectDisposedException("PooledFrameBuffer");

	private PooledFrameBuffer(int minimumLength)
	{
		_bytes = ArrayPool<byte>.Shared.Rent(minimumLength);
	}

	public static PooledFrameBuffer Rent(int minimumLength)
	{
		return new PooledFrameBuffer(minimumLength);
	}

	public PooledFrameBuffer AddReference()
	{
		if (Interlocked.Increment(ref _referenceCount) <= 1)
		{
			Interlocked.Decrement(ref _referenceCount);
			throw new ObjectDisposedException("PooledFrameBuffer");
		}
		return this;
	}

	public void Dispose()
	{
		if (Interlocked.Decrement(ref _referenceCount) == 0)
		{
			byte[]? array = Interlocked.Exchange(ref _bytes, null);
			if (array != null)
			{
				ArrayPool<byte>.Shared.Return(array);
			}
		}
	}
}
