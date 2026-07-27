using System;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.MediaFoundation;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace AvatarBuilder.Modules.Webcam.DirectX11;

internal sealed class Direct3D11SharedTextureFrameLease : IDisposable
{
	private Direct3D11SharedTextureBridge? _owner;

	private readonly int _slotIndex;

	private int _referenceCount = 1;

	public nint TextureHandle { get; }

	public nint ProducerFenceHandle { get; }

	public ulong ProducerFenceValue { get; }

	internal Direct3D11SharedTextureFrameLease(
		Direct3D11SharedTextureBridge owner,
		int slotIndex,
		nint textureHandle,
		nint producerFenceHandle,
		ulong producerFenceValue)
	{
		_owner = owner;
		_slotIndex = slotIndex;
		TextureHandle = textureHandle;
		ProducerFenceHandle = producerFenceHandle;
		ProducerFenceValue = producerFenceValue;
	}

	public Direct3D11SharedTextureFrameLease AddReference()
	{
		if (Interlocked.Increment(ref _referenceCount) <= 1)
		{
			Interlocked.Decrement(ref _referenceCount);
			throw new ObjectDisposedException(nameof(Direct3D11SharedTextureFrameLease));
		}
		return this;
	}

	internal nint DuplicateTexture()
	{
		Direct3D11SharedTextureBridge? owner = Volatile.Read(ref _owner);
		if (owner == null)
		{
			throw new ObjectDisposedException(
				nameof(Direct3D11SharedTextureFrameLease));
		}
		return owner.DuplicateSlotTexture(_slotIndex);
	}

	public void Dispose()
	{
		if (Interlocked.Decrement(ref _referenceCount) == 0)
		{
			Interlocked.Exchange(ref _owner, null)?.ReleaseSlot(_slotIndex);
		}
	}
}

internal sealed class Direct3D11SharedTextureBridge : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void CopySubresourceRegionDelegate(
		nint context,
		nint destinationResource,
		uint destinationSubresource,
		uint destinationX,
		uint destinationY,
		uint destinationZ,
		nint sourceResource,
		uint sourceSubresource,
		nint sourceBox);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void FlushDelegate(nint context);

	private const int CopySubresourceRegionSlot = 46;

	private const int FlushSlot = 111;

	// The immutable pipeline retains separate working and published snapshots
	// throughout the configured module chain, while the viewport can retain
	// additional GPU in-flight frames. Four slots starved a 30 FPS source down
	// to roughly 10 FPS. This fixed pool is allocated once at stream startup.
	internal const int SharedSlotCount = 24;

	private sealed class SharedSlot
	{
		public ID3D11Texture2D? D3D11Texture;

		public ID3D12Resource? D3D12Texture;

		public nint Texture =>
			D3D11Texture?.NativePointer
			?? IntPtr.Zero;

		public nint SharedHandle;

		public int InUse;
	}

	private readonly object _disposeLock = new();

	private nint _device;

	private nint _context;

	private readonly CopySubresourceRegionDelegate _copySubresourceRegion;

	private readonly FlushDelegate _flush;

	private ID3D11Device5? _device5;

	private ID3D11Device1? _device1;

	private ID3D11DeviceContext4? _context4;

	private ID3D12Device? _d3d12Device;

	private ID3D11Fence? _producerFence;

	private nint _producerFenceSharedHandle;

	private readonly SharedSlot[] _slots = new SharedSlot[SharedSlotCount];

	private ulong _producerFenceValue;

	private bool _disposed;

	private bool _resourcesReleased;

	public Direct3D11SharedTextureBridge(nint device, nint context, int width, int height)
	{
		if (device == IntPtr.Zero)
		{
			throw new ArgumentException("D3D11 device is missing.", "device");
		}
		if (context == IntPtr.Zero)
		{
			throw new ArgumentException("D3D11 context is missing.", "context");
		}
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException("width", "Shared texture dimensions must be positive.");
		}
		_device = device;
		_context = context;
		Marshal.AddRef(_device);
		Marshal.AddRef(_context);
		_copySubresourceRegion =
			GetComMethod<CopySubresourceRegionDelegate>(
				_context,
				CopySubresourceRegionSlot);
		_flush = GetComMethod<FlushDelegate>(_context, 111);
		try
		{
			_device5 = QueryInterface<ID3D11Device5>(_device);
			_device1 = QueryInterface<ID3D11Device1>(_device);
			_context4 = QueryInterface<ID3D11DeviceContext4>(_context);
			using (IDXGIDevice dxgiDevice =
				QueryInterface<IDXGIDevice>(_device))
			{
				dxgiDevice.GetAdapter(out IDXGIAdapter adapter);
				using (adapter)
				{
					_d3d12Device =
						D3D12CreateDevice<ID3D12Device>(
							adapter,
							FeatureLevel.Level_12_0);
				}
			}
			_producerFence = _device5.CreateFence(
				0uL,
				Vortice.Direct3D11.FenceFlags.Shared);
			_producerFenceSharedHandle =
				_producerFence.CreateSharedHandle(null!, null!);
			if (_producerFenceSharedHandle == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D11 bridge fence did not create a shared handle.");
			}
			for (int index = 0; index < _slots.Length; index++)
			{
				_slots[index] = CreateSharedTexture(width, height);
			}
		}
		catch
		{
			_disposed = true;
			ReleaseResources();
			throw;
		}
	}

	public bool TryCopyToSharedFrame(
		nint sourceTexture,
		int sourceSubresource,
		out Direct3D11SharedTextureFrameLease? frame,
		out string? failureReason)
	{
		frame = null;
		failureReason = null;
		if (_disposed)
		{
			failureReason = "D3D11 shared texture bridge is disposed.";
			return false;
		}
		if (sourceTexture == IntPtr.Zero)
		{
			failureReason = "D3D11 source texture is missing.";
			return false;
		}
		ID3D11DeviceContext4? context4 = _context4;
		ID3D11Fence? producerFence = _producerFence;
		if (context4 == null
			|| producerFence == null
			|| _producerFenceSharedHandle == IntPtr.Zero)
		{
			failureReason = "D3D11 shared texture bridge is not initialized.";
			return false;
		}
		int slotIndex = TryAcquireSlot();
		if (slotIndex < 0)
		{
			return false;
		}
		try
		{
			SharedSlot slot = _slots[slotIndex];
			_copySubresourceRegion(
				_context,
				slot.Texture,
				0u,
				0u,
				0u,
				0u,
				sourceTexture,
				checked((uint)Math.Max(0, sourceSubresource)),
				IntPtr.Zero);
			ulong fenceValue = checked(++_producerFenceValue);
			context4.Signal(producerFence, fenceValue);
			_flush(_context);
			frame = new Direct3D11SharedTextureFrameLease(
				this,
				slotIndex,
				slot.SharedHandle,
				_producerFenceSharedHandle,
				fenceValue);
			return true;
		}
		catch (Exception ex)
		{
			ReleaseSlot(slotIndex);
			failureReason = ex.Message;
			return false;
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			TryReleaseResources();
		}
	}

	internal void ReleaseSlot(int slotIndex)
	{
		if ((uint)slotIndex >= (uint)_slots.Length)
		{
			return;
		}
		Interlocked.Exchange(ref _slots[slotIndex].InUse, 0);
		if (_disposed)
		{
			TryReleaseResources();
		}
	}

	internal nint DuplicateSlotTexture(int slotIndex)
	{
		if (_disposed
			|| (uint)slotIndex >= (uint)_slots.Length
			|| Volatile.Read(ref _slots[slotIndex].InUse) == 0)
		{
			throw new ObjectDisposedException(
				nameof(Direct3D11SharedTextureFrameLease));
		}
		nint texture = _slots[slotIndex].Texture;
		if (texture == IntPtr.Zero)
		{
			throw new ObjectDisposedException(
				nameof(Direct3D11SharedTextureFrameLease));
		}
		Marshal.AddRef(texture);
		return texture;
	}

	private int TryAcquireSlot()
	{
		for (int index = 0; index < _slots.Length; index++)
		{
			if (Interlocked.CompareExchange(
				ref _slots[index].InUse,
				1,
				0) == 0)
			{
				return index;
			}
		}
		return -1;
	}

	private SharedSlot CreateSharedTexture(int width, int height)
	{
		ID3D12Device d3d12Device = _d3d12Device
			?? throw new ObjectDisposedException(
				nameof(Direct3D11SharedTextureBridge));
		ID3D11Device1 d3d11Device = _device1
			?? throw new ObjectDisposedException(
				nameof(Direct3D11SharedTextureBridge));
		ID3D12Resource? d3d12Texture = null;
		ID3D11Texture2D? d3d11Texture = null;
		nint sharedHandle = IntPtr.Zero;
		string stage = "describe D3D12 NV12 texture";
		try
		{
			ResourceDescription description = new(
				Vortice.Direct3D12.ResourceDimension.Texture2D,
				0uL,
				checked((ulong)width),
				checked((uint)height),
				1,
				1,
				Format.NV12,
				1u,
				0u,
				Vortice.Direct3D12.TextureLayout.Unknown,
				ResourceFlags.AllowRenderTarget
					| ResourceFlags.AllowSimultaneousAccess);
			stage = "create D3D12 shared NV12 texture";
			d3d12Texture =
				d3d12Device.CreateCommittedResource<ID3D12Resource>(
					new HeapProperties(HeapType.Default),
					HeapFlags.Shared,
					description,
					ResourceStates.Common);
			stage = "create D3D12 shared texture handle";
			sharedHandle = d3d12Device.CreateSharedHandle(
				d3d12Texture,
				null!,
				null!);
			if (sharedHandle == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D12 bridge texture did not create a shared handle.");
			}
			stage = "open D3D12 shared texture on D3D11";
			d3d11Texture =
				d3d11Device.OpenSharedResource1<ID3D11Texture2D>(
					sharedHandle);
			if (d3d11Texture.NativePointer == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D11 did not open the D3D12 bridge texture.");
			}
			SharedSlot slot = new()
			{
				D3D11Texture = d3d11Texture,
				D3D12Texture = d3d12Texture,
				SharedHandle = sharedHandle,
			};
			d3d11Texture = null;
			d3d12Texture = null;
			sharedHandle = IntPtr.Zero;
			return slot;
		}
		catch (Exception ex)
		{
			d3d11Texture?.Dispose();
			d3d12Texture?.Dispose();
			if (sharedHandle != IntPtr.Zero)
			{
				CloseHandle(sharedHandle);
			}
			throw new InvalidOperationException(
				$"Failed to {stage}: {ex.Message}",
				ex);
		}
	}

	private void TryReleaseResources()
	{
		lock (_disposeLock)
		{
			if (_resourcesReleased
				|| Array.Exists(
					_slots,
					slot => slot != null
						&& Volatile.Read(ref slot.InUse) != 0))
			{
				return;
			}
			ReleaseResources();
		}
	}

	private void ReleaseResources()
	{
		if (_resourcesReleased)
		{
			return;
		}
		_resourcesReleased = true;
		foreach (SharedSlot? slot in _slots)
		{
			if (slot == null)
			{
				continue;
			}
			if (slot.SharedHandle != IntPtr.Zero)
			{
				CloseHandle(slot.SharedHandle);
				slot.SharedHandle = IntPtr.Zero;
			}
			slot.D3D11Texture?.Dispose();
			slot.D3D11Texture = null;
			slot.D3D12Texture?.Dispose();
			slot.D3D12Texture = null;
		}
		if (_producerFenceSharedHandle != IntPtr.Zero)
		{
			CloseHandle(_producerFenceSharedHandle);
			_producerFenceSharedHandle = IntPtr.Zero;
		}
		_producerFence?.Dispose();
		_producerFence = null;
		_context4?.Dispose();
		_context4 = null;
		_device1?.Dispose();
		_device1 = null;
		_device5?.Dispose();
		_device5 = null;
		_d3d12Device?.Dispose();
		_d3d12Device = null;
		if (_context != IntPtr.Zero)
		{
			Marshal.Release(_context);
			_context = IntPtr.Zero;
		}
		if (_device != IntPtr.Zero)
		{
			Marshal.Release(_device);
			_device = IntPtr.Zero;
		}
	}

	private static T QueryInterface<T>(nint instance) where T : ComObject
	{
		Guid iid = typeof(T).GUID;
		MediaFoundationInterop.ThrowIfFailed(
			Marshal.QueryInterface(instance, in iid, out nint result));
		return (T)Activator.CreateInstance(typeof(T), result)!;
	}

	private static TDelegate GetComMethod<TDelegate>(nint instance, int slot) where TDelegate : Delegate
	{
		return Marshal.GetDelegateForFunctionPointer<TDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint handle);
}

public sealed record Direct3D11SharedTextureBridgeCapacitySelfTestResult(
	bool Succeeded,
	string Detail);

public static class Direct3D11SharedTextureBridgeCapacitySelfTest
{
	// Eight configured modules can each retain a working and published frame,
	// and the viewport owns three in-flight GPU frames.
	private const int MinimumCurrentPipelineCapacity = 8 * 2 + 3;

	public static Direct3D11SharedTextureBridgeCapacitySelfTestResult Run()
	{
		bool passed =
			Direct3D11SharedTextureBridge.SharedSlotCount
				>= MinimumCurrentPipelineCapacity;
		return new Direct3D11SharedTextureBridgeCapacitySelfTestResult(
			passed,
			passed
				? $"PASS: {Direct3D11SharedTextureBridge.SharedSlotCount} shared texture slots cover the current pipeline's {MinimumCurrentPipelineCapacity}-slot worst case."
				: $"FAIL: {Direct3D11SharedTextureBridge.SharedSlotCount} shared texture slots cannot cover the current pipeline's {MinimumCurrentPipelineCapacity}-slot worst case.");
	}
}
