using AvatarBuilder.Modules.Audio.ParakeetSpeechToText;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.WakeWord;
using AvatarBuilder.Modules.Confidence;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Security;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.IdentityEnrollment;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Overlays;
using AvatarBuilder.Modules.Vision.TargetSelection;

namespace Ali.Framework.Tests;

public sealed class ModuleSelfTests
{
    [Fact]
    public void FrameTiming()
    {
        var result = FrameModuleTimingSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void FramePublicationSignal()
    {
        var result = FramePublicationSignalSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void LatestFramePublisher()
    {
        var result = LatestFramePublisherSelfTest.Run();
        Assert.True(result.Passed, result.Status);
    }

    [Fact]
    public void ModuleOutputBroadcaster()
    {
        var result = ModuleOutputBroadcasterSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void Attention() => AssertPass(AttentionModuleSelfTest.Run());

    [Fact]
    public void Security() => AssertPass(AliSecurityModuleSelfTest.Run());

    [Fact]
    public void InteractionConfidence() => AssertPass(InteractionConfidenceModuleSelfTest.Run());

    [Fact]
    public void SpeakerEnrollmentWorkflow() => AssertPass(SpeakerEnrollmentWorkflowSelfTest.Run());

    [Fact]
    public void IdentityEnrollmentGuidance()
    {
        var result = IdentityEnrollmentGuidanceSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void PersonIdentityMemory()
    {
        var result = PersonIdentityMemorySelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void TargetSelectionRetention()
    {
        var result = TargetSelectionRetentionSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void PreviewOverlayStack()
    {
        var result = PreviewOverlayStackSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void MediaPipeBrowGeometry()
    {
        var result = MediaPipeBrowGeometrySelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    [Trait("Category", "RuntimeAsset")]
    public void WakeWordRuntime()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "dependencies", "audio", "keyword-spotting",
            WakeWordModelInfo.ModelFolderName);
        AssertPass(WakeWordModuleSelfTest.Run(model));
    }

    [Fact]
    [Trait("Category", "RuntimeAsset")]
    public void ParakeetRuntime()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "dependencies", "audio", "parakeet",
            ParakeetModelInfo.ModelName);
        var result = ParakeetSpeechToTextModuleSelfTest.Run(model);
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    [Trait("Category", "OptionalRuntimeAsset")]
    public void DenseFaceGeometry_WhenLegacyModelIsPresent()
    {
        if (!DenseFaceLandmarkModelInfo.Load().ModelExists)
        {
            return;
        }

        var result = MediaPipeFaceGeometryEstimatorSelfTest.Run();
        Assert.True(result.Succeeded, result.Detail);
    }

    private static void AssertPass(string detail) =>
        Assert.True(detail.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase), detail);
}
