using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace Ali.UI.ViewModels;

public sealed class StackComponentStatusViewModel : ObservableObject
{
    private string _state = "Checking";
    private string _toolTip = "Status has not been checked yet.";
    private MediaBrush _brush = MediaBrushes.Gold;

    public StackComponentStatusViewModel(string label)
    {
        Label = label;
    }

    public string Label { get; }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    public MediaBrush Brush
    {
        get => _brush;
        private set => SetProperty(ref _brush, value);
    }

    public void Update(string state, string toolTip, MediaBrush brush)
    {
        State = state;
        ToolTip = toolTip;
        Brush = brush;
    }
}
