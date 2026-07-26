using System.Windows;
using System.Windows.Controls;
using WpfSize = System.Windows.Size;

namespace Ali.UI.Controls;

public sealed class AspectRatioDecorator : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(AspectRatioDecorator),
            new FrameworkPropertyMetadata(
                16d / 9d,
                FrameworkPropertyMetadataOptions.AffectsMeasure
                | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        if (Child is null)
        {
            return default;
        }
        var available = Fit(constraint);
        Child.Measure(available);
        return available;
    }

    protected override WpfSize ArrangeOverride(WpfSize arrangeSize)
    {
        if (Child is null)
        {
            return arrangeSize;
        }
        var fitted = Fit(arrangeSize);
        Child.Arrange(new Rect(
            (arrangeSize.Width - fitted.Width) / 2d,
            (arrangeSize.Height - fitted.Height) / 2d,
            fitted.Width,
            fitted.Height));
        return arrangeSize;
    }

    private WpfSize Fit(WpfSize available)
    {
        var ratio = double.IsFinite(AspectRatio) && AspectRatio > 0d
            ? AspectRatio
            : 16d / 9d;
        var width = double.IsFinite(available.Width) ? Math.Max(0d, available.Width) : 1600d;
        var height = double.IsFinite(available.Height) ? Math.Max(0d, available.Height) : width / ratio;
        if (height <= 0d || width / height > ratio)
        {
            width = height * ratio;
        }
        else
        {
            height = width / ratio;
        }
        return new WpfSize(width, height);
    }
}
