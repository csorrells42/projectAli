using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ali.UI;

internal sealed class OwnerPasswordDialog : Window
{
    private readonly PasswordBox password = new();
    private readonly PasswordBox? confirmation;
    private readonly TextBlock validation = new()
    {
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 154, 154)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private OwnerPasswordDialog(string title, string instruction, bool confirm)
    {
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 17, 20));
        Foreground = System.Windows.Media.Brushes.White;
        NativeTitleBarTheme.ApplyDarkTitleBar(this);

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock
        {
            Text = instruction,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 216, 224)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(Label("Owner password"));
        password.MinWidth = 360;
        password.Margin = new Thickness(0, 4, 0, 8);
        content.Children.Add(password);

        if (confirm)
        {
            content.Children.Add(Label("Confirm password"));
            confirmation = new PasswordBox
            {
                MinWidth = 360,
                Margin = new Thickness(0, 4, 0, 2)
            };
            content.Children.Add(confirmation);
        }

        content.Children.Add(validation);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var accept = new System.Windows.Controls.Button { Content = "OK", Width = 82, IsDefault = true };
        accept.Click += AcceptClicked;
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => password.Focus();
    }

    public string EnteredPassword { get; private set; } = string.Empty;

    public static string? Prompt(Window owner, string title, string instruction, bool confirm = false)
    {
        var dialog = new OwnerPasswordDialog(title, instruction, confirm) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.EnteredPassword : null;
    }

    private void AcceptClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(password.Password))
        {
            validation.Text = "Enter the owner password.";
            return;
        }
        if (confirmation is not null && !string.Equals(password.Password, confirmation.Password, StringComparison.Ordinal))
        {
            validation.Text = "The two passwords do not match.";
            confirmation.Clear();
            confirmation.Focus();
            return;
        }
        if (confirmation is not null && password.Password.Length < 8)
        {
            validation.Text = "Use an owner password at least eight characters long.";
            password.Clear();
            confirmation.Clear();
            password.Focus();
            return;
        }

        EnteredPassword = password.Password;
        DialogResult = true;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = System.Windows.Media.Brushes.White
    };
}
