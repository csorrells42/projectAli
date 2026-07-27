using System.Collections.Generic;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

internal static class Direct3D12PreviewDescriptorLayout
{
	internal const int FrameCount = 3;

	internal const int BgraTexture = 0;

	internal const int UploadedNv12Start = 1;

	internal const int BgraColorSettingsStart = 3;

	internal const int NativeNv12Start =
		BgraColorSettingsStart + FrameCount;

	internal const int DescriptorCount =
		NativeNv12Start + FrameCount * 2;

	internal static int GetNativeNv12Start(int frameIndex)
	{
		return NativeNv12Start + frameIndex * 2;
	}
}

public sealed record Direct3D12PreviewDescriptorLayoutSelfTestResult(
	bool Succeeded,
	string Detail);

public static class Direct3D12PreviewDescriptorLayoutSelfTest
{
	public static Direct3D12PreviewDescriptorLayoutSelfTestResult Run()
	{
		var occupied = new HashSet<int>
		{
			Direct3D12PreviewDescriptorLayout.BgraTexture,
			Direct3D12PreviewDescriptorLayout.UploadedNv12Start,
			Direct3D12PreviewDescriptorLayout.UploadedNv12Start + 1
		};
		bool unique = occupied.Count == 3;
		for (int frameIndex = 0;
			frameIndex < Direct3D12PreviewDescriptorLayout.FrameCount;
			frameIndex++)
		{
			unique &= occupied.Add(
				Direct3D12PreviewDescriptorLayout
					.BgraColorSettingsStart + frameIndex);
		}
		for (int frameIndex = 0;
			frameIndex < Direct3D12PreviewDescriptorLayout.FrameCount;
			frameIndex++)
		{
			int start =
				Direct3D12PreviewDescriptorLayout
					.GetNativeNv12Start(frameIndex);
			unique &= occupied.Add(start);
			unique &= occupied.Add(start + 1);
		}
		bool passed =
			unique
			&& occupied.Count
				== Direct3D12PreviewDescriptorLayout.DescriptorCount
			&& Direct3D12PreviewDescriptorLayout.DescriptorCount == 12;
		return new Direct3D12PreviewDescriptorLayoutSelfTestResult(
			passed,
			passed
				? "PASS: all three in-flight frames own non-overlapping native NV12 descriptor pairs."
				: "FAIL: an in-flight native NV12 descriptor pair overlaps another GPU descriptor.");
	}
}
