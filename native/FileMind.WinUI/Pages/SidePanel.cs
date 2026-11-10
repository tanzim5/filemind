using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileMind.Pages;

/// <summary>A page's own content for the left sidebar: small captions over full-width rows with a quiet count,
/// in the same style as the destinations above it.</summary>
internal sealed class SidePanel
{
    private readonly StackPanel _stack = new() { Spacing = 2, Padding = new Thickness(0, 0, 0, 24) };
    public ScrollViewer Root { get; }

    public SidePanel() => Root = new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };

    public void Clear() => _stack.Children.Clear();

    public void Header(string text) =>
        _stack.Children.Add(new TextBlock { Text = text, Style = Ui.Style("SectionLabel"), Margin = new Thickness(10, _stack.Children.Count == 0 ? 2 : 16, 0, 6) });

    public void Note(string text) =>
        _stack.Children.Add(new TextBlock { Text = text, Style = Ui.Style("SecondaryText"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 8, 6) });

    /// <summary>A row. When <paramref name="selected"/> is given the row shows as the current one.</summary>
    public ToggleButton Row(string icon, string label, string? count, Action click, bool selected = false)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new LineIcon { Kind = icon, Size = 15, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        var name = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); row.Children.Add(name);
        if (!string.IsNullOrEmpty(count))
        {
            var c = new TextBlock { Text = count, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") };
            Grid.SetColumn(c, 2); row.Children.Add(c);
        }
        var b = new ToggleButton { Style = Ui.Style("RailItem"), Content = row, IsChecked = selected };
        b.Click += (_, _) => { b.IsChecked = selected; click(); };
        _stack.Children.Add(b);
        return b;
    }
}
