using System;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX11;

public sealed record D3D11Nv12PlaneStatistics(
	double Mean,
	byte Minimum,
	byte Maximum,
	long Samples);

public sealed record D3D11Nv12TextureStatistics(
	int Width,
	int Height,
	int ArraySize,
	int Format,
	int RowPitch,
	D3D11Nv12PlaneStatistics Y,
	D3D11Nv12PlaneStatistics U,
	D3D11Nv12PlaneStatistics V)
{
	public override string ToString()
	{
		return
			$"{Width}x{Height}; array {ArraySize}; format {Format}; pitch {RowPitch}; " +
			$"Y {Y.Minimum}-{Y.Maximum} mean {Y.Mean:0.0}; " +
			$"U {U.Minimum}-{U.Maximum} mean {U.Mean:0.0}; " +
			$"V {V.Minimum}-{V.Maximum} mean {V.Mean:0.0}";
	}
}

public sealed record D3D11Nv12TextureProbeResult(
	bool Succeeded,
	string Detail,
	D3D11Nv12TextureStatistics? Source,
	D3D11Nv12TextureStatistics? SharedCopy);

public static class D3D11Nv12TextureProbe
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void GetDeviceDelegate(
		nint deviceChild,
		out nint device);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void GetDescriptionDelegate(
		nint texture,
		out D3D11Texture2DDescription description);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int CreateTexture2DDelegate(
		nint device,
		ref D3D11Texture2DDescription description,
		nint initialData,
		out nint texture);

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
	private delegate int MapDelegate(
		nint context,
		nint resource,
		uint subresource,
		int mapType,
		uint mapFlags,
		out D3D11MappedSubresource mapped);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void UnmapDelegate(
		nint context,
		nint resource,
		uint subresource);

	[StructLayout(LayoutKind.Sequential)]
	private struct D3D11Texture2DDescription
	{
		public uint Width;

		public uint Height;

		public uint MipLevels;

		public uint ArraySize;

		public int Format;

		public DxgiSampleDescription SampleDescription;

		public int Usage;

		public uint BindFlags;

		public uint CPUAccessFlags;

		public uint MiscFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DxgiSampleDescription
	{
		public uint Count;

		public uint Quality;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct D3D11MappedSubresource
	{
		public nint Data;

		public uint RowPitch;

		public uint DepthPitch;
	}

	private const int GetDeviceSlot = 3;

	private const int GetDescriptionSlot = 10;

	private const int CreateTexture2DSlot = 5;

	private const int MapSlot = 14;

	private const int UnmapSlot = 15;

	private const int CopySubresourceRegionSlot = 46;

	private const int D3D11UsageStaging = 3;

	private const uint D3D11CpuAccessRead = 0x20000;

	private const int D3D11MapRead = 1;

	private const int DxgiFormatNv12 = 103;

	public static D3D11Nv12TextureProbeResult Run(
		TextureNativeFrameLease frame)
	{
		if (frame == null)
		{
			throw new ArgumentNullException(nameof(frame));
		}
		if (frame.Resource == IntPtr.Zero)
		{
			return new D3D11Nv12TextureProbeResult(
				false,
				"Frame has no D3D11 source texture.",
				null,
				null);
		}

		try
		{
			D3D11Nv12TextureStatistics source = ReadTexture(
				frame.Resource,
				checked((uint)Math.Max(0, frame.Subresource)),
				addReference: true);
			nint sharedTexture = frame.DuplicateD3D11SharedTextureForDiagnostics();
			if (sharedTexture == IntPtr.Zero)
			{
				return new D3D11Nv12TextureProbeResult(
					false,
					$"Source: {source}. Frame has no D3D11 bridge copy.",
					source,
					null);
			}
			D3D11Nv12TextureStatistics shared;
			try
			{
				shared = ReadTexture(
					sharedTexture,
					0u,
					addReference: false);
			}
			finally
			{
				Marshal.Release(sharedTexture);
			}

			bool sourceHasImage =
				source.Y.Maximum > source.Y.Minimum
				&& source.Y.Maximum > 16;
			bool sharedHasImage =
				shared.Y.Maximum > shared.Y.Minimum
				&& shared.Y.Maximum > 16;
			bool matching =
				Math.Abs(source.Y.Mean - shared.Y.Mean) <= 2.0
				&& Math.Abs(source.U.Mean - shared.U.Mean) <= 2.0
				&& Math.Abs(source.V.Mean - shared.V.Mean) <= 2.0;
			bool succeeded = sourceHasImage && sharedHasImage && matching;
			return new D3D11Nv12TextureProbeResult(
				succeeded,
				$"Source: {source}. Shared: {shared}. " +
				$"Image source {sourceHasImage}; shared {sharedHasImage}; planes match {matching}.",
				source,
				shared);
		}
		catch (Exception ex)
		{
			return new D3D11Nv12TextureProbeResult(
				false,
				ex.ToString(),
				null,
				null);
		}
	}

	private static D3D11Nv12TextureStatistics ReadTexture(
		nint sourceTexture,
		uint sourceSubresource,
		bool addReference)
	{
		nint device = IntPtr.Zero;
		nint context = IntPtr.Zero;
		nint stagingTexture = IntPtr.Zero;
		if (addReference)
		{
			Marshal.AddRef(sourceTexture);
		}
		try
		{
			GetComMethod<GetDescriptionDelegate>(
				sourceTexture,
				GetDescriptionSlot)(
				sourceTexture,
				out D3D11Texture2DDescription sourceDescription);
			if (sourceDescription.Format != DxgiFormatNv12)
			{
				throw new InvalidOperationException(
					$"Expected NV12 format {DxgiFormatNv12}, received " +
					$"{sourceDescription.Format}.");
			}
			GetComMethod<GetDeviceDelegate>(
				sourceTexture,
				GetDeviceSlot)(
				sourceTexture,
				out device);
			if (device == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D11 texture did not return its device.");
			}
			GetComMethod<GetDeviceContextDelegate>(
				device,
				GetImmediateContextSlot)(
				device,
				out context);
			if (context == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D11 device did not return its immediate context.");
			}

			D3D11Texture2DDescription stagingDescription =
				sourceDescription;
			stagingDescription.MipLevels = 1u;
			stagingDescription.ArraySize = 1u;
			stagingDescription.SampleDescription.Count = 1u;
			stagingDescription.SampleDescription.Quality = 0u;
			stagingDescription.Usage = D3D11UsageStaging;
			stagingDescription.BindFlags = 0u;
			stagingDescription.CPUAccessFlags = D3D11CpuAccessRead;
			stagingDescription.MiscFlags = 0u;
			MediaFoundationInterop.ThrowIfFailed(
				GetComMethod<CreateTexture2DDelegate>(
					device,
					CreateTexture2DSlot)(
					device,
					ref stagingDescription,
					IntPtr.Zero,
					out stagingTexture));
			if (stagingTexture == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					"D3D11 staging texture creation returned no texture.");
			}

			GetComMethod<CopySubresourceRegionDelegate>(
				context,
				CopySubresourceRegionSlot)(
				context,
				stagingTexture,
				0u,
				0u,
				0u,
				0u,
				sourceTexture,
				sourceSubresource,
				IntPtr.Zero);
			MapDelegate map =
				GetComMethod<MapDelegate>(context, MapSlot);
			MediaFoundationInterop.ThrowIfFailed(
				map(
					context,
					stagingTexture,
					0u,
					D3D11MapRead,
					0u,
					out D3D11MappedSubresource mapped));
			try
			{
				return ReadMappedNv12(
					mapped,
					sourceDescription);
			}
			finally
			{
				GetComMethod<UnmapDelegate>(
					context,
					UnmapSlot)(
						context,
						stagingTexture,
						0u);
			}
		}
		finally
		{
			if (stagingTexture != IntPtr.Zero)
			{
				Marshal.Release(stagingTexture);
			}
			if (context != IntPtr.Zero)
			{
				Marshal.Release(context);
			}
			if (device != IntPtr.Zero)
			{
				Marshal.Release(device);
			}
			if (addReference)
			{
				Marshal.Release(sourceTexture);
			}
		}
	}

	private static D3D11Nv12TextureStatistics ReadMappedNv12(
		D3D11MappedSubresource mapped,
		D3D11Texture2DDescription description)
	{
		int width = checked((int)description.Width);
		int height = checked((int)description.Height);
		int rowPitch = checked((int)mapped.RowPitch);
		if (mapped.Data == IntPtr.Zero
			|| width <= 0
			|| height <= 0
			|| rowPitch < width)
		{
			throw new InvalidOperationException(
				"Mapped NV12 texture has invalid dimensions or pitch.");
		}

		var y = new PlaneAccumulator();
		var u = new PlaneAccumulator();
		var v = new PlaneAccumulator();
		const int sampleStep = 8;
		for (int row = 0; row < height; row += sampleStep)
		{
			nint rowPointer = mapped.Data + row * rowPitch;
			for (int column = 0; column < width; column += sampleStep)
			{
				y.Add(Marshal.ReadByte(rowPointer, column));
			}
		}
		nint uvBase = mapped.Data + rowPitch * height;
		for (int row = 0; row < height / 2; row += sampleStep)
		{
			nint rowPointer = uvBase + row * rowPitch;
			for (int column = 0; column + 1 < width; column += sampleStep)
			{
				u.Add(Marshal.ReadByte(rowPointer, column));
				v.Add(Marshal.ReadByte(rowPointer, column + 1));
			}
		}
		return new D3D11Nv12TextureStatistics(
			width,
			height,
			checked((int)description.ArraySize),
			description.Format,
			rowPitch,
			y.ToStatistics(),
			u.ToStatistics(),
			v.ToStatistics());
	}

	private sealed class PlaneAccumulator
	{
		private long _sum;

		private long _samples;

		private byte _minimum = byte.MaxValue;

		private byte _maximum;

		public void Add(byte value)
		{
			_sum += value;
			_samples++;
			if (value < _minimum)
			{
				_minimum = value;
			}
			if (value > _maximum)
			{
				_maximum = value;
			}
		}

		public D3D11Nv12PlaneStatistics ToStatistics()
		{
			return _samples == 0
				? new D3D11Nv12PlaneStatistics(0.0, 0, 0, 0)
				: new D3D11Nv12PlaneStatistics(
					(double)_sum / _samples,
					_minimum,
					_maximum,
					_samples);
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void GetDeviceContextDelegate(
		nint device,
		out nint immediateContext);

	private const int GetImmediateContextSlot = 40;

	private static TDelegate GetComMethod<TDelegate>(
		nint instance,
		int slot)
		where TDelegate : Delegate
	{
		return Marshal.GetDelegateForFunctionPointer<TDelegate>(
			Marshal.ReadIntPtr(
				Marshal.ReadIntPtr(instance),
				slot * IntPtr.Size));
	}
}
