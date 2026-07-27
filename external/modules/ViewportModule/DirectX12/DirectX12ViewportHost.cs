using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

public abstract class DirectX12ViewportHost : HwndHost
{
	private const int WsChild = 1073741824;

	private const int WsVisible = 268435456;

	private const int WsClipChildren = 33554432;

	private const int WsClipSiblings = 67108864;

	private const int SsBlackRect = 4;

	private const int SwpNoZOrder = 4;

	private const int SwpNoActivate = 16;

	private readonly string _childWindowFailureMessage;

	private readonly bool _useBlackBackground;

	private nint _viewportHandle;

	private int _viewportPixelWidth;

	private int _viewportPixelHeight;

	private DateTimeOffset? _viewportCreatedUtc;

	public bool IsViewportCreated => _viewportHandle != IntPtr.Zero;

	public int ViewportPixelWidth => _viewportPixelWidth;

	public int ViewportPixelHeight => _viewportPixelHeight;

	public DateTimeOffset? ViewportCreatedUtc => _viewportCreatedUtc;

	public string ViewportStateDescription
	{
		get
		{
			if (!IsViewportCreated)
			{
				return "webcam DX12 viewport not created";
			}
			return $"webcam DX12 viewport {_viewportPixelWidth}x{_viewportPixelHeight}";
		}
	}

	protected nint ViewportHandle => _viewportHandle;

	protected int ViewportWidth => Math.Max(1, (int)base.ActualWidth);

	protected int ViewportHeight => Math.Max(1, (int)base.ActualHeight);

	protected DirectX12ViewportHost(string childWindowFailureMessage = "Could not create DX12 viewport child window.", bool useBlackBackground = true)
	{
		_childWindowFailureMessage = childWindowFailureMessage;
		_useBlackBackground = useBlackBackground;
	}

	protected sealed override HandleRef BuildWindowCore(HandleRef hwndParent)
	{
		int viewportWidth = ViewportWidth;
		int viewportHeight = ViewportHeight;
		int num = 1442840576;
		if (_useBlackBackground)
		{
			num |= 4;
		}
		_viewportHandle = CreateWindowEx(0, "static", string.Empty, num, 0, 0, viewportWidth, viewportHeight, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		if (_viewportHandle == IntPtr.Zero)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			throw new InvalidOperationException($"{_childWindowFailureMessage} Win32 error: {lastWin32Error}.");
		}
		_viewportPixelWidth = viewportWidth;
		_viewportPixelHeight = viewportHeight;
		_viewportCreatedUtc = DateTimeOffset.UtcNow;
		try
		{
			OnViewportCreated(_viewportHandle, viewportWidth, viewportHeight);
		}
		catch (Exception ex)
		{
			OnViewportCreateFailed(ex);
		}
		return new HandleRef(this, _viewportHandle);
	}

	protected sealed override void DestroyWindowCore(HandleRef hwnd)
	{
		try
		{
			OnViewportDestroying();
		}
		finally
		{
			if (hwnd.Handle != IntPtr.Zero)
			{
				DestroyWindow(hwnd.Handle);
			}
			_viewportHandle = IntPtr.Zero;
			_viewportPixelWidth = 0;
			_viewportPixelHeight = 0;
			_viewportCreatedUtc = null;
		}
	}

	protected sealed override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		if (_viewportHandle == IntPtr.Zero)
		{
			return;
		}
		int viewportWidth = ViewportWidth;
		int viewportHeight = ViewportHeight;
		SetWindowPos(_viewportHandle, IntPtr.Zero, 0, 0, viewportWidth, viewportHeight, 20);
		_viewportPixelWidth = viewportWidth;
		_viewportPixelHeight = viewportHeight;
		try
		{
			OnViewportResized(viewportWidth, viewportHeight);
		}
		catch (Exception ex)
		{
			OnViewportResizeFailed(ex);
		}
	}

	protected abstract void OnViewportCreated(nint hwnd, int width, int height);

	protected abstract void OnViewportDestroying();

	protected abstract void OnViewportResized(int width, int height);

	protected virtual void OnViewportCreateFailed(Exception ex)
	{
		throw new InvalidOperationException("Webcam DX12 viewport initialization failed.", ex);
	}

	protected virtual void OnViewportResizeFailed(Exception ex)
	{
		throw new InvalidOperationException("Webcam DX12 viewport resize failed.", ex);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyWindow(nint hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, int flags);
}
