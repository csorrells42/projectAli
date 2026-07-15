namespace Ali.Modules.Voice;

public sealed class VoiceProcessingTelemetry
{
    public double CompressorGainReductionDb { get; set; }

    public double CompressorInputLevelDb { get; set; } = -100;

    public double CompressorThresholdDb { get; set; } = -18;

    public bool CompressorActive { get; set; }

    public bool GateOpen { get; set; } = true;

    public double GateOpenness { get; set; } = 1;
}
