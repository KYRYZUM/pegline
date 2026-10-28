using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pegline.App.Localization;
using Pegline.App.Shelves;

namespace Pegline.App.Platform;

/// <summary>A small modal name field (new folder, rename folder or tag). Enter confirms, Esc cancels.</summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _input = new();
    private readonly TextBlock _error = new();
    private readonly Func<string, string?>? _validate;

    private PromptWindow(string title, string initial, ShelfPalette palette, Func<string, string?>? validate)
    {
        _validate = validate;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = title;

        _input.Text = initial;
        _input.FontSize = 14;
        _input.Width = 340;
        _input.Margin = new Thickness(0, 12, 0, 0);
        _input.Padding = new Thickness(6, 5, 6, 5);
        _input.Background = palette.Chip;
        _input.Foreground = palette.Foreground;
        _input.CaretBrush = palette.Foreground;
        _input.BorderBrush = palette.Accent;
        _error.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        _error.FontSize = 11.5;
        _error.Margin = new Thickness(0, 6, 0, 0);
        _error.TextWrapping = TextWrapping.Wrap;
        _error.MaxWidth = 340;
        _error.Visibility = Visibility.Collapsed;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Button(Loc.T("Cancel"), palette.Chip, palette, () => DialogResult = false));
        buttons.Children.Add(Button(Loc.T("OK"), palette.ChipActive, palette, Confirm));

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = palette.Foreground });
        panel.Children.Add(_input);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = palette.Surface,
            BorderBrush = palette.PanelBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.45 },
            Child = panel,
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Confirm();
            else if (e.Key == Key.Escape) DialogResult = false;
            else return;
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            Keyboard.Focus(_input);
            _input.SelectAll();
        };
    }

    public string Value => _input.Text.Trim();

    /// <summary>Asks for a name. <paramref name="validate"/> returns an error to show, or null to accept.</summary>
    public static string? Ask(Window owner, string title, string initial, ShelfPalette palette, Func<string, string?>? validate = null)
    {
        var prompt = new PromptWindow(title, initial, palette, validate) { Owner = owner, Topmost = owner.Topmost };
        return prompt.ShowDialog() == true ? prompt.Value : null;
    }

    private void Confirm()
    {
        var error = Value.Length == 0 ? Loc.T("Enter a name") : _validate?.Invoke(Value);
        if (error is not null)
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private static Border Button(string text, Brush background, ShelfPalette palette, Action click)
    {
        var button = new Border
        {
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = background,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = palette.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) },
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }
}
