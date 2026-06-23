namespace Ali.Infrastructure.Voice;

public static class InputChannelModeCatalog
{
    public const int MaximumSelectableInputs = 8;
    public const string HighestEnergyLabel = "Auto strongest";
    public const string MonoSumLabel = "Sum L+R";

    public static IReadOnlyList<string> CreateLabels(int channelCount)
    {
        var labels = new List<string> { MonoSumLabel };
        var selectableCount = Math.Clamp(channelCount, 1, MaximumSelectableInputs);
        for (var channel = 1; channel <= selectableCount; channel++)
        {
            labels.Add(InputLabel(channel));
        }

        return labels;
    }

    public static string ToLabel(InputChannelMode mode) =>
        mode switch
        {
            InputChannelMode.HighestEnergy => HighestEnergyLabel,
            InputChannelMode.MonoSum => MonoSumLabel,
            InputChannelMode.Input1Left => "Input 1 L",
            InputChannelMode.Input2Right => "Input 2 R",
            InputChannelMode.Input3 => "Input 3",
            InputChannelMode.Input4 => "Input 4",
            InputChannelMode.Input5 => "Input 5",
            InputChannelMode.Input6 => "Input 6",
            InputChannelMode.Input7 => "Input 7",
            InputChannelMode.Input8 => "Input 8",
            _ => HighestEnergyLabel
        };

    public static InputChannelMode FromLabel(string? label) =>
        Normalize(label) switch
        {
            "auto strongest" => InputChannelMode.HighestEnergy,
            "auto" => InputChannelMode.HighestEnergy,
            "highest energy" => InputChannelMode.HighestEnergy,
            "mono fold-down" => InputChannelMode.MonoSum,
            "mono folddown" => InputChannelMode.MonoSum,
            "mono sum" => InputChannelMode.MonoSum,
            "sum l+r" => InputChannelMode.MonoSum,
            "sum lr" => InputChannelMode.MonoSum,
            "input 1" => InputChannelMode.Input1Left,
            "input 1 l" => InputChannelMode.Input1Left,
            "input 1 left" => InputChannelMode.Input1Left,
            "input 2" => InputChannelMode.Input2Right,
            "input 2 r" => InputChannelMode.Input2Right,
            "input 2 right" => InputChannelMode.Input2Right,
            "input 3" => InputChannelMode.Input3,
            "input 4" => InputChannelMode.Input4,
            "input 5" => InputChannelMode.Input5,
            "input 6" => InputChannelMode.Input6,
            "input 7" => InputChannelMode.Input7,
            "input 8" => InputChannelMode.Input8,
            _ => InputChannelMode.HighestEnergy
        };

    public static InputChannelMode FromStorageValue(string? value)
    {
        if (Enum.TryParse<InputChannelMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return FromLabel(value);
    }

    public static int RequiredChannelCount(InputChannelMode mode) =>
        ChannelIndex(mode) is { } index ? index + 1 : 2;

    public static int? ChannelIndex(InputChannelMode mode) =>
        mode switch
        {
            InputChannelMode.Input1Left => 0,
            InputChannelMode.Input2Right => 1,
            InputChannelMode.Input3 => 2,
            InputChannelMode.Input4 => 3,
            InputChannelMode.Input5 => 4,
            InputChannelMode.Input6 => 5,
            InputChannelMode.Input7 => 6,
            InputChannelMode.Input8 => 7,
            _ => null
        };

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static string InputLabel(int channel) =>
        channel switch
        {
            1 => "Input 1 L",
            2 => "Input 2 R",
            _ => $"Input {channel}"
        };
}
