namespace Ali.Infrastructure.Voice;

public sealed class SpectrumAnalyzer
{
    public const int BarCount = 256;
    private const int FftSize = 8192;
    private const int SampleRate = 44100;
    private const double MinimumDisplayFrequency = 40d;
    private const double MaximumDisplayFrequency = 20000d;
    private readonly double[] _samples = new double[FftSize];
    private int _sampleIndex;

    public SpectrumFrame AddSamples(ReadOnlySpan<float> samples)
    {
        var peak = 0d;
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            _samples[_sampleIndex] = clamped;
            _sampleIndex = (_sampleIndex + 1) % FftSize;
            peak = Math.Max(peak, Math.Abs(clamped));
        }

        var real = new double[FftSize];
        var imaginary = new double[FftSize];
        for (var index = 0; index < FftSize; index++)
        {
            var sourceIndex = (_sampleIndex + index) % FftSize;
            var window = 0.5d * (1d - Math.Cos(2d * Math.PI * index / (FftSize - 1)));
            real[index] = _samples[sourceIndex] * window;
        }

        FastFourierTransform(real, imaginary);
        return new SpectrumFrame(CreateBars(real, imaginary), peak, DateTimeOffset.UtcNow);
    }

    private static double[] CreateBars(double[] real, double[] imaginary)
    {
        var bars = new double[BarCount];
        var maxBin = real.Length / 2;

        for (var bar = 0; bar < BarCount; bar++)
        {
            var startFrequency = FrequencyForBar(bar);
            var endFrequency = FrequencyForBar(bar + 1);
            var startBin = Math.Max(1, FrequencyToBin(startFrequency, real.Length));
            var endBin = Math.Max(startBin + 1, FrequencyToBin(endFrequency, real.Length));
            endBin = Math.Min(endBin, maxBin);

            var magnitudeSum = 0d;
            var binCount = 0;
            for (var bin = startBin; bin < endBin; bin++)
            {
                magnitudeSum += Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
                binCount++;
            }

            var averageMagnitude = binCount == 0 ? 0d : magnitudeSum / binCount;
            var db = 20d * Math.Log10(averageMagnitude / real.Length + 0.000000001d);
            bars[bar] = Math.Clamp((db + 95d) / 70d, 0d, 1d);
        }

        return bars;
    }

    private static double FrequencyForBar(int bar)
    {
        var position = bar / (double)BarCount;
        return MinimumDisplayFrequency * Math.Pow(MaximumDisplayFrequency / MinimumDisplayFrequency, position);
    }

    private static int FrequencyToBin(double frequency, int fftSize) =>
        (int)Math.Round(frequency * fftSize / SampleRate);

    private static void FastFourierTransform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        var bits = (int)Math.Log2(n);

        for (var index = 0; index < n; index++)
        {
            var reversed = ReverseBits(index, bits);
            if (reversed <= index)
            {
                continue;
            }

            (real[index], real[reversed]) = (real[reversed], real[index]);
            (imaginary[index], imaginary[reversed]) = (imaginary[reversed], imaginary[index]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2d * Math.PI / length;
            var wLengthReal = Math.Cos(angle);
            var wLengthImaginary = Math.Sin(angle);

            for (var index = 0; index < n; index += length)
            {
                var wReal = 1d;
                var wImaginary = 0d;

                for (var offset = 0; offset < length / 2; offset++)
                {
                    var evenIndex = index + offset;
                    var oddIndex = evenIndex + length / 2;
                    var oddReal = real[oddIndex] * wReal - imaginary[oddIndex] * wImaginary;
                    var oddImaginary = real[oddIndex] * wImaginary + imaginary[oddIndex] * wReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextWReal = wReal * wLengthReal - wImaginary * wLengthImaginary;
                    wImaginary = wReal * wLengthImaginary + wImaginary * wLengthReal;
                    wReal = nextWReal;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        var reversed = 0;
        for (var index = 0; index < bits; index++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}
