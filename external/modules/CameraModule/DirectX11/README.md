# Webcam.DirectX11 Module

Owns the D3D11 device/context and Media Foundation DXGI device-manager setup used by texture-native capture.

Current entry points:
- `Direct3D11DeviceManager.cs`
- `Direct3D11SharedTextureBridge.cs`

Keep this module narrow. Camera selection, frame ownership, and DX12 presentation belong in their respective webcam modules.

`Direct3D11SharedTextureBridge` is the last-resort path when capture cannot provide a validated NV12 staging payload. It owns one reusable NV12 shared texture, so the DX12 presenter must finish its GPU read before capture copies another frame into that texture.
