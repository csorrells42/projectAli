using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSharedMemoryFrame : IDisposable
{
	public const string PixelFormatBgra32 = "BGRA32";

	private const int BytesPerPixel = 4;

	private const int CapacityAlignmentBytes = 4194304;

	private MemoryMappedFile? _mapping;

	private MemoryMappedViewAccessor? _view;

	private string _mappingName = "";

	private long _capacityBytes;

	private bool _disposed;

	public unsafe MediaPipeSharedMemoryFrameDescriptor Write(BitmapSource bitmap)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		BitmapSource bitmapSource = ConvertToBgra32(bitmap);
		checked
		{
			int num = bitmapSource.PixelWidth * 4;
			int num2 = num * bitmapSource.PixelHeight;
			EnsureCapacity(num2);
			byte* pointer = null;
			MemoryMappedViewAccessor view = _view ?? throw new InvalidOperationException("Shared-memory view was not initialized.");
			SafeMemoryMappedViewHandle safeMemoryMappedViewHandle = view.SafeMemoryMappedViewHandle;
			safeMemoryMappedViewHandle.AcquirePointer(ref pointer);
			try
			{
				bitmapSource.CopyPixels(buffer: new IntPtr(pointer + view.PointerOffset), sourceRect: new Int32Rect(0, 0, bitmapSource.PixelWidth, bitmapSource.PixelHeight), bufferSize: num2, stride: num);
				Thread.MemoryBarrier();
			}
			finally
			{
				safeMemoryMappedViewHandle.ReleasePointer();
			}
			return new MediaPipeSharedMemoryFrameDescriptor(_mappingName, _capacityBytes, num2, bitmapSource.PixelWidth, bitmapSource.PixelHeight, num, "BGRA32");
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			ReleaseMapping();
		}
	}

	private void EnsureCapacity(int requiredBytes)
	{
		if (_mapping == null || _view == null || requiredBytes > _capacityBytes)
		{
			ReleaseMapping();
			_capacityBytes = AlignCapacity(requiredBytes);
			_mappingName = $"AvatarBuilder.MediaPipe.{Environment.ProcessId}.{Guid.NewGuid():N}";
			_mapping = MemoryMappedFile.CreateNew(_mappingName, _capacityBytes, MemoryMappedFileAccess.ReadWrite);
			_view = _mapping.CreateViewAccessor(0L, _capacityBytes, MemoryMappedFileAccess.ReadWrite);
		}
	}

	private void ReleaseMapping()
	{
		_view?.Dispose();
		_view = null;
		_mapping?.Dispose();
		_mapping = null;
		_mappingName = "";
		_capacityBytes = 0L;
	}

	private static BitmapSource ConvertToBgra32(BitmapSource bitmap)
	{
		if (bitmap.Format == PixelFormats.Bgra32)
		{
			return bitmap;
		}
		BitmapSource bitmapSource = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		if (bitmapSource.CanFreeze)
		{
			bitmapSource.Freeze();
		}
		return bitmapSource;
	}

	private static long AlignCapacity(int requiredBytes)
	{
		checked
		{
			long val = unchecked(checked(unchecked((long)requiredBytes) + 4194304L - 1) / 4194304) * 4194304;
			return Math.Max(4194304L, val);
		}
	}
}
