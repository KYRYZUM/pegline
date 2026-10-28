using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Shelves;

namespace Pegline.App.Platform;

/// <summary>Small pieces shared by the palette, the pickers and the prompts so they look like one family.</summary>
internal static class SurfaceParts
{
    public static Border HeaderButton(ShelfPalette palette, string glyph, string tooltip, Action click, string? label = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 11, Foreground = palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        if (label is not null)
            content.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = palette.Muted, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Border
        {
            MinWidth = 24,
            Height = 24,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = content,
        };
        button.MouseEnter += (_, _) => button.Background = palette.Chip;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }

    public static Border Keycap(ShelfPalette palette, string text) => new()
    {
        CornerRadius = new CornerRadius(4),
        BorderBrush = palette.PanelBorder,
        BorderThickness = new Thickness(1),
        Background = palette.Chip,
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = palette.Foreground },
    };

    public static TextBlock Hint(ShelfPalette palette, string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = palette.Muted,
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A rounded text button; <paramref name="primary"/> uses the accent-tinted fill.</summary>
    public static Border Button(ShelfPalette palette, string text, bool primary, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, Foreground = palette.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var idle = primary ? palette.ChipActive : palette.Chip;
        var button = new Border
        {
            MinWidth = 84,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = idle,
            Cursor = Cursors.Hand,
            Child = label,
        };
        button.MouseEnter += (_, _) => button.Background = palette.Highlight;
        button.MouseLeave += (_, _) => button.Background = idle;
        button.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }

    /// <summary>A borderless single-line input with a glyph and a placeholder, as in the palette.</summary>
    public static Grid Input(ShelfPalette palette, TextBox box, TextBlock placeholder, string glyph, string placeholderText)
    {
        var input = new Grid { Height = 32 };
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition());
        input.Children.Add(new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 18, Foreground = palette.Muted, VerticalAlignment = VerticalAlignment.Center, Width = 24 });
        box.FontSize = 19;
        box.Background = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);
        box.Foreground = palette.Foreground;
        box.CaretBrush = palette.Foreground;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(box, 1);
        input.Children.Add(box);
        placeholder.Text = placeholderText;
        placeholder.FontSize = 19;
        placeholder.Foreground = palette.Muted;
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(11, 0, 0, 0);
        Grid.SetColumn(placeholder, 1);
        input.Children.Add(placeholder);
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return input;
    }
}
