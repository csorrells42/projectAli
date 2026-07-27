using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public static class TextureNativePreviewPolicy
{
	private sealed record PreviewFailure(DateTime RecordedAtUtc, string Reason);

	private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(20L);

	private static readonly Dictionary<string, PreviewFailure> PreviewFailures = new Dictionary<string, PreviewFailure>(StringComparer.OrdinalIgnoreCase);

	public static bool ShouldPreferNv12UploadFallback(string? mediaSubtype, int width, int height, byte[]? nv12PreviewBytes, int nv12PreviewStride)
	{
		if (string.IsNullOrWhiteSpace(mediaSubtype) || !mediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase) || width <= 0 || height <= 0 || nv12PreviewBytes == null || nv12PreviewBytes.LongLength <= 0 || nv12PreviewStride < width)
		{
			return false;
		}
		int num = (height + 1) / 2;
		long num2 = (long)nv12PreviewStride * (long)height + (long)nv12PreviewStride * (long)num;
		if (num2 > 0)
		{
			return nv12PreviewBytes.LongLength >= num2;
		}
		return false;
	}

	public static bool TryGetPreviewFailure(CameraDevice camera, CameraVideoMode mode, out string reason)
	{
		string key = CreatePreviewFailureKey(camera, mode);
		if (!PreviewFailures.TryGetValue(key, out PreviewFailure? value))
		{
			reason = string.Empty;
			return false;
		}
		if (DateTime.UtcNow - value.RecordedAtUtc > FailureCooldown)
		{
			PreviewFailures.Remove(key);
			reason = string.Empty;
			return false;
		}
		reason = value.Reason;
		return true;
	}

	public static void RememberPreviewFailure(CameraDevice camera, CameraVideoMode mode, string reason)
	{
		PreviewFailures[CreatePreviewFailureKey(camera, mode)] = new PreviewFailure(DateTime.UtcNow, string.IsNullOrWhiteSpace(reason) ? "previous native DX12 texture preview attempt failed" : reason);
	}

	public static void ForgetPreviewFailure(CameraDevice camera, CameraVideoMode mode)
	{
		PreviewFailures.Remove(CreatePreviewFailureKey(camera, mode));
	}

	private static string CreatePreviewFailureKey(CameraDevice camera, CameraVideoMode mode)
	{
		string obj = (string.IsNullOrWhiteSpace(camera.DevicePath) ? (camera.Source + "|" + camera.Name) : (camera.Source + "|" + camera.DevicePath));
		string text = (mode.IsAuto ? "auto" : $"{mode.Width}x{mode.Height}@{mode.FramesPerSecond:0.###}");
		return obj + "|" + text;
	}
}
