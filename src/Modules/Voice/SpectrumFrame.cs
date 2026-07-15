namespace Ali.Modules.Voice;

public sealed record SpectrumFrame(double[] Magnitudes, double PeakLevel, DateTimeOffset CapturedAt);
