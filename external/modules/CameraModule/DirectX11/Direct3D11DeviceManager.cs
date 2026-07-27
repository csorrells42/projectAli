using System;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX11;

internal sealed class Direct3D11DeviceManager : ITextureNativeDeviceManager, IDisposable
{
	public static readonly Guid ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

	private const int D3D_DRIVER_TYPE_HARDWARE = 1;

	private const int D3D11_CREATE_DEVICE_BGRA_SUPPORT = 32;

	private const int D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 2048;

	private const int D3D11_SDK_VERSION = 7;

	private const int D3D_FEATURE_LEVEL_11_1 = 45312;

	private const int D3D_FEATURE_LEVEL_11_0 = 45056;

	private nint _device;

	private nint _context;

	private IMFDXGIDeviceManager? _manager;

	public IMFDXGIDeviceManager Manager => _manager ?? throw new ObjectDisposedException("Direct3D11DeviceManager");

	public int Mode { get; }

	public string ModeName => Mode switch
	{
		2 => "D3D12", 
		1 => "D3D11", 
		_ => $"mode {Mode}", 
	};

	public Guid TextureResourceId => ID3D11Texture2D;

	private Direct3D11DeviceManager(nint device, nint context, IMFDXGIDeviceManager manager, int mode)
	{
		_device = device;
		_context = context;
		_manager = manager;
		Mode = mode;
	}

	public nint DuplicateNativeD3D12Device()
	{
		return IntPtr.Zero;
	}

	public Direct3D11SharedTextureBridge CreateSharedTextureBridge(int width, int height)
	{
		if (_device == IntPtr.Zero || _context == IntPtr.Zero)
		{
			throw new ObjectDisposedException("Direct3D11DeviceManager");
		}
		return new Direct3D11SharedTextureBridge(_device, _context, width, height);
	}

	public static Direct3D11DeviceManager Create()
	{
		int[] array = new int[2] { 45312, 45056 };
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 2080, array, array.Length, 7, out var device, out var _, out var immediateContext));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateDXGIDeviceManager(out int resetToken, out IMFDXGIDeviceManager deviceManager));
			MediaFoundationInterop.ThrowIfFailed(deviceManager.ResetDevice(device, resetToken));
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFGetDXGIDeviceManageMode(deviceManager, out var mode));
			return new Direct3D11DeviceManager(device, immediateContext, deviceManager, mode);
		}
		catch
		{
			if (immediateContext != IntPtr.Zero)
			{
				Marshal.Release(immediateContext);
			}
			if (device != IntPtr.Zero)
			{
				Marshal.Release(device);
			}
			throw;
		}
	}

	public void Dispose()
	{
		MediaFoundationInterop.ReleaseComObject(_manager);
		_manager = null;
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
}
