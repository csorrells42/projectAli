namespace Ali.App.Wpf.ViewModels;

public sealed class ResourceMeterViewModel(string label) : ObservableObject
{
    private double _percent;
    private string _displayText = "--";
    private string _toolTip = $"{label}: unavailable";

    public string Label { get; } = label;

    public double Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, Math.Clamp(value, 0d, 100d));
    }

    public string DisplayText
    {
        get => _displayText;
        private set => SetProperty(ref _displayText, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    public void Update(double? percent, string unavailableReason)
    {
        if (percent is null)
        {
            Percent = 0;
            DisplayText = "--";
            ToolTip = $"{Label}: {unavailableReason}";
            return;
        }

        var clamped = Math.Clamp(percent.Value, 0d, 100d);
        Percent = clamped;
        DisplayText = $"{clamped:0}%";
        ToolTip = $"{Label}: {clamped:0.0}%";
    }
}
