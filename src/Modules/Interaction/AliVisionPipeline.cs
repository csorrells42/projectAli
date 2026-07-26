using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Overlays;
using AvatarBuilder.Modules.Vision.TargetSelection;
using AvatarBuilder.Modules.Viewports;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.Common;

namespace Ali.Modules.Interaction;

/// <summary>
/// Ali's composition root for the tested vision DLLs. It selects and starts
/// black-box modules; each module retains sole ownership of its operation.
/// </summary>
public sealed class AliVisionPipeline : IDisposable
{
    private readonly CameraModule _camera;
    private readonly MediaPipeModule _mediaPipe;
    private readonly IdentityModule _identity;
    private readonly AttentionModule _attention;
    private readonly TargetSelectionModule _targetSelection;
    private readonly OverlayModule _overlay;
    private readonly ViewportModule<OverlayOutput> _viewport;
    private readonly PreviewOverlaySelection _overlaySelection;
    private bool _disposed;

    public AliVisionPipeline(
        CameraDevice camera,
        CameraVideoMode mode,
        string identityDataFolder,
        IModuleOutputSource<SpeakerRecognitionOutput>? speaker = null)
    {
        CameraModule? cameraModule = null;
        MediaPipeModule? mediaPipeModule = null;
        IdentityModule? identityModule = null;
        AttentionModule? attentionModule = null;
        TargetSelectionModule? targetSelectionModule = null;
        OverlayModule? overlayModule = null;
        ViewportModule<OverlayOutput>? viewportModule = null;
        nint nativeDevice = 0;
        try
        {
            cameraModule = new CameraModule(camera, mode);
            mediaPipeModule = new MediaPipeModule(cameraModule);
            identityModule = new IdentityModule(cameraModule, identityDataFolder);
            attentionModule = new AttentionModule(mediaPipeModule);
            targetSelectionModule = speaker is null
                ? new TargetSelectionModule(identityModule, mediaPipeModule)
                : new TargetSelectionModule(identityModule, mediaPipeModule, speaker);
            mediaPipeModule.ConnectTargetHints(targetSelectionModule.SteeringOutputSource);
            _overlaySelection = new PreviewOverlaySelection(PreviewOverlayLayers.Tracking);
            overlayModule = new OverlayModule(
                mediaPipeModule,
                _overlaySelection,
                targetSelectionModule,
                attentionModule);

            // Camera initialization creates the native device that the
            // independent viewport duplicates. Starting is idempotent.
            StartModules(cameraModule);
            nativeDevice = cameraModule.DuplicateNativeD3D12Device();
            viewportModule = new ViewportModule<OverlayOutput>(
                cameraModule,
                overlayModule,
                nativeDevice);
            nativeDevice = 0;

            _camera = cameraModule;
            _mediaPipe = mediaPipeModule;
            _identity = identityModule;
            _attention = attentionModule;
            _targetSelection = targetSelectionModule;
            _overlay = overlayModule;
            _viewport = viewportModule;

            StartModules(
                _camera,
                _mediaPipe,
                _identity,
                _attention,
                _targetSelection,
                _overlay,
                _viewport);
            _viewport.ConfigurePresentation(
                VideoFrameColorSettings.Off,
                denoiseEnabled: false,
                denoiseStrength: 2d);
        }
        catch
        {
            if (nativeDevice != 0)
            {
                System.Runtime.InteropServices.Marshal.Release(nativeDevice);
            }
            viewportModule?.Dispose();
            overlayModule?.Dispose();
            targetSelectionModule?.Dispose();
            attentionModule?.Dispose();
            identityModule?.Dispose();
            mediaPipeModule?.Dispose();
            cameraModule?.Dispose();
            throw;
        }
    }

    public System.Windows.FrameworkElement ViewportHost => _viewport.Host;
    public IModuleOutputSource<TargetSelectionOutput> TargetSelection => _targetSelection;
    public IModuleOutputSource<AttentionOutput> Attention => _attention;
    public bool HasStableAttention => _attention.LatestStableAttention;
    public string CameraStatus => _camera.Status;
    public string IdentityStatus => _identity.IdentityStatus;

    public void SetTrackingOverlay(bool enabled) =>
        _overlaySelection.Set(PreviewOverlayLayers.Tracking, enabled);

    public void SetFaceMeshOverlay(bool enabled) =>
        _overlaySelection.Set(PreviewOverlayLayers.FaceMesh, enabled);

    public void SetOptionalOverlays(bool tracking, bool faceMesh)
    {
        var layers = PreviewOverlayLayers.None;
        if (tracking)
        {
            layers |= PreviewOverlayLayers.Tracking;
        }
        if (faceMesh)
        {
            layers |= PreviewOverlayLayers.FaceMesh;
        }
        _overlaySelection.Replace(layers);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _viewport.Dispose();
        _overlay.Dispose();
        _targetSelection.Dispose();
        _attention.Dispose();
        _identity.Dispose();
        _mediaPipe.Dispose();
        _camera.Dispose();
    }

    private static void StartModules(params IVisionModule[] modules)
    {
        foreach (var module in modules)
        {
            module.Start();
        }
    }
}
