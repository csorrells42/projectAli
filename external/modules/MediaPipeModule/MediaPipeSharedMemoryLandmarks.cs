using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSharedMemoryLandmarks : IDisposable
{
	public const int MaximumLandmarkCount = 512;

	private const int ValuesPerLandmark = 3;

	private const int MaximumTransformationMatrixValueCount = 16;

	private const int LandmarkCapacityBytes = MaximumLandmarkCount * ValuesPerLandmark * sizeof(float);

	private const int CapacityBytes = LandmarkCapacityBytes + MaximumTransformationMatrixValueCount * sizeof(float);

	private readonly string _mappingName = $"AvatarBuilder.MediaPipe.Landmarks.{Environment.ProcessId}.{Guid.NewGuid():N}";

	private readonly MemoryMappedFile _mapping;

	private readonly MemoryMappedViewAccessor _view;

	private bool _disposed;

	public MediaPipeSharedMemoryLandmarks()
	{
		_mapping = MemoryMappedFile.CreateNew(_mappingName, CapacityBytes, MemoryMappedFileAccess.ReadWrite);
		_view = _mapping.CreateViewAccessor(0L, CapacityBytes, MemoryMappedFileAccess.ReadWrite);
	}

	public string Name => _mappingName;

	public int Capacity => CapacityBytes;

	public unsafe void Read(
		int landmarkCount,
		int transformationMatrixValueCount,
		out MediaPipeSidecarLandmark[] landmarks,
		out double[] transformationMatrix)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if ((uint)landmarkCount > MaximumLandmarkCount)
		{
			throw new InvalidOperationException($"MediaPipe returned an invalid landmark count: {landmarkCount}.");
		}
		if ((uint)transformationMatrixValueCount > MaximumTransformationMatrixValueCount)
		{
			throw new InvalidOperationException($"MediaPipe returned an invalid transformation matrix value count: {transformationMatrixValueCount}.");
		}

		Thread.MemoryBarrier();
		byte* pointer = null;
		SafeMemoryMappedViewHandle handle = _view.SafeMemoryMappedViewHandle;
		handle.AcquirePointer(ref pointer);
		try
		{
			ReadOnlySpan<float> values = new(pointer + _view.PointerOffset, landmarkCount * ValuesPerLandmark);
			landmarks = landmarkCount == 0
				? Array.Empty<MediaPipeSidecarLandmark>()
				: new MediaPipeSidecarLandmark[landmarkCount];
			for (int index = 0, valueIndex = 0; index < landmarkCount; index++, valueIndex += ValuesPerLandmark)
			{
				landmarks[index] = new MediaPipeSidecarLandmark(values[valueIndex], values[valueIndex + 1], values[valueIndex + 2]);
			}

			ReadOnlySpan<float> matrixValues = new(
				pointer + _view.PointerOffset + LandmarkCapacityBytes,
				transformationMatrixValueCount);
			transformationMatrix = transformationMatrixValueCount == 0
				? Array.Empty<double>()
				: new double[transformationMatrixValueCount];
			for (int index = 0; index < transformationMatrixValueCount; index++)
			{
				transformationMatrix[index] = matrixValues[index];
			}
		}
		finally
		{
			handle.ReleasePointer();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_view.Dispose();
		_mapping.Dispose();
	}
}
