using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

/// <summary>
/// MediaPipe-owned adapter from an immutable camera NV12 texture to the
/// CPU image accepted by the official MediaPipe Tasks API. GPU completion,
/// readback, and color conversion occur only on the MediaPipe worker.
/// </summary>
internal sealed class MediaPipeOfficialTextureFrameReader : IDisposable
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

	private PlacedSubresourceFootPrint _yFootprint;

	private PlacedSubresourceFootPrint _uvFootprint;

	private int _width;

	private int _height;

	private ulong _fenceValue;

	private bool _disposed;

	public MediaPipeOfficialTextureFrameReader(
		TextureNativeFrameLease firstFrame)
	{
		ArgumentNullException.ThrowIfNull(firstFrame);
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
		if (IsNativeD3D12Resource(frame))
		{
			try
			{
				using ID3D12Resource resource = Wrap(frame.Resource);
				using ID3D12Device frameDevice =
					resource.GetDevice<ID3D12Device>();
				return frameDevice.NativePointer == _device.NativePointer;
			}
			catch
			{
				return false;
			}
		}
		return frame.D3D12SharedTextureHandle != IntPtr.Zero;
	}

	public unsafe BitmapSource ReadBgra(
		TextureNativeFrameLease frame,
		int maximumDimension)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		using ID3D12Resource source = OpenFrameResource(frame);
		EnsureReadback(source.Description, frame.Width, frame.Height);
		ID3D12Resource readback = _readback
			?? throw new InvalidOperationException(
				"MediaPipe NV12 readback buffer is unavailable.");

		_allocator.Reset();
		_commands.Reset(_allocator);
		WaitForD3D11Producer(frame);
		_commands.ResourceBarrier(
			ResourceBarrier.BarrierTransition(
				source,
				ResourceStates.Common,
				ResourceStates.CopySource));
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
		_commands.ResourceBarrier(
			ResourceBarrier.BarrierTransition(
				source,
				ResourceStates.CopySource,
				ResourceStates.Common));
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
			using Mat bgra = new();
			Cv2.CvtColorTwoPlane(
				y,
				uv,
				bgra,
				ColorConversionCodes.YUV2BGRA_NV12);

			using Mat resized = new();
			Mat output = bgra;
			int largestDimension = Math.Max(bgra.Width, bgra.Height);
			if (maximumDimension > 0
				&& largestDimension > maximumDimension)
			{
				double scale =
					(double)maximumDimension / largestDimension;
				Cv2.Resize(
					bgra,
					resized,
					new OpenCvSharp.Size(
						Math.Max(1, (int)Math.Round(bgra.Width * scale)),
						Math.Max(1, (int)Math.Round(bgra.Height * scale))),
					0d,
					0d,
					InterpolationFlags.Linear);
				output = resized;
			}

			int stride = checked((int)output.Step());
			int bufferSize = checked(stride * output.Height);
			BitmapSource bitmap = BitmapSource.Create(
				output.Width,
				output.Height,
				96d,
				96d,
				PixelFormats.Bgra32,
				null,
				output.Data,
				bufferSize,
				stride);
			bitmap.Freeze();
			return bitmap;
		}
		finally
		{
			readback.Unmap(0u);
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
		_d3d11ProducerFence?.Dispose();
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
				"MediaPipe GPU readback did not complete within " +
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

	private ID3D12Resource OpenFrameResource(
		TextureNativeFrameLease frame)
	{
		if (IsNativeD3D12Resource(frame))
		{
			return Wrap(frame.Resource);
		}
		if (frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			return _device.OpenSharedHandle<ID3D12Resource>(
				frame.D3D12SharedTextureHandle);
		}
		throw new InvalidOperationException(
			"The MediaPipe frame does not carry an NV12 GPU texture.");
	}

	private static ID3D12Device CreateCompatibleDevice(
		TextureNativeFrameLease frame)
	{
		if (IsNativeD3D12Resource(frame))
		{
			using ID3D12Resource resource = Wrap(frame.Resource);
			return resource.GetDevice<ID3D12Device>();
		}
		if (frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			return D3D12CreateDevice<ID3D12Device>(
				null,
				FeatureLevel.Level_12_0);
		}
		throw new InvalidOperationException(
			"The first MediaPipe frame does not carry a D3D12 texture.");
	}

	private static bool IsNativeD3D12Resource(
		TextureNativeFrameLease frame)
	{
		return frame.Resource != IntPtr.Zero
			&& frame.DeviceMode.StartsWith(
				"D3D12",
				StringComparison.OrdinalIgnoreCase);
	}

	private static ID3D12Resource Wrap(nint resource)
	{
		Marshal.AddRef(resource);
		return new ID3D12Resource(resource);
	}
}
