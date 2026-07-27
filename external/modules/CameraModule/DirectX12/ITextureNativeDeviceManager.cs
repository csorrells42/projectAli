using System;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal interface ITextureNativeDeviceManager : IDisposable
{
	IMFDXGIDeviceManager Manager { get; }

	string ModeName { get; }

	Guid TextureResourceId { get; }

	nint DuplicateNativeD3D12Device();
}
