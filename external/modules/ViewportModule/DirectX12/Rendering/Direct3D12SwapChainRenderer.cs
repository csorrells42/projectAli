using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using Vortice;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

internal sealed class Direct3D12SwapChainRenderer : IDisposable
{
	private sealed class FrameResource : IDisposable
	{
		private TextureNativeFrameLease? _retainedSourceFrame;

		public ID3D12CommandAllocator CommandAllocator { get; }

		public ID3D12Resource? CameraUploadBuffer { get; private set; }

		public nint CameraUploadPointer { get; private set; }

		public ID3D12Resource? BgraColorSettingsBuffer { get; private set; }

		public nint BgraColorSettingsPointer { get; private set; }

		public ID3D12Resource? Nv12YUploadBuffer { get; private set; }

		public nint Nv12YUploadPointer { get; private set; }

		public ID3D12Resource? Nv12UvUploadBuffer { get; private set; }

		public nint Nv12UvUploadPointer { get; private set; }

		public ulong FenceValue { get; set; }

		public FrameResource(ID3D12CommandAllocator commandAllocator)
		{
			CommandAllocator = commandAllocator;
		}

		public void CreateCameraUploadBuffer(ID3D12Device device, ulong uploadBytes)
		{
			ReleaseCameraUploadBuffer();
			CameraUploadBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
			CameraUploadPointer = mappedPointer;
		}

		public void CreateBgraColorSettingsBuffer(ID3D12Device device, ulong uploadBytes)
		{
			ReleaseBgraColorSettingsBuffer();
			BgraColorSettingsBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
			BgraColorSettingsPointer = mappedPointer;
		}

		public void CreateNv12UploadBuffers(ID3D12Device device, ulong yUploadBytes, ulong uvUploadBytes)
		{
			ReleaseNv12UploadBuffers();
			Nv12YUploadBuffer = CreateMappedUploadBuffer(device, yUploadBytes, out var mappedPointer);
			Nv12YUploadPointer = mappedPointer;
			Nv12UvUploadBuffer = CreateMappedUploadBuffer(device, uvUploadBytes, out var mappedPointer2);
			Nv12UvUploadPointer = mappedPointer2;
		}

		public void ReleaseCameraUploadBuffer()
		{
			if (CameraUploadBuffer is not null)
			{
				CameraUploadBuffer.Unmap(0u);
				CameraUploadBuffer.Dispose();
				CameraUploadBuffer = null;
			}
			CameraUploadPointer = IntPtr.Zero;
		}

		public void ReleaseBgraColorSettingsBuffer()
		{
			if (BgraColorSettingsBuffer is not null)
			{
				BgraColorSettingsBuffer.Unmap(0u);
				BgraColorSettingsBuffer.Dispose();
				BgraColorSettingsBuffer = null;
			}
			BgraColorSettingsPointer = IntPtr.Zero;
		}

		public void ReleaseNv12UploadBuffers()
		{
			if (Nv12YUploadBuffer is not null)
			{
				Nv12YUploadBuffer.Unmap(0u);
				Nv12YUploadBuffer.Dispose();
				Nv12YUploadBuffer = null;
			}
			if (Nv12UvUploadBuffer is not null)
			{
				Nv12UvUploadBuffer.Unmap(0u);
				Nv12UvUploadBuffer.Dispose();
				Nv12UvUploadBuffer = null;
			}
			Nv12YUploadPointer = IntPtr.Zero;
			Nv12UvUploadPointer = IntPtr.Zero;
		}

		public void RetainSourceFrame(
			TextureNativeFrameLease sourceFrame)
		{
			ArgumentNullException.ThrowIfNull(sourceFrame);
			if (_retainedSourceFrame is not null)
			{
				throw new InvalidOperationException(
					"DX12 frame resource still owns its prior source frame.");
			}
			_retainedSourceFrame = sourceFrame.Duplicate()
				?? throw new ObjectDisposedException(
					nameof(TextureNativeFrameLease));
		}

		public void ReleaseCompletedSourceFrame()
		{
			Interlocked.Exchange(
				ref _retainedSourceFrame,
				null)?.Dispose();
		}

		public void Dispose()
		{
			ReleaseCompletedSourceFrame();
			ReleaseCameraUploadBuffer();
			ReleaseBgraColorSettingsBuffer();
			ReleaseNv12UploadBuffers();
			CommandAllocator.Dispose();
		}

		private unsafe static ID3D12Resource CreateMappedUploadBuffer(ID3D12Device device, ulong uploadBytes, out nint mappedPointer)
		{
			ID3D12Resource iD3D12Resource = device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(uploadBytes, ResourceFlags.None, 0uL), ResourceStates.GenericRead);
			void* ptr = null;
			iD3D12Resource.Map(0u, null, &ptr).CheckError();
			mappedPointer = (nint)ptr;
			return iD3D12Resource;
		}
	}

	private sealed class RetiredResourceBatch : IDisposable
	{
		public ulong FenceValue { get; }

		private readonly ID3D12Resource[] _resources;

		public RetiredResourceBatch(
			ulong fenceValue,
			ID3D12Resource[] resources)
		{
			FenceValue = fenceValue;
			_resources = resources;
		}

		public void Dispose()
		{
			foreach (ID3D12Resource resource in _resources)
			{
				resource.Dispose();
			}
		}
	}

	private const int FrameCount =
		Direct3D12PreviewDescriptorLayout.FrameCount;

	private const int D3D12DefaultShader4ComponentMappingValue = 5768;

	private const int BgraColorSettingsBufferBytes = 256;

	private static readonly TimeSpan GpuOperationTimeout =
		TimeSpan.FromSeconds(2);

	private readonly ID3D12Device _device;

	private readonly ID3D12CommandQueue _commandQueue;

	private readonly IDXGIFactory4 _factory;

	private readonly ID3D12GraphicsCommandList _commandList;

	private readonly ID3D12Fence _fence;

	private readonly AutoResetEvent _fenceEvent = new AutoResetEvent(initialState: false);

	private readonly ID3D12DescriptorHeap _rtvHeap;

	private readonly ID3D12DescriptorHeap _srvHeap;

	private readonly int _rtvDescriptorSize;

	private readonly int _srvDescriptorSize;

	private readonly ID3D12Resource?[] _renderTargets = new ID3D12Resource[3];

	private ID3D12RootSignature? _previewRootSignature;

	private ID3D12PipelineState? _previewPipelineState;

	private ID3D12RootSignature? _nv12PreviewRootSignature;

	private ID3D12PipelineState? _nv12PreviewPipelineState;

	private Direct2DTrackingOverlayRenderer? _trackingOverlayRenderer;

	private readonly FrameResource[] _frameResources = new FrameResource[3];

	private ID3D12Resource? _cameraTexture;

	private PlacedSubresourceFootPrint _cameraTextureFootprint;

	private ResourceStates _cameraTextureState;

	private ID3D12Resource? _nv12YTexture;

	private ID3D12Resource? _nv12UvTexture;

	private PlacedSubresourceFootPrint _nv12YFootprint;

	private PlacedSubresourceFootPrint _nv12UvFootprint;

	private ResourceStates _nv12YTextureState;

	private ResourceStates _nv12UvTextureState;

	private IDXGISwapChain3 _swapChain;

	private ulong _fenceValue;

	private ID3D12Fence? _d3d11ProducerFence;

	private nint _d3d11ProducerFenceHandle;

	private ulong _lastD3D11ProducerFenceValue;

	private readonly Dictionary<nint, ID3D12Resource>
		_sharedD3D11BridgeResources = [];

	private readonly Queue<RetiredResourceBatch>
		_retiredSharedD3D11BridgeResources = [];

	private long _lastSubmittedFenceValue;

	private int _cameraTextureWidth;

	private int _cameraTextureHeight;

	private int _nv12TextureWidth;

	private int _nv12TextureHeight;

	private int _viewportWidth;

	private int _viewportHeight;

	private int _presentationRefreshRequested;

	private bool _disposed;

	private bool _shaderPreviewUnavailable;

	private bool _nv12PreviewUnavailable;

	private string? _nv12PreviewFailureReason;

	private bool _nativeTexturePreviewUnavailable;

	private string? _nativeTexturePreviewFailureReason;

	private bool _sharedD3D11BridgePreviewUnavailable;

	private string? _sharedD3D11BridgePreviewFailureReason;

	private int _lastRenderAttemptWasBusy;

	private int _pendingViewportWidth;

	private int _pendingViewportHeight;

	private readonly bool _usesSharedCaptureDevice;


	public string DeviceDescription
	{
		get
		{
			if (!_usesSharedCaptureDevice)
			{
				return "Direct3D 12 / DXGI flip model";
			}
			return "Direct3D 12 / DXGI flip model on shared capture device";
		}
	}

	public string LastNv12PreviewFailureReason => _nv12PreviewFailureReason ?? "no NV12 failure detail";

	public ulong LastSubmittedFenceValue =>
		checked((ulong)Math.Max(
			0L,
			Volatile.Read(ref _lastSubmittedFenceValue)));

	public bool LastRenderAttemptWasBusy =>
		Volatile.Read(ref _lastRenderAttemptWasBusy) != 0;

	public bool IsGpuIdle => AreAllFrameResourcesAvailable();

	public Direct3D12SwapChainRenderer(nint hwnd, int width, int height, nint nativeD3D12Device = 0)
	{
		_viewportWidth = width;
		_viewportHeight = height;
		if (nativeD3D12Device != IntPtr.Zero)
		{
			_device = new ID3D12Device(nativeD3D12Device);
			_usesSharedCaptureDevice = true;
		}
		else
		{
			_device = D3D12.D3D12CreateDevice<ID3D12Device>(null, FeatureLevel.Level_12_0);
		}
		_commandQueue = _device.CreateCommandQueue<ID3D12CommandQueue>(new CommandQueueDescription(CommandListType.Direct));
		_factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
		SwapChainDescription1 desc = new SwapChainDescription1
		{
			Width = (uint)width,
			Height = (uint)height,
			Format = Format.B8G8R8A8_UNorm,
			Stereo = false,
			SampleDescription = new SampleDescription(1u, 0u),
			BufferUsage = Usage.RenderTargetOutput,
			BufferCount = 3u,
			Scaling = Scaling.Stretch,
			SwapEffect = SwapEffect.FlipDiscard,
			AlphaMode = AlphaMode.Ignore,
			Flags = SwapChainFlags.None
		};
		using IDXGISwapChain1 iDXGISwapChain = _factory.CreateSwapChainForHwnd(_commandQueue, hwnd, desc);
		_swapChain = iDXGISwapChain.QueryInterface<IDXGISwapChain3>();
		_factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
		_rtvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3u));
		_srvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, Direct3D12PreviewDescriptorLayout.DescriptorCount, DescriptorHeapFlags.ShaderVisible));
		_rtvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
		_srvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
		CreateRenderTargetViews();
		TryCreatePreviewShaderPipeline();
		TryCreateNv12PreviewShaderPipeline();
		_trackingOverlayRenderer = Direct2DTrackingOverlayRenderer.TryCreate(_device, _commandQueue, 3);
		TryAttachTrackingOverlayRenderer();
		for (int i = 0; i < 3; i++)
		{
			_frameResources[i] = new FrameResource(_device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct));
			_frameResources[i].CreateBgraColorSettingsBuffer(_device, 256uL);
			ID3D12Resource colorSettingsBuffer = _frameResources[i].BgraColorSettingsBuffer
				?? throw new InvalidOperationException("DX12 color-settings upload buffer was not created.");
			_device.CreateConstantBufferView(new ConstantBufferViewDescription
			{
				BufferLocation = colorSettingsBuffer.GPUVirtualAddress,
				SizeInBytes = 256u
			}, GetSrvCpuHandle(
				Direct3D12PreviewDescriptorLayout
					.BgraColorSettingsStart + i));
		}
		_commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(0u, CommandListType.Direct, _frameResources[0].CommandAllocator);
		_commandList.Close();
		_fence = _device.CreateFence<ID3D12Fence>(0uL);
	}

	public bool RenderProofFrame(long frameNumber)
	{
		if (_disposed
			|| !TryBeginFrame(out FrameResource frameResource, out int frameIndex))
		{
			return false;
		}
		ID3D12Resource? resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
		ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
		ID3D12GraphicsCommandList commandList = _commandList;
		ResourceBarrier reference = resourceBarrier;
		commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
		CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
		float num = (float)((double)(frameNumber % 120) / 120.0);
		_commandList.OMSetRenderTargets(rtvHandle);
		_commandList.ClearRenderTargetView(rtvHandle, new Color4(0.02f + num * 0.08f, 0.08f, 0.12f + num * 0.18f), []);
		ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
		ID3D12GraphicsCommandList commandList2 = _commandList;
		ResourceBarrier reference2 = resourceBarrier2;
		commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
		ExecuteAndPresent(frameResource);
		return true;
	}

	public bool RenderBgraFrame(byte[] bgraBytes, int width, int height, int stride, long frameNumber, VideoFrameColorSettings colorSettings = default(VideoFrameColorSettings), bool denoiseEnabled = false, double denoiseStrength = 0.0, PreviewOverlayStack? overlays = null)
	{
		if (_disposed || bgraBytes.Length < stride * height)
		{
			return false;
		}
		PreviewOverlayStack overlayStack = overlays ?? PreviewOverlayStack.Empty;
		return TryRenderBgraFrameWithShader(
			bgraBytes,
			width,
			height,
			stride,
			colorSettings,
			denoiseEnabled,
			denoiseStrength,
			overlayStack);
	}

	public bool RenderNv12Frame(byte[] nv12Bytes, int width, int height, int stride, long frameNumber, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays, bool swapChromaChannels = false)
	{
		int num = (height + 1) / 2;
		if (_disposed || stride < width || nv12Bytes.Length < stride * height + stride * num)
		{
			_nv12PreviewFailureReason = (_disposed ? "renderer disposed" : $"invalid NV12 payload: stride {stride}, width {width}, bytes {nv12Bytes.Length}, expected {stride * height + stride * num}");
			return false;
		}
		bool num2 = TryRenderNv12FrameWithShader(nv12Bytes, width, height, stride, colorSettings, denoiseEnabled, denoiseStrength, overlays, swapChromaChannels);
		if (num2)
		{
			_nv12PreviewFailureReason = null;
		}
		return num2;
	}

	public bool RenderNativeTextureFrame(TextureNativeFrameLease frame, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays, out string? failureReason)
	{
		failureReason = null;
		if (_disposed)
		{
			failureReason = "renderer disposed";
			return false;
		}
		if (!_usesSharedCaptureDevice)
		{
			failureReason = "presenter is not using the capture D3D12 device";
			return false;
		}
		if (_nativeTexturePreviewUnavailable)
		{
			failureReason = _nativeTexturePreviewFailureReason ?? "direct texture rendering disabled after an earlier failure";
			return false;
		}
		if (_nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
		{
			failureReason = "NV12 shader pipeline unavailable";
			return false;
		}
		if (frame.Resource == IntPtr.Zero)
		{
			failureReason = "frame texture resource is missing";
			return false;
		}
		if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
		{
			failureReason = "media subtype " + frame.MediaSubtype + " is not NV12";
			return false;
		}
		try
		{
			Marshal.AddRef(frame.Resource);
			using ID3D12Resource cameraResource = new ID3D12Resource(frame.Resource);
			if (!RenderNativeNv12Resource(cameraResource, frame, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength, overlays))
			{
				failureReason = "preview GPU frame resources are busy";
				return false;
			}
			_nativeTexturePreviewFailureReason = null;
			return true;
		}
		catch (Exception ex)
		{
			_nativeTexturePreviewUnavailable = true;
			_nativeTexturePreviewFailureReason = ex.Message;
			failureReason = ex.Message;
			return false;
		}
	}

	public bool RenderSharedD3D11BridgeFrame(TextureNativeFrameLease frame, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays, out string? failureReason)
	{
		failureReason = null;
		if (_disposed)
		{
			failureReason = "renderer disposed";
			return false;
		}
		if (_sharedD3D11BridgePreviewUnavailable)
		{
			failureReason = _sharedD3D11BridgePreviewFailureReason ?? "D3D11 bridge texture rendering disabled after an earlier failure";
			return false;
		}
		if (_nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
		{
			failureReason = "NV12 shader pipeline unavailable";
			return false;
		}
		if (frame.D3D12SharedTextureHandle == IntPtr.Zero)
		{
			failureReason = "D3D11 bridge shared texture handle is missing";
			return false;
		}
		if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
		{
			failureReason = "media subtype " + frame.MediaSubtype + " is not NV12";
			return false;
		}
		try
		{
			WaitForD3D11Producer(frame);
			ID3D12Resource cameraResource =
				GetSharedD3D11BridgeResource(
					frame.D3D12SharedTextureHandle);
			if (!RenderNativeNv12Resource(cameraResource, frame, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength, overlays))
			{
				failureReason = "preview GPU frame resources are busy";
				return false;
			}
			_sharedD3D11BridgePreviewFailureReason = null;
			return true;
		}
		catch (Exception ex)
		{
			_sharedD3D11BridgePreviewUnavailable = true;
			_sharedD3D11BridgePreviewFailureReason = ex.Message;
			failureReason = ex.Message;
			return false;
		}
	}

	private void WaitForD3D11Producer(TextureNativeFrameLease frame)
	{
		nint producerFenceHandle = frame.D3D11ProducerFenceHandle;
		ulong producerFenceValue = frame.D3D11ProducerFenceValue;
		if (producerFenceHandle == IntPtr.Zero || producerFenceValue == 0uL)
		{
			return;
		}
		bool generationChanged = _d3d11ProducerFence is null
			|| _d3d11ProducerFenceHandle != producerFenceHandle
			|| producerFenceValue < _lastD3D11ProducerFenceValue;
		if (generationChanged)
		{
			RetireSharedD3D11BridgeResources();
			_d3d11ProducerFence?.Dispose();
			_d3d11ProducerFence =
				_device.OpenSharedHandle<ID3D12Fence>(producerFenceHandle);
			_d3d11ProducerFenceHandle = producerFenceHandle;
		}
		_lastD3D11ProducerFenceValue = producerFenceValue;
		_commandQueue.Wait(_d3d11ProducerFence, producerFenceValue);
		ReleaseCompletedSharedD3D11BridgeResources();
	}

	private ID3D12Resource GetSharedD3D11BridgeResource(
		nint sharedTextureHandle)
	{
		if (!_sharedD3D11BridgeResources.TryGetValue(
			sharedTextureHandle,
			out ID3D12Resource? resource))
		{
			resource = _device.OpenSharedHandle<ID3D12Resource>(
				sharedTextureHandle);
			_sharedD3D11BridgeResources.Add(
				sharedTextureHandle,
				resource);
		}
		return resource;
	}

	private void RetireSharedD3D11BridgeResources()
	{
		if (_sharedD3D11BridgeResources.Count == 0)
		{
			return;
		}
		_retiredSharedD3D11BridgeResources.Enqueue(
			new RetiredResourceBatch(
				LastSubmittedFenceValue,
				[.. _sharedD3D11BridgeResources.Values]));
		_sharedD3D11BridgeResources.Clear();
	}

	private void ReleaseCompletedSharedD3D11BridgeResources()
	{
		while (_retiredSharedD3D11BridgeResources.TryPeek(
			out RetiredResourceBatch? batch)
			&& (batch.FenceValue == 0uL
				|| _fence.CompletedValue >= batch.FenceValue))
		{
			_retiredSharedD3D11BridgeResources.Dequeue();
			batch.Dispose();
		}
	}

	private void ReleaseSharedD3D11BridgeResources()
	{
		foreach (ID3D12Resource resource
			in _sharedD3D11BridgeResources.Values)
		{
			resource.Dispose();
		}
		_sharedD3D11BridgeResources.Clear();
		while (_retiredSharedD3D11BridgeResources.TryDequeue(
			out RetiredResourceBatch? batch))
		{
			batch.Dispose();
		}
	}

	private bool RenderNativeNv12Resource(ID3D12Resource cameraResource, TextureNativeFrameLease sourceFrame, int width, int height, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays)
	{
		ShaderResourceViewDescription value = new ShaderResourceViewDescription
		{
			Format = Format.R8_UNorm,
			ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
			Shader4ComponentMapping = 5768u,
			Texture2D = new Texture2DShaderResourceView
			{
				MipLevels = 1u,
				PlaneSlice = 0u
			}
		};
		ShaderResourceViewDescription value2 = new ShaderResourceViewDescription
		{
			Format = Format.R8G8_UNorm,
			ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
			Shader4ComponentMapping = 5768u,
			Texture2D = new Texture2DShaderResourceView
			{
				MipLevels = 1u,
				PlaneSlice = 1u
			}
		};
		if (!TryBeginFrame(out FrameResource frameResource, out int frameIndex))
		{
			return false;
		}
		int nativeDescriptorStart =
			Direct3D12PreviewDescriptorLayout
				.GetNativeNv12Start(frameIndex);
		_device.CreateShaderResourceView(
			cameraResource,
			value,
			GetSrvCpuHandle(nativeDescriptorStart));
		_device.CreateShaderResourceView(
			cameraResource,
			value2,
			GetSrvCpuHandle(nativeDescriptorStart + 1));
		ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
		ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(cameraResource, ResourceStates.Common, ResourceStates.PixelShaderResource);
		ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
		ID3D12GraphicsCommandList commandList = _commandList;
		InlineArray2<ResourceBarrier> buffer = default(InlineArray2<ResourceBarrier>);
		buffer[0] = resourceBarrier;
		buffer[1] = resourceBarrier2;
		commandList.ResourceBarrier(buffer);
		CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
		_commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
		_commandList.SetPipelineState(_nv12PreviewPipelineState);
		_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(in _srvHeap));
		_commandList.SetGraphicsRootDescriptorTable(
			0u,
			GetSrvGpuHandle(nativeDescriptorStart));
		SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength);
		Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
		RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
		_commandList.RSSetViewports(viewport);
		_commandList.RSSetScissorRects(rawRect);
		_commandList.OMSetRenderTargets(rtvHandle);
		_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		_commandList.DrawInstanced(3u, 1u, 0u, 0u);
		bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, overlays, _viewportWidth, _viewportHeight) ?? false;
		ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
		ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(cameraResource, ResourceStates.PixelShaderResource, ResourceStates.Common);
		_commandList.ResourceBarrier((!flag) ? new ResourceBarrier[2] { resourceBarrier3, resourceBarrier4 } : new ResourceBarrier[1] { resourceBarrier4 });
		frameResource.RetainSourceFrame(sourceFrame);
		ExecuteAndPresent(frameResource, frameIndex, overlays, flag);
		return true;
	}

	private bool TryRenderNv12FrameWithShader(byte[] nv12Bytes, int width, int height, int stride, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays, bool swapChromaChannels)
	{
		if (_nv12PreviewUnavailable || _nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
		{
			_nv12PreviewFailureReason = (_nv12PreviewUnavailable ? (_nv12PreviewFailureReason ?? "NV12 preview disabled after earlier failure") : "NV12 shader pipeline unavailable");
			return false;
		}
		try
		{
			if (!TryEnsureNv12Textures(width, height))
			{
				return false;
			}
			ID3D12Resource? nv12YTexture = _nv12YTexture;
			ID3D12Resource? nv12UvTexture = _nv12UvTexture;
			if (!TryBeginFrame(out FrameResource frameResource, out int frameIndex))
			{
				return false;
			}
			ID3D12Resource? nv12YUploadBuffer = frameResource.Nv12YUploadBuffer;
			ID3D12Resource? nv12UvUploadBuffer = frameResource.Nv12UvUploadBuffer;
			if (nv12YTexture is null || nv12UvTexture is null || nv12YUploadBuffer is null || nv12UvUploadBuffer is null)
			{
				return false;
			}
			CopyNv12FrameToUploadBuffers(frameResource, nv12Bytes, width, height, stride);
			ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
			if (_nv12YTextureState != ResourceStates.CopyDest)
			{
				ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(nv12YTexture, _nv12YTextureState, ResourceStates.CopyDest);
				ID3D12GraphicsCommandList commandList = _commandList;
				ResourceBarrier reference = resourceBarrier;
				commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
				_nv12YTextureState = ResourceStates.CopyDest;
			}
			if (_nv12UvTextureState != ResourceStates.CopyDest)
			{
				ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(nv12UvTexture, _nv12UvTextureState, ResourceStates.CopyDest);
				ID3D12GraphicsCommandList commandList2 = _commandList;
				ResourceBarrier reference2 = resourceBarrier2;
				commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
				_nv12UvTextureState = ResourceStates.CopyDest;
			}
			_commandList.CopyTextureRegion(new TextureCopyLocation(nv12YTexture), 0u, 0u, 0u, new TextureCopyLocation(nv12YUploadBuffer, _nv12YFootprint));
			_commandList.CopyTextureRegion(new TextureCopyLocation(nv12UvTexture), 0u, 0u, 0u, new TextureCopyLocation(nv12UvUploadBuffer, _nv12UvFootprint));
			ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(nv12YTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
			ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(nv12UvTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
			ID3D12GraphicsCommandList commandList3 = _commandList;
			InlineArray2<ResourceBarrier> buffer = default(InlineArray2<ResourceBarrier>);
			buffer[0] = resourceBarrier3;
			buffer[1] = resourceBarrier4;
			commandList3.ResourceBarrier(buffer);
			_nv12YTextureState = ResourceStates.PixelShaderResource;
			_nv12UvTextureState = ResourceStates.PixelShaderResource;
			ResourceBarrier resourceBarrier5 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
			ID3D12GraphicsCommandList commandList4 = _commandList;
			ResourceBarrier reference3 = resourceBarrier5;
			commandList4.ResourceBarrier(new Span<ResourceBarrier>(ref reference3));
			CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
			_commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
			_commandList.SetPipelineState(_nv12PreviewPipelineState);
			_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(in _srvHeap));
			_commandList.SetGraphicsRootDescriptorTable(0u, GetSrvGpuHandle(1));
			SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength, swapChromaChannels);
			Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
			RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
			_commandList.RSSetViewports(viewport);
			_commandList.RSSetScissorRects(rawRect);
			_commandList.OMSetRenderTargets(rtvHandle);
			_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			_commandList.DrawInstanced(3u, 1u, 0u, 0u);
			bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, overlays, _viewportWidth, _viewportHeight) ?? false;
			ResourceBarrier resourceBarrier6 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
			if (!flag)
			{
				ID3D12GraphicsCommandList commandList5 = _commandList;
				ResourceBarrier reference4 = resourceBarrier6;
				commandList5.ResourceBarrier(new Span<ResourceBarrier>(ref reference4));
			}
			ExecuteAndPresent(frameResource, frameIndex, overlays, flag);
			return true;
		}
		catch (Exception ex)
		{
			_nv12PreviewUnavailable = true;
			_nv12PreviewFailureReason = ex.Message;
			return false;
		}
	}

	private bool TryRenderBgraFrameWithShader(byte[] bgraBytes, int width, int height, int stride, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays)
	{
		if (_shaderPreviewUnavailable || _previewRootSignature is null || _previewPipelineState is null)
		{
			return false;
		}
		try
		{
			if (!TryEnsureCameraTexture(width, height))
			{
				return false;
			}
			ID3D12Resource? cameraTexture = _cameraTexture;
			if (!TryBeginFrame(out FrameResource frameResource, out int frameIndex))
			{
				return false;
			}
			ID3D12Resource? cameraUploadBuffer = frameResource.CameraUploadBuffer;
			if (cameraTexture is null || cameraUploadBuffer is null)
			{
				return false;
			}
			CopyBgraFrameToUploadBuffer(frameResource, bgraBytes, width, height, stride);
			WriteBgraColorSettings(frameResource, colorSettings, denoiseEnabled, denoiseStrength, width, height);
			ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
			if (_cameraTextureState != ResourceStates.CopyDest)
			{
				ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(cameraTexture, _cameraTextureState, ResourceStates.CopyDest);
				ID3D12GraphicsCommandList commandList = _commandList;
				ResourceBarrier reference = resourceBarrier;
				commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
				_cameraTextureState = ResourceStates.CopyDest;
			}
			_commandList.CopyTextureRegion(new TextureCopyLocation(cameraTexture), 0u, 0u, 0u, new TextureCopyLocation(cameraUploadBuffer, _cameraTextureFootprint));
			ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(cameraTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
			ID3D12GraphicsCommandList commandList2 = _commandList;
			ResourceBarrier reference2 = resourceBarrier2;
			commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
			_cameraTextureState = ResourceStates.PixelShaderResource;
			ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
			ID3D12GraphicsCommandList commandList3 = _commandList;
			ResourceBarrier reference3 = resourceBarrier3;
			commandList3.ResourceBarrier(new Span<ResourceBarrier>(ref reference3));
			CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
			_commandList.SetGraphicsRootSignature(_previewRootSignature);
			_commandList.SetPipelineState(_previewPipelineState);
			_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(in _srvHeap));
			_commandList.SetGraphicsRootDescriptorTable(0u, _srvHeap.GetGPUDescriptorHandleForHeapStart());
			_commandList.SetGraphicsRootDescriptorTable(
				1u,
				GetSrvGpuHandle(
					Direct3D12PreviewDescriptorLayout
						.BgraColorSettingsStart + frameIndex));
			Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
			RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
			_commandList.RSSetViewports(viewport);
			_commandList.RSSetScissorRects(rawRect);
			_commandList.OMSetRenderTargets(rtvHandle);
			_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			_commandList.DrawInstanced(3u, 1u, 0u, 0u);
			bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, overlays, _viewportWidth, _viewportHeight) ?? false;
			ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
			if (!flag)
			{
				ID3D12GraphicsCommandList commandList4 = _commandList;
				ResourceBarrier reference4 = resourceBarrier4;
				commandList4.ResourceBarrier(new Span<ResourceBarrier>(ref reference4));
			}
			ExecuteAndPresent(frameResource, frameIndex, overlays, flag);
			return true;
		}
		catch
		{
			_shaderPreviewUnavailable = true;
			return false;
		}
	}

	private bool TryEnsureCameraTexture(int width, int height)
	{
		if (_cameraTexture is null || !CameraUploadBuffersReady() || _cameraTextureWidth != width || _cameraTextureHeight != height)
		{
			if (!AreAllFrameResourcesAvailable())
			{
				Interlocked.Exchange(ref _lastRenderAttemptWasBusy, 1);
				return false;
			}
			_cameraTexture?.Dispose();
			ReleaseCameraUploadBuffers();
			_cameraTexture = null;
			_cameraTextureWidth = width;
			_cameraTextureHeight = height;
			ResourceDescription resourceDescription = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)width, (uint)height, 1, 1, Format.B8G8R8A8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
			_cameraTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, resourceDescription, ResourceStates.CopyDest);
			_cameraTextureState = ResourceStates.CopyDest;
			PlacedSubresourceFootPrint[] array = new PlacedSubresourceFootPrint[1];
			uint[] numRows = new uint[1];
			ulong[] rowSizeInBytes = new ulong[1];
			_device.GetCopyableFootprints(resourceDescription, 0u, 1u, 0uL, array, numRows, rowSizeInBytes, out var totalBytes);
			_cameraTextureFootprint = array[0];
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].CreateCameraUploadBuffer(_device, totalBytes);
			}
			ShaderResourceViewDescription value = new ShaderResourceViewDescription
			{
				Format = Format.B8G8R8A8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u
				}
			};
			_device.CreateShaderResourceView(_cameraTexture, value, _srvHeap.GetCPUDescriptorHandleForHeapStart());
		}
		return true;
	}

	private bool TryEnsureNv12Textures(int width, int height)
	{
		if (_nv12YTexture is null || _nv12UvTexture is null || !Nv12UploadBuffersReady() || _nv12TextureWidth != width || _nv12TextureHeight != height)
		{
			if (!AreAllFrameResourcesAvailable())
			{
				Interlocked.Exchange(ref _lastRenderAttemptWasBusy, 1);
				return false;
			}
			_nv12YTexture?.Dispose();
			_nv12UvTexture?.Dispose();
			ReleaseNv12UploadBuffers();
			_nv12YTexture = null;
			_nv12UvTexture = null;
			_nv12TextureWidth = width;
			_nv12TextureHeight = height;
			ResourceDescription description = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)width, (uint)height, 1, 1, Format.R8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
			ResourceDescription description2 = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)Math.Max(1, width / 2), (uint)Math.Max(1, height / 2), 1, 1, Format.R8G8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
			_nv12YTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, description, ResourceStates.CopyDest);
			_nv12UvTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, description2, ResourceStates.CopyDest);
			_nv12YTextureState = ResourceStates.CopyDest;
			_nv12UvTextureState = ResourceStates.CopyDest;
			_nv12YFootprint = GetTextureFootprint(description, out var uploadBytes);
			_nv12UvFootprint = GetTextureFootprint(description2, out var uploadBytes2);
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].CreateNv12UploadBuffers(_device, uploadBytes, uploadBytes2);
			}
			ShaderResourceViewDescription value = new ShaderResourceViewDescription
			{
				Format = Format.R8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u
				}
			};
			ShaderResourceViewDescription value2 = new ShaderResourceViewDescription
			{
				Format = Format.R8G8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u
				}
			};
			_device.CreateShaderResourceView(_nv12YTexture, value, GetSrvCpuHandle(1));
			_device.CreateShaderResourceView(_nv12UvTexture, value2, GetSrvCpuHandle(2));
		}
		return true;
	}

	private PlacedSubresourceFootPrint GetTextureFootprint(ResourceDescription description, out ulong uploadBytes)
	{
		PlacedSubresourceFootPrint[] array = new PlacedSubresourceFootPrint[1];
		uint[] numRows = new uint[1];
		ulong[] rowSizeInBytes = new ulong[1];
		_device.GetCopyableFootprints(description, 0u, 1u, 0uL, array, numRows, rowSizeInBytes, out uploadBytes);
		return array[0];
	}

	private bool CameraUploadBuffersReady()
	{
		FrameResource[] frameResources = _frameResources;
		foreach (FrameResource frameResource in frameResources)
		{
			if (frameResource.CameraUploadBuffer is null || frameResource.CameraUploadPointer == IntPtr.Zero)
			{
				return false;
			}
		}
		return true;
	}

	private bool Nv12UploadBuffersReady()
	{
		FrameResource[] frameResources = _frameResources;
		foreach (FrameResource frameResource in frameResources)
		{
			if (frameResource.Nv12YUploadBuffer is null || frameResource.Nv12UvUploadBuffer is null || frameResource.Nv12YUploadPointer == IntPtr.Zero || frameResource.Nv12UvUploadPointer == IntPtr.Zero)
			{
				return false;
			}
		}
		return true;
	}

	private void ReleaseCameraUploadBuffers()
	{
		FrameResource[] frameResources = _frameResources;
		for (int i = 0; i < frameResources.Length; i++)
		{
			frameResources[i].ReleaseCameraUploadBuffer();
		}
	}

	private void ReleaseNv12UploadBuffers()
	{
		FrameResource[] frameResources = _frameResources;
		for (int i = 0; i < frameResources.Length; i++)
		{
			frameResources[i].ReleaseNv12UploadBuffers();
		}
	}

	private unsafe void CopyBgraFrameToUploadBuffer(FrameResource frameResource, byte[] bgraBytes, int width, int height, int stride)
	{
		byte* cameraUploadPointer = (byte*)frameResource.CameraUploadPointer;
		if (cameraUploadPointer == null)
		{
			throw new InvalidOperationException("DX12 BGRA upload buffer is not mapped.");
		}
		fixed (byte* ptr = bgraBytes)
		{
			int num = width * 4;
			byte* ptr2 = cameraUploadPointer + (nint)_cameraTextureFootprint.Offset;
			nint num2 = (nint)_cameraTextureFootprint.Footprint.RowPitch;
			for (int i = 0; i < height; i++)
			{
				Buffer.MemoryCopy(ptr + i * stride, ptr2 + i * num2, num2, num);
			}
		}
	}

	private unsafe void WriteBgraColorSettings(FrameResource frameResource, VideoFrameColorSettings settings, bool denoiseEnabled, double denoiseStrength, int width, int height)
	{
		float* bgraColorSettingsPointer = (float*)frameResource.BgraColorSettingsPointer;
		if (bgraColorSettingsPointer == null)
		{
			throw new InvalidOperationException("DX12 BGRA color settings buffer is not mapped.");
		}
		bool hasVisibleAdjustments = settings.HasVisibleAdjustments;
		*bgraColorSettingsPointer = (hasVisibleAdjustments ? ((float)(Math.Clamp(settings.Exposure, -30.0, 30.0) * 2.2)) : 0f);
		bgraColorSettingsPointer[1] = (hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(settings.Contrast, -40.0, 40.0) / 100.0)) : 1f);
		bgraColorSettingsPointer[2] = (hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(settings.Saturation, -40.0, 40.0) / 100.0)) : 1f);
		bgraColorSettingsPointer[3] = (hasVisibleAdjustments ? ((float)(Math.Clamp(settings.Warmth, -40.0, 40.0) * 0.9)) : 0f);
		float num = (float)Math.Clamp(denoiseStrength, 0.5, 5.0);
		bgraColorSettingsPointer[4] = (denoiseEnabled ? Math.Clamp(0.06f + num * 0.08f, 0.1f, 0.42f) : 0f);
		bgraColorSettingsPointer[5] = (denoiseEnabled ? Math.Clamp(0.018f + num * 0.006f, 0.024f, 0.052f) : 0f);
		bgraColorSettingsPointer[6] = 1f / (float)Math.Max(1, width);
		bgraColorSettingsPointer[7] = 1f / (float)Math.Max(1, height);
	}

	private unsafe void CopyNv12FrameToUploadBuffers(FrameResource frameResource, byte[] nv12Bytes, int width, int height, int stride)
	{
		int num = Math.Max(1, height / 2);
		int num2 = stride * height;
		byte* nv12YUploadPointer = (byte*)frameResource.Nv12YUploadPointer;
		byte* nv12UvUploadPointer = (byte*)frameResource.Nv12UvUploadPointer;
		if (nv12YUploadPointer == null || nv12UvUploadPointer == null)
		{
			throw new InvalidOperationException("DX12 NV12 upload buffers are not mapped.");
		}
		fixed (byte* ptr = nv12Bytes)
		{
			byte* ptr2 = nv12YUploadPointer + (nint)_nv12YFootprint.Offset;
			nint num3 = (nint)_nv12YFootprint.Footprint.RowPitch;
			for (int i = 0; i < height; i++)
			{
				Buffer.MemoryCopy(ptr + i * stride, ptr2 + i * num3, num3, width);
			}
			byte* ptr3 = nv12UvUploadPointer + (nint)_nv12UvFootprint.Offset;
			nint num4 = (nint)_nv12UvFootprint.Footprint.RowPitch;
			for (int j = 0; j < num; j++)
			{
				Buffer.MemoryCopy(ptr + num2 + j * stride, ptr3 + j * num4, num4, width);
			}
		}
	}

	private void SetNv12ShaderConstants(int width, int height, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, bool swapChromaChannels = false)
	{
		bool hasVisibleAdjustments = colorSettings.HasVisibleAdjustments;
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(Math.Clamp(colorSettings.Exposure, -30.0, 30.0) * 2.2)) : 0f), 0u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(colorSettings.Contrast, -40.0, 40.0) / 100.0)) : 1f), 1u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(colorSettings.Saturation, -40.0, 40.0) / 100.0)) : 1f), 2u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(Math.Clamp(colorSettings.Warmth, -40.0, 40.0) * 0.9)) : 0f), 3u);
		float num = (float)Math.Clamp(denoiseStrength, 0.5, 5.0);
		float value = (denoiseEnabled ? Math.Clamp(0.08f + num * 0.11f, 0.14f, 0.58f) : 0f);
		float value2 = (denoiseEnabled ? Math.Clamp(0.018f + num * 0.006f, 0.024f, 0.052f) : 0f);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(value), 4u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(value2), 5u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(1f / (float)Math.Max(1, width)), 6u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(1f / (float)Math.Max(1, height)), 7u);
		_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(swapChromaChannels ? 1f : 0f), 8u);
	}

	private void TryCreatePreviewShaderPipeline()
	{
		try
		{
			byte[] array =
				EmbeddedShaderBytecode.Load("PreviewBgra.vs.cso");
			byte[] array2 =
				EmbeddedShaderBytecode.Load("PreviewBgra.ps.cso");
			DescriptorRange[] ranges = new DescriptorRange[1]
			{
				new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1u, 0u)
			};
			DescriptorRange[] ranges2 = new DescriptorRange[1]
			{
				new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1u, 0u)
			};
			RootParameter[] parameters = new RootParameter[2]
			{
				new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.Pixel),
				new RootParameter(new RootDescriptorTable(ranges2), ShaderVisibility.Pixel)
			};
			StaticSamplerDescription[] samplers = new StaticSamplerDescription[1]
			{
				new StaticSamplerDescription(0u, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0f, 0u, ComparisonFunction.Never, StaticBorderColor.TransparentBlack, 0f, float.MaxValue, ShaderVisibility.Pixel)
			};
			RootSignatureDescription description = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers);
			_previewRootSignature = _device.CreateRootSignature(in description, RootSignatureVersion.Version1);
			GraphicsPipelineStateDescription graphicsPipelineStateDescription = new GraphicsPipelineStateDescription();
			graphicsPipelineStateDescription.RootSignature = _previewRootSignature;
			graphicsPipelineStateDescription.VertexShader = array;
			graphicsPipelineStateDescription.PixelShader = array2;
			graphicsPipelineStateDescription.BlendState = BlendDescription.Opaque;
			graphicsPipelineStateDescription.RasterizerState = RasterizerDescription.CullNone;
			graphicsPipelineStateDescription.DepthStencilState = DepthStencilDescription.None;
			graphicsPipelineStateDescription.SampleMask = uint.MaxValue;
			graphicsPipelineStateDescription.PrimitiveTopologyType = PrimitiveTopologyType.Triangle;
			graphicsPipelineStateDescription.RenderTargetFormats = new Format[1] { Format.B8G8R8A8_UNorm };
			graphicsPipelineStateDescription.SampleDescription = new SampleDescription(1u, 0u);
			GraphicsPipelineStateDescription description2 = graphicsPipelineStateDescription;
			_previewPipelineState = _device.CreateGraphicsPipelineState<ID3D12PipelineState>(description2);
		}
		catch
		{
			_shaderPreviewUnavailable = true;
		}
	}


	private void TryCreateNv12PreviewShaderPipeline()
	{
		try
		{
			byte[] vertexShader =
				EmbeddedShaderBytecode.Load("PreviewNv12.vs.cso");
			byte[] pixelShader =
				EmbeddedShaderBytecode.Load("PreviewNv12.ps.cso");
			DescriptorRange[] ranges =
			[
				new DescriptorRange(
					DescriptorRangeType.ShaderResourceView,
					2u,
					0u)
			];
			RootParameter[] parameters =
			[
				new RootParameter(
					new RootDescriptorTable(ranges),
					ShaderVisibility.Pixel),
				new RootParameter(
					new RootConstants(0u, 0u, 9u),
					ShaderVisibility.Pixel)
			];
			StaticSamplerDescription[] samplers =
			[
				new StaticSamplerDescription(
					0u,
					Filter.MinMagMipLinear,
					TextureAddressMode.Clamp,
					TextureAddressMode.Clamp,
					TextureAddressMode.Clamp,
					0f,
					0u,
					ComparisonFunction.Never,
					StaticBorderColor.TransparentBlack,
					0f,
					float.MaxValue,
					ShaderVisibility.Pixel)
			];
			RootSignatureDescription rootDescription = new(
				RootSignatureFlags.AllowInputAssemblerInputLayout,
				parameters,
				samplers);
			_nv12PreviewRootSignature = _device.CreateRootSignature(
				in rootDescription,
				RootSignatureVersion.Version1);
			_nv12PreviewPipelineState =
				_device.CreateGraphicsPipelineState<ID3D12PipelineState>(
					new GraphicsPipelineStateDescription
					{
						RootSignature = _nv12PreviewRootSignature,
						VertexShader = vertexShader,
						PixelShader = pixelShader,
						BlendState = BlendDescription.Opaque,
						RasterizerState = RasterizerDescription.CullNone,
						DepthStencilState = DepthStencilDescription.None,
						SampleMask = uint.MaxValue,
						PrimitiveTopologyType =
							PrimitiveTopologyType.Triangle,
						RenderTargetFormats =
							[Format.B8G8R8A8_UNorm],
						SampleDescription =
							new SampleDescription(1u, 0u)
					});
		}
		catch (Exception ex)
		{
			_nv12PreviewUnavailable = true;
			_nv12PreviewFailureReason =
				"NV12 shader pipeline creation failed: " + ex.Message;
		}
	}

	public void Resize(int width, int height)
	{
		if (!_disposed && width > 0 && height > 0 && (width != _viewportWidth || height != _viewportHeight))
		{
			_pendingViewportWidth = width;
			_pendingViewportHeight = height;
		}
	}

	public void RequestPresentationRefresh()
	{
		if (!_disposed)
		{
			Interlocked.Exchange(ref _presentationRefreshRequested, 1);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			try
			{
				WaitForGpu();
			}
			catch (TimeoutException)
			{
				// A wedged GPU must never hold the application shutdown or
				// camera recovery path. Leave these native objects to process
				// teardown rather than releasing resources still owned by a
				// non-responsive command queue.
				_disposed = true;
				return;
			}
			_disposed = true;
			_trackingOverlayRenderer?.Dispose();
			_trackingOverlayRenderer = null;
			ReleaseRenderTargets();
			_swapChain.Dispose();
			_rtvHeap.Dispose();
			_srvHeap.Dispose();
			_cameraTexture?.Dispose();
			_nv12YTexture?.Dispose();
			_nv12UvTexture?.Dispose();
			_previewPipelineState?.Dispose();
			_previewRootSignature?.Dispose();
			_nv12PreviewPipelineState?.Dispose();
			_nv12PreviewRootSignature?.Dispose();
			ReleaseSharedD3D11BridgeResources();
			_d3d11ProducerFence?.Dispose();
			_d3d11ProducerFence = null;
			_d3d11ProducerFenceHandle = IntPtr.Zero;
			_fence.Dispose();
			_fenceEvent.Dispose();
			_commandList.Dispose();
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].Dispose();
			}
			_factory.Dispose();
			_commandQueue.Dispose();
			_device.Dispose();
		}
	}

	private void CreateRenderTargetViews()
	{
		CpuDescriptorHandle cPUDescriptorHandleForHeapStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		for (int i = 0; i < 3; i++)
		{
			_renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
			_device.CreateRenderTargetView(_renderTargets[i], null, cPUDescriptorHandleForHeapStart);
			cPUDescriptorHandleForHeapStart += _rtvDescriptorSize;
		}
	}

	private void TryAttachTrackingOverlayRenderer()
	{
		if (_trackingOverlayRenderer == null)
		{
			return;
		}
		try
		{
			_trackingOverlayRenderer.AttachBackBuffers(_renderTargets);
		}
		catch
		{
			_trackingOverlayRenderer.Dispose();
			_trackingOverlayRenderer = null;
		}
	}

	private CpuDescriptorHandle GetRtvHandle(int frameIndex)
	{
		return _rtvHeap.GetCPUDescriptorHandleForHeapStart() + frameIndex * _rtvDescriptorSize;
	}

	private CpuDescriptorHandle GetSrvCpuHandle(int descriptorIndex)
	{
		return _srvHeap.GetCPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
	}

	private GpuDescriptorHandle GetSrvGpuHandle(int descriptorIndex)
	{
		return _srvHeap.GetGPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
	}

	private void ReleaseRenderTargets()
	{
		for (int i = 0; i < _renderTargets.Length; i++)
		{
			_renderTargets[i]?.Dispose();
			_renderTargets[i] = null;
		}
	}

	private bool TryBeginFrame(out FrameResource frameResource, out int frameIndex)
	{
		Interlocked.Exchange(ref _lastRenderAttemptWasBusy, 0);
		RefreshPresentationResourcesIfRequested();
		TryApplyPendingResize();
		frameIndex = (int)_swapChain.CurrentBackBufferIndex;
		frameResource = _frameResources[frameIndex];
		if (frameResource.FenceValue != 0uL
			&& _fence.CompletedValue < frameResource.FenceValue)
		{
			Interlocked.Exchange(ref _lastRenderAttemptWasBusy, 1);
			frameResource = null!;
			return false;
		}
		frameResource.ReleaseCompletedSourceFrame();
		frameResource.FenceValue = 0uL;
		frameResource.CommandAllocator.Reset();
		_commandList.Reset(frameResource.CommandAllocator);
		return true;
	}

	private void TryApplyPendingResize()
	{
		int width = _pendingViewportWidth;
		int height = _pendingViewportHeight;
		if (width <= 0
			|| height <= 0
			|| (width == _viewportWidth && height == _viewportHeight)
			|| !AreAllFrameResourcesAvailable())
		{
			return;
		}
		_trackingOverlayRenderer?.ReleaseBackBuffers();
		ReleaseRenderTargets();
		_swapChain.ResizeBuffers(3u, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
		_viewportWidth = width;
		_viewportHeight = height;
		_pendingViewportWidth = 0;
		_pendingViewportHeight = 0;
		CreateRenderTargetViews();
		TryAttachTrackingOverlayRenderer();
	}

	private bool AreAllFrameResourcesAvailable()
	{
		if (_disposed)
		{
			return false;
		}
		ulong completedValue = _fence.CompletedValue;
		foreach (FrameResource frameResource in _frameResources)
		{
			if (frameResource.FenceValue != 0uL
				&& completedValue < frameResource.FenceValue)
			{
				return false;
			}
		}
		return true;
	}

	private void RefreshPresentationResourcesIfRequested()
	{
		if (Interlocked.Exchange(ref _presentationRefreshRequested, 0) != 0 && _trackingOverlayRenderer != null)
		{
			_trackingOverlayRenderer.ResetOverlayCache();
		}
	}

	private void ExecuteAndPresent(FrameResource frameResource)
	{
		_commandList.Close();
		_commandQueue.ExecuteCommandList(_commandList);
		_swapChain.Present(0u, PresentFlags.None);
		SignalFrameSubmitted(frameResource);
	}

	private void ExecuteAndPresent(FrameResource frameResource, int frameIndex, PreviewOverlayStack overlays, bool useDirect2DOverlay)
	{
		_commandList.Close();
		_commandQueue.ExecuteCommandList(_commandList);
		Direct2DTrackingOverlayRenderer? trackingOverlayRenderer = _trackingOverlayRenderer;
		if (useDirect2DOverlay && trackingOverlayRenderer is not null)
		{
			trackingOverlayRenderer.Draw(frameIndex, _viewportWidth, _viewportHeight, overlays);
		}
		_swapChain.Present(0u, PresentFlags.None);
		SignalFrameSubmitted(frameResource);
	}

	private void WaitForGpu()
	{
		if (!_disposed)
		{
			_fenceValue++;
			_commandQueue.Signal(_fence, _fenceValue);
			if (_fence.CompletedValue >= _fenceValue)
			{
				ClearFrameFenceValues();
				return;
			}
			_fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
			if (!_fenceEvent.WaitOne(GpuOperationTimeout))
			{
				throw new TimeoutException(
					"DX12 preview GPU did not become idle within " +
					$"{GpuOperationTimeout.TotalSeconds:0.#} seconds.");
			}
			ClearFrameFenceValues();
		}
	}

	private void SignalFrameSubmitted(FrameResource frameResource)
	{
		if (!_disposed)
		{
			_fenceValue++;
			_commandQueue.Signal(_fence, _fenceValue);
			frameResource.FenceValue = _fenceValue;
			Volatile.Write(
				ref _lastSubmittedFenceValue,
				checked((long)_fenceValue));
		}
	}

	public bool WaitForFence(ulong fenceValue, TimeSpan timeout)
	{
		if (_disposed
			|| fenceValue == 0uL
			|| _fence.CompletedValue >= fenceValue)
		{
			return true;
		}
		long started = Stopwatch.GetTimestamp();
		while (!_disposed
			&& _fence.CompletedValue < fenceValue
			&& Stopwatch.GetElapsedTime(started) < timeout)
		{
			Thread.Sleep(1);
		}
		return _disposed || _fence.CompletedValue >= fenceValue;
	}

	private void ClearFrameFenceValues()
	{
		FrameResource[] frameResources = _frameResources;
		for (int i = 0; i < frameResources.Length; i++)
		{
			frameResources[i].ReleaseCompletedSourceFrame();
			frameResources[i].FenceValue = 0uL;
		}
	}
}
