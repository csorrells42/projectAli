using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ali.Modules.Conversation;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace Ali.UI.Controls;

public sealed partial class MarkdownMessageView : ContentControl
{
    private static readonly MediaBrush TextBrush = BrushFrom(0xF8, 0xFA, 0xFC);
    private static readonly MediaBrush MutedTextBrush = BrushFrom(0xD0, 0xD5, 0xDD);
    private static readonly MediaBrush AccentBrush = BrushFrom(0x8D, 0xDD, 0xF0);
    private static readonly MediaBrush CodeBrush = BrushFrom(0xE9, 0xD5, 0xFF);
    private static readonly MediaBrush CodeBackgroundBrush = BrushFrom(0x25, 0x25, 0x2A);
    private static readonly MediaBrush TableHeaderBrush = BrushFrom(0x1D, 0x1D, 0x20);
    private static readonly MediaBrush TableRowBrush = BrushFrom(0x10, 0x10, 0x12);
    private static readonly MediaBrush TableAlternateRowBrush = BrushFrom(0x16, 0x16, 0x19);
    private static readonly MediaBrush TableBorderBrush = BrushFrom(0x3A, 0x3A, 0x40);

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    public static readonly DependencyProperty MessageTextAlignmentProperty = DependencyProperty.Register(
        nameof(MessageTextAlignment),
        typeof(TextAlignment),
        typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsRender, OnMarkdownChanged));

    public MarkdownMessageView()
    {
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        Background = MediaBrushes.Transparent;
        Rebuild();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public TextAlignment MessageTextAlignment
    {
        get => (TextAlignment)GetValue(MessageTextAlignmentProperty);
        set => SetValue(MessageTextAlignmentProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _) =>
        ((MarkdownMessageView)dependencyObject).Rebuild();

    private void Rebuild()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        foreach (var block in MarkdownMessageParser.Parse(Markdown))
        {
            panel.Children.Add(block switch
            {
                MarkdownHeadingBlock heading => BuildHeading(heading),
                MarkdownListItemBlock item => BuildListItem(item),
                MarkdownCodeBlock code => BuildCodeBlock(code),
                MarkdownTableBlock table => BuildTable(table),
                MarkdownParagraphBlock paragraph => BuildParagraph(paragraph),
                _ => new Border()
            });
        }

        Content = panel;
    }

    private TextBlock BuildHeading(MarkdownHeadingBlock heading)
    {
        var text = CreateTextBlock(
            heading.Level switch
            {
                1 => 21,
                2 => 18,
                _ => 16
            },
            FontWeights.SemiBold,
            TextBrush);
        text.Margin = new Thickness(0, 6, 0, 5);
        AddInlineContent(text.Inlines, heading.Text);
        return text;
    }

    private TextBlock BuildParagraph(MarkdownParagraphBlock paragraph)
    {
        var text = CreateTextBlock(15, FontWeights.Normal, TextBrush);
        text.Margin = new Thickness(0, 0, 0, 8);
        AddInlineContent(text.Inlines, paragraph.Text);
        return text;
    }

    private FrameworkElement BuildListItem(MarkdownListItemBlock item)
    {
        var grid = new Grid { Margin = new Thickness(8, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = CreateTextBlock(15, FontWeights.SemiBold, AccentBrush);
        marker.Text = item.Marker;
        marker.Margin = new Thickness(0, 0, 8, 0);
        var text = CreateTextBlock(15, FontWeights.Normal, TextBrush);
        AddInlineContent(text.Inlines, item.Text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(marker);
        grid.Children.Add(text);
        return grid;
    }

    private FrameworkElement BuildCodeBlock(MarkdownCodeBlock code)
    {
        var text = CreateTextBlock(13, FontWeights.Normal, CodeBrush);
        text.FontFamily = new MediaFontFamily("Cascadia Mono, Consolas");
        text.Text = code.Text;
        return new Border
        {
            Background = CodeBackgroundBrush,
            BorderBrush = TableBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 9),
            Child = text
        };
    }

    private FrameworkElement BuildTable(MarkdownTableBlock table)
    {
        var grid = new Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 10)
        };
        for (var column = 0; column < table.Headers.Count; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = table.Headers.Count == 2 && column == 0
                    ? GridLength.Auto
                    : new GridLength(1, GridUnitType.Star),
                MaxWidth = table.Headers.Count == 2 && column == 0 ? 260 : double.PositiveInfinity,
                MinWidth = 80
            });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < table.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddTableRow(grid, table.Headers, 0, TableHeaderBrush, FontWeights.SemiBold);
        for (var row = 0; row < table.Rows.Count; row++)
        {
            AddTableRow(
                grid,
                table.Rows[row],
                row + 1,
                row % 2 == 0 ? TableRowBrush : TableAlternateRowBrush,
                FontWeights.Normal);
        }

        return grid;
    }

    private void AddTableRow(
        Grid grid,
        IReadOnlyList<string> cells,
        int row,
        MediaBrush background,
        FontWeight fontWeight)
    {
        for (var column = 0; column < grid.ColumnDefinitions.Count; column++)
        {
            var text = CreateTextBlock(13, fontWeight, row == 0 ? TextBrush : MutedTextBrush);
            AddInlineContent(text.Inlines, column < cells.Count ? cells[column] : string.Empty);
            var border = new Border
            {
                Background = background,
                BorderBrush = TableBorderBrush,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(10, 7, 10, 7),
                Child = text
            };
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            grid.Children.Add(border);
        }
    }

    private TextBlock CreateTextBlock(double fontSize, FontWeight weight, MediaBrush foreground) =>
        new()
        {
            FontFamily = FontFamily,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = foreground,
            LineHeight = Math.Max(fontSize + 7, 20),
            TextAlignment = MessageTextAlignment,
            TextWrapping = TextWrapping.Wrap
        };

    private static void AddInlineContent(InlineCollection inlines, string text)
    {
        var normalized = BreakTagRegex().Replace(text, "\n");
        for (var index = 0; index < normalized.Length;)
        {
            if (normalized[index] == '\n')
            {
                inlines.Add(new LineBreak());
                index++;
                continue;
            }

            if (TryReadWrapped(normalized, index, "**", out var bold, out var next)
                || TryReadWrapped(normalized, index, "__", out bold, out next))
            {
                inlines.Add(new Bold(new Run(bold)));
                index = next;
                continue;
            }

            if (TryReadWrapped(normalized, index, "`", out var code, out next))
            {
                inlines.Add(new Run(code)
                {
                    FontFamily = new MediaFontFamily("Cascadia Mono, Consolas"),
                    Foreground = CodeBrush,
                    Background = CodeBackgroundBrush
                });
                index = next;
                continue;
            }

            if (TryReadLink(normalized, index, out var label, out next))
            {
                inlines.Add(new Run(label)
                {
                    Foreground = AccentBrush,
                    TextDecorations = TextDecorations.Underline
                });
                index = next;
                continue;
            }

            var end = index + 1;
            while (end < normalized.Length
                   && normalized[end] != '\n'
                   && normalized[end] != '`'
                   && normalized[end] != '['
                   && !(normalized[end] == '*' && end + 1 < normalized.Length && normalized[end + 1] == '*')
                   && !(normalized[end] == '_' && end + 1 < normalized.Length && normalized[end + 1] == '_'))
            {
                end++;
            }

            inlines.Add(new Run(normalized[index..end]));
            index = end;
        }
    }

    private static bool TryReadWrapped(
        string text,
        int start,
        string marker,
        out string value,
        out int next)
    {
        value = string.Empty;
        next = start;
        if (!text.AsSpan(start).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var closing = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (closing <= start + marker.Length)
        {
            return false;
        }

        value = text[(start + marker.Length)..closing];
        next = closing + marker.Length;
        return true;
    }

    private static bool TryReadLink(string text, int start, out string label, out int next)
    {
        label = string.Empty;
        next = start;
        if (text[start] != '[')
        {
            return false;
        }

        var labelEnd = text.IndexOf("](", start, StringComparison.Ordinal);
        var targetEnd = labelEnd < 0 ? -1 : text.IndexOf(')', labelEnd + 2);
        if (labelEnd <= start + 1 || targetEnd < 0)
        {
            return false;
        }

        label = text[(start + 1)..labelEnd];
        next = targetEnd + 1;
        return true;
    }

    private static SolidColorBrush BrushFrom(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakTagRegex();
}
