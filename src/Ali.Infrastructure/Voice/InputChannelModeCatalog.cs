namespace Ali.Infrastructure.Voice;

public static class InputChannelModeCatalog
{
    public const int MaximumSelectableInputs = 8;
    public const string MonoSumLabel = "Auto mix";

    public static IReadOnlyList<string> CreateLabels(int channelCount)
    {
        var labels = new List<string> { MonoSumLabel };
        var selectableCount = Math.Clamp(channelCount, 1, MaximumSelectableInputs);
        for (var channel = 1; channel <= selectableCount; channel++)
        {
            labels.Add($"Input {channel}");
        }

        return labels;
    }

    public static string ToLabel(InputChannelMode mode) =>
        mode switch
        {
            InputChannelMode.Input1Left => "Input 1",
            InputChannelMode.Input2Right => "Input 2",
            InputChannelMode.Input3 => "Input 3",
            InputChannelMode.Input4 => "Input 4",
            InputChannelMode.Input5 => "Input 5",
            InputChannelMode.Input6 => "Input 6",
            InputChannelMode.Input7 => "Input 7",
            InputChannelMode.Input8 => "Input 8",
            _ => MonoSumLabel
        };

    public static InputChannelMode FromLabel(string? label) =>
        Normalize(label) switch
        {
            "input 1" => InputChannelMode.Input1Left,
            "input 2" => InputChannelMode.Input2Right,
            "input 3" => InputChannelMode.Input3,
            "input 4" => InputChannelMode.Input4,
            "input 5" => InputChannelMode.Input5,
            "input 6" => InputChannelMode.Input6,
            "input 7" => InputChannelMode.Input7,
            "input 8" => InputChannelMode.Input8,
            _ => MonoSum
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

    private static InputChannelMode MonoSum => InputChannelMode.MonoSum;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
