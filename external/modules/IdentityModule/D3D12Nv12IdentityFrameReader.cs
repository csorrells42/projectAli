using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Reads a native NV12 camera texture on the identity worker. The camera
/// callback only hands off a reference; GPU completion and the single NV12 to
/// BGR conversion happen here, off the display lane.
/// </summary>
internal sealed class D3D12Nv12IdentityFrameReader : IDisposable
{
	private static readonly TimeSpan GpuTimeout =
		TimeSpan.FromMilliseconds(500);

	private readonly ID3D12Device _device;

	private readonly ID3D12CommandQueue _queue;

	private readonly ID3D12CommandAllocator _allocator;

	private readonly ID3D12GraphicsCommandList _commands;

	private readonly ID3D12Fence _fence;

	private readonly AutoResetEvent _fenceEvent = new(false);

	private ID3D12Fence? _d3d11ProducerFence;

	private nint _d3d11ProducerFenceHandle;

	private ulong _lastD3D11ProducerFenceValue;

	private ID3D12Resource? _readback;

	private readonly Dictionary<nint, ID3D12Resource>
		_sharedFrameResources = [];

	private PlacedSubresourceFootPrint _yFootprint;

	private PlacedSubresourceFootPrint _uvFootprint;

	private int _width;

	private int _height;

	private ulong _fenceValue;

	private bool _disposed;

	public D3D12Nv12IdentityFrameReader(TextureNativeFrameLease firstFrame)
	{
		_device = CreateCompatibleDevice(firstFrame);
		_queue = _device.CreateCommandQueue<ID3D12CommandQueue>(
			new CommandQueueDescription(CommandListType.Direct));
		_allocator =
			_device.CreateCommandAllocator<ID3D12CommandAllocator>(
				CommandListType.Direct);
		_commands =
			_device.CreateCommandList<ID3D12GraphicsCommandList>(
				0u,
				CommandListType.Direct,
				_allocator);
		_commands.Close();
		_fence = _device.CreateFence<ID3D12Fence>(0uL);
	}

	public bool CanRead(TextureNativeFrameLease frame)
	{
		if (_disposed
			|| !frame.MediaSubtype.Contains(
				"NV12",
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!IsNativeD3D12Frame(frame))
		{
			return frame.D3D12SharedTextureHandle != IntPtr.Zero;
		}
		try
		{
			using ID3D12Resource resource = Wrap(frame);
			using ID3D12Device frameDevice =
				resource.GetDevice<ID3D12Device>();
			return frameDevice.NativePointer == _device.NativePointer;
		}
		catch
		{
			return false;
		}
	}

	public unsafe Mat ReadBgr(TextureNativeFrameLease frame)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ID3D12Resource source = OpenFrameResource(frame);
		bool disposeSource = IsNativeD3D12Frame(frame);
		try
		{
			EnsureReadback(
				source.Description,
				frame.Width,
				frame.Height);
			ID3D12Resource readback = _readback
				?? throw new InvalidOperationException(
					"Identity NV12 readback buffer is unavailable.");

			_allocator.Reset();
			_commands.Reset(_allocator);
			WaitForD3D11Producer(frame);
			ResourceBarrier toCopy = ResourceBarrier.BarrierTransition(
				source,
				ResourceStates.Common,
				ResourceStates.CopySource);
			_commands.ResourceBarrier(toCopy);
			_commands.CopyTextureRegion(
				new TextureCopyLocation(readback, _yFootprint),
				0u,
				0u,
				0u,
				new TextureCopyLocation(source, 0u));
			_commands.CopyTextureRegion(
				new TextureCopyLocation(readback, _uvFootprint),
				0u,
				0u,
				0u,
				new TextureCopyLocation(source, 1u));
			ResourceBarrier toCommon = ResourceBarrier.BarrierTransition(
				source,
				ResourceStates.CopySource,
				ResourceStates.Common);
			_commands.ResourceBarrier(toCommon);
			_commands.Close();
			_queue.ExecuteCommandList(_commands);
			ulong completion = ++_fenceValue;
			_queue.Signal(_fence, completion);
			WaitFor(completion);

			void* mapped = null;
			readback.Map(0u, null, &mapped).CheckError();
			try
			{
				using Mat y = Mat.FromPixelData(
					_height,
					_width,
					MatType.CV_8UC1,
					(nint)((byte*)mapped + _yFootprint.Offset),
					_yFootprint.Footprint.RowPitch);
				using Mat uv = Mat.FromPixelData(
					Math.Max(1, _height / 2),
					Math.Max(1, _width / 2),
					MatType.CV_8UC2,
					(nint)((byte*)mapped + _uvFootprint.Offset),
					_uvFootprint.Footprint.RowPitch);
				Mat bgr = new();
				Cv2.CvtColorTwoPlane(
					y,
					uv,
					bgr,
					ColorConversionCodes.YUV2BGR_NV12);
				return bgr;
			}
			finally
			{
				readback.Unmap(0u);
			}
		}
		finally
		{
			if (disposeSource)
			{
				source.Dispose();
			}
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_readback?.Dispose();
		foreach (ID3D12Resource resource
			in _sharedFrameResources.Values)
		{
			resource.Dispose();
		}
		_sharedFrameResources.Clear();
		_d3d11ProducerFence?.Dispose();
		_d3d11ProducerFence = null;
		_d3d11ProducerFenceHandle = IntPtr.Zero;
		_fence.Dispose();
		_fenceEvent.Dispose();
		_commands.Dispose();
		_allocator.Dispose();
		_queue.Dispose();
		_device.Dispose();
	}

	private void EnsureReadback(
		ResourceDescription description,
		int width,
		int height)
	{
		if (_readback is not null
			&& width == _width
			&& height == _height)
		{
			return;
		}
		_readback?.Dispose();
		PlacedSubresourceFootPrint[] footprints =
			new PlacedSubresourceFootPrint[2];
		uint[] rows = new uint[2];
		ulong[] rowSizes = new ulong[2];
		_device.GetCopyableFootprints(
			description,
			0u,
			2u,
			0uL,
			footprints,
			rows,
			rowSizes,
			out ulong totalBytes);
		_yFootprint = footprints[0];
		_uvFootprint = footprints[1];
		_readback = _device.CreateCommittedResource<ID3D12Resource>(
			new HeapProperties(HeapType.Readback),
			HeapFlags.None,
			ResourceDescription.Buffer(totalBytes),
			ResourceStates.CopyDest);
		_width = width;
		_height = height;
	}

	private void WaitFor(ulong fenceValue)
	{
		if (_fence.CompletedValue >= fenceValue)
		{
			return;
		}
		_fence.SetEventOnCompletion(fenceValue, _fenceEvent);
		if (!_fenceEvent.WaitOne(GpuTimeout))
		{
			throw new TimeoutException(
				"Identity GPU readback did not complete within " +
				$"{GpuTimeout.TotalMilliseconds:0} ms.");
		}
	}

	private void WaitForD3D11Producer(TextureNativeFrameLease frame)
	{
		nint producerFenceHandle = frame.D3D11ProducerFenceHandle;
		ulong producerFenceValue = frame.D3D11ProducerFenceValue;
		if (producerFenceHandle == IntPtr.Zero
			|| producerFenceValue == 0uL)
		{
			return;
		}
		bool generationChanged = _d3d11ProducerFence is null
			|| _d3d11ProducerFenceHandle != producerFenceHandle
			|| producerFenceValue < _lastD3D11ProducerFenceValue;
		if (generationChanged)
		{
			_d3d11ProducerFence?.Dispose();
			_d3d11ProducerFence =
				_device.OpenSharedHandle<ID3D12Fence>(
					producerFenceHandle);
			_d3d11ProducerFenceHandle = producerFenceHandle;
		}
		_lastD3D11ProducerFenceValue = producerFenceValue;
		_queue.Wait(_d3d11ProducerFence, producerFenceValue);
	}

	private static ID3D12Resource Wrap(TextureNativeFrameLease frame)
	{
		if (frame.Resource == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				"Identity frame does not carry a native D3D12 texture.");
		}
		Marshal.AddRef(frame.Resource);
		return new ID3D12Resource(frame.Resource);
	}

	private ID3D12Resource OpenFrameResource(
		TextureNativeFrameLease frame)
	{
		if (IsNativeD3D12Frame(frame))
		{
			return Wrap(frame);
		}

		nint handle = frame.D3D12SharedTextureHandle;
		if (handle == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				"Identity frame has no shared GPU texture.");
		}
		if (!_sharedFrameResources.TryGetValue(
			handle,
			out ID3D12Resource? resource))
		{
			resource = _device.OpenSharedHandle<ID3D12Resource>(handle);
			_sharedFrameResources.Add(handle, resource);
		}
		return resource;
	}

	private static ID3D12Device CreateCompatibleDevice(
		TextureNativeFrameLease frame)
	{
		if (IsNativeD3D12Frame(frame))
		{
			using ID3D12Resource resource = Wrap(frame);
			return resource.GetDevice<ID3D12Device>();
		}
		if (frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			return D3D12CreateDevice<ID3D12Device>(
				null,
				FeatureLevel.Level_12_0);
		}
		throw new InvalidOperationException(
			"Identity requires a native or shared NV12 GPU texture.");
	}

	private static bool IsNativeD3D12Frame(
		TextureNativeFrameLease frame)
	{
		return frame.Resource != IntPtr.Zero
			&& frame.DeviceMode.StartsWith(
				"D3D12",
				StringComparison.OrdinalIgnoreCase);
	}
}
