using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Webcam.DirectX11;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class TextureNativeFrameLease : IVisionFrame
{
	private nint _resource;

	private nint _d3d12SharedTextureHandle;

	private Direct3D11SharedTextureFrameLease? _d3d11SharedTextureFrame;

	private PooledFrameBuffer? _nv12PreviewBuffer;

	public nint Resource => _resource;

	public int Subresource { get; }

	public int Width { get; }

	public int Height { get; }

	public double FramesPerSecond { get; }

	public string DeviceMode { get; }

	public string MediaSubtype { get; }

	public long FrameNumber { get; }

	public long CapturedAtTimestamp { get; }

	public DateTime CapturedAtUtc { get; }

	public nint D3D12SharedTextureHandle =>
		_d3d11SharedTextureFrame?.TextureHandle
		?? _d3d12SharedTextureHandle;

	public nint D3D11ProducerFenceHandle =>
		_d3d11SharedTextureFrame?.ProducerFenceHandle
		?? IntPtr.Zero;

	public ulong D3D11ProducerFenceValue =>
		_d3d11SharedTextureFrame?.ProducerFenceValue
		?? 0uL;

	public byte[]? Nv12PreviewBytes => _nv12PreviewBuffer?.Bytes;

	public int Nv12PreviewStride { get; }

	public bool IsValid
	{
		get
		{
			if (_resource == IntPtr.Zero)
			{
				return _nv12PreviewBuffer != null;
			}
			return true;
		}
	}

	public TimeSpan Age => CapturedAtTimestamp == 0L
		? TimeSpan.MaxValue
		: Stopwatch.GetElapsedTime(CapturedAtTimestamp);

	internal TextureNativeFrameLease(nint resource, int subresource, int width, int height, double framesPerSecond, string deviceMode, string mediaSubtype, long frameNumber, nint d3d12SharedTextureHandle = 0, PooledFrameBuffer? nv12PreviewBuffer = null, int nv12PreviewStride = 0, long capturedAtTimestamp = 0L, DateTime capturedAtUtc = default, Direct3D11SharedTextureFrameLease? d3d11SharedTextureFrame = null)
	{
		_resource = resource;
		Subresource = subresource;
		Width = width;
		Height = height;
		FramesPerSecond = framesPerSecond;
		DeviceMode = deviceMode;
		MediaSubtype = mediaSubtype;
		FrameNumber = frameNumber;
		CapturedAtTimestamp = capturedAtTimestamp;
		CapturedAtUtc = capturedAtUtc == default ? DateTime.UtcNow : capturedAtUtc;
		_d3d12SharedTextureHandle = d3d12SharedTextureHandle;
		_d3d11SharedTextureFrame = d3d11SharedTextureFrame;
		_nv12PreviewBuffer = nv12PreviewBuffer;
		Nv12PreviewStride = nv12PreviewStride;
	}

	public TextureNativeFrameLease? Duplicate()
	{
		nint resource = _resource;
		PooledFrameBuffer? nv12PreviewBuffer = _nv12PreviewBuffer;
		if (resource == IntPtr.Zero && nv12PreviewBuffer == null)
		{
			return null;
		}
		if (resource != IntPtr.Zero)
		{
			Marshal.AddRef(resource);
		}
		nint duplicatedHandle = IntPtr.Zero;
		Direct3D11SharedTextureFrameLease? sharedTextureFrame = null;
		PooledFrameBuffer? pooledFrameBuffer = null;
		try
		{
			Direct3D11SharedTextureFrameLease? currentSharedTextureFrame =
				_d3d11SharedTextureFrame;
			if (currentSharedTextureFrame != null)
			{
				sharedTextureFrame = currentSharedTextureFrame.AddReference();
			}
			else if (D3D12SharedTextureHandle != IntPtr.Zero && !TryDuplicateHandle(D3D12SharedTextureHandle, out duplicatedHandle))
			{
				if (resource != IntPtr.Zero)
				{
					Marshal.Release(resource);
				}
				return null;
			}
			pooledFrameBuffer = nv12PreviewBuffer?.AddReference();
		}
		catch
		{
			pooledFrameBuffer?.Dispose();
			sharedTextureFrame?.Dispose();
			if (duplicatedHandle != IntPtr.Zero)
			{
				CloseHandle(duplicatedHandle);
			}
			if (resource != IntPtr.Zero)
			{
				Marshal.Release(resource);
			}
			throw;
		}
		return new TextureNativeFrameLease(resource, Subresource, Width, Height, FramesPerSecond, DeviceMode, MediaSubtype, FrameNumber, duplicatedHandle, pooledFrameBuffer, Nv12PreviewStride, CapturedAtTimestamp, CapturedAtUtc, sharedTextureFrame);
	}

	IVisionFrame? IVisionFrame.Duplicate()
	{
		return Duplicate();
	}

	public TextureNativeFrameLease? DuplicatePreviewData()
	{
		PooledFrameBuffer? nv12PreviewBuffer = _nv12PreviewBuffer;
		if (nv12PreviewBuffer == null)
		{
			return null;
		}
		return new TextureNativeFrameLease(IntPtr.Zero, Subresource, Width, Height, FramesPerSecond, DeviceMode, MediaSubtype, FrameNumber, IntPtr.Zero, nv12PreviewBuffer.AddReference(), Nv12PreviewStride, CapturedAtTimestamp, CapturedAtUtc);
	}

	internal nint DuplicateD3D11SharedTextureForDiagnostics()
	{
		return _d3d11SharedTextureFrame?.DuplicateTexture()
			?? IntPtr.Zero;
	}

	public void Dispose()
	{
		nint num = Interlocked.Exchange(ref _resource, IntPtr.Zero);
		if (num != IntPtr.Zero)
		{
			Marshal.Release(num);
		}
		Direct3D11SharedTextureFrameLease? sharedTextureFrame =
			Interlocked.Exchange(ref _d3d11SharedTextureFrame, null);
		nint sharedTextureHandle =
			Interlocked.Exchange(ref _d3d12SharedTextureHandle, IntPtr.Zero);
		if (sharedTextureFrame != null)
		{
			sharedTextureFrame.Dispose();
		}
		else
		{
			CloseOwnedSharedTextureHandle(sharedTextureHandle);
		}
		Interlocked.Exchange(ref _nv12PreviewBuffer, null)?.Dispose();
	}

	internal static void CloseOwnedSharedTextureHandle(nint handle)
	{
		if (handle != IntPtr.Zero)
		{
			CloseHandle(handle);
		}
	}

	private static bool TryDuplicateHandle(nint sourceHandle, out nint duplicatedHandle)
	{
		nint currentProcess = GetCurrentProcess();
		return DuplicateHandle(currentProcess, sourceHandle, currentProcess, out duplicatedHandle, 0u, inheritHandle: false, 2u);
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DuplicateHandle(nint sourceProcessHandle, nint sourceHandle, nint targetProcessHandle, out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint handle);
}
