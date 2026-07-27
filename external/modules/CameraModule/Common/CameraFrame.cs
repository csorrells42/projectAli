using System;
using System.Buffers;
using System.Threading;

namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class CameraFrame : IDisposable
{
	private sealed class PooledBufferOwner
	{
		private int _referenceCount = 1;

		public byte[] Buffer { get; }

		public PooledBufferOwner(int byteCount)
		{
			Buffer = ArrayPool<byte>.Shared.Rent(byteCount);
		}

		public void AddReference()
		{
			int num;
			do
			{
				num = Volatile.Read(in _referenceCount);
				if (num <= 0)
				{
					throw new ObjectDisposedException("PooledBufferOwner");
				}
			}
			while (Interlocked.CompareExchange(ref _referenceCount, num + 1, num) != num);
		}

		public void Release()
		{
			if (Interlocked.Decrement(ref _referenceCount) == 0)
			{
				ArrayPool<byte>.Shared.Return(Buffer);
			}
		}
	}

	private readonly PooledBufferOwner? _pooledOwner;

	private int _disposed;

	public byte[] BgraBytes { get; }

	public byte[]? Nv12Bytes { get; }

	public int Width { get; }

	public int Height { get; }

	public int Stride { get; }

	public int Nv12Stride { get; }

	public string Format { get; }

	public bool HasBgra
	{
		get
		{
			if (BgraBytes.Length != 0)
			{
				return Stride > 0;
			}
			return false;
		}
	}

	public bool HasNv12
	{
		get
		{
		byte[]? nv12Bytes = Nv12Bytes;
			if (nv12Bytes != null && nv12Bytes.Length > 0)
			{
				return Nv12Stride > 0;
			}
			return false;
		}
	}

	public CameraFrame(byte[] bgraBytes, int width, int height, int stride)
		: this(bgraBytes, width, height, stride, null, 0, "bgra32")
	{
	}

	public CameraFrame(byte[] bgraBytes, int width, int height, int stride, byte[]? nv12Bytes, int nv12Stride, string format)
		: this(bgraBytes, width, height, stride, nv12Bytes, nv12Stride, format, null)
	{
	}

	private CameraFrame(byte[] bgraBytes, int width, int height, int stride, byte[]? nv12Bytes, int nv12Stride, string format, PooledBufferOwner? pooledOwner)
	{
		BgraBytes = bgraBytes;
		Width = width;
		Height = height;
		Stride = stride;
		Nv12Bytes = nv12Bytes;
		Nv12Stride = nv12Stride;
		Format = format;
		_pooledOwner = pooledOwner;
	}

	public static CameraFrame RentNv12(int width, int height, int stride, string format = "nv12")
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, "width");
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, "height");
		ArgumentOutOfRangeException.ThrowIfLessThan(stride, width, "stride");
		if ((height & 1) != 0)
		{
			throw new ArgumentException("NV12 frame height must be even.", "height");
		}
		PooledBufferOwner pooledBufferOwner = new PooledBufferOwner(checked(stride * height * 3) / 2);
		return new CameraFrame(Array.Empty<byte>(), width, height, 0, pooledBufferOwner.Buffer, stride, format, pooledBufferOwner);
	}

	public static CameraFrame RentBgra(int width, int height, int stride, string format = "bgra32")
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, "width");
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, "height");
		ArgumentOutOfRangeException.ThrowIfLessThan(stride, checked(width * 4), "stride");
		PooledBufferOwner pooledBufferOwner = new PooledBufferOwner(checked(stride * height));
		return new CameraFrame(pooledBufferOwner.Buffer, width, height, stride, null, 0, format, pooledBufferOwner);
	}

	public CameraFrame Duplicate()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(in _disposed) != 0, this);
		_pooledOwner?.AddReference();
		return new CameraFrame(BgraBytes, Width, Height, Stride, Nv12Bytes, Nv12Stride, Format, _pooledOwner);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			_pooledOwner?.Release();
		}
	}
}
