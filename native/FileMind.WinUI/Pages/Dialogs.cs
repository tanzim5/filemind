using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FileMind.Pages;

/// <summary>The one look for every dialog in the app: the theme's card surface (frosted or liquid), a hairline, radius 10,
/// a serif title, an accent-filled primary button at the right and a quiet secondary one, 20 px of padding all round.
/// The dialog draws its own title and buttons, so the stock grey footer band never appears.</summary>
internal static class Dialogs
{
    /// <summary>Shows <paramref name="body"/> in a dialog. <paramref name="onPrimary"/> runs when the primary button is pressed and
    /// returns an error to show (the dialog stays open) or null to close it. Returns true when the primary action went through.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, string title, UIElement? body, string? primary, string? secondary = "Cancel",
        Func<Task<string?>>? onPrimary = null, double width = 440, bool enterConfirms = true, Action<Action>? gotClose = null)
    {
        var dlg = new ContentDialog { XamlRoot = root, RequestedTheme = ThemeEngine.Element(), Padding = new Thickness(0) };
        var surface = Ui.Brush(ThemeEngine.Liquid ? "OverlayBrush" : "SurfaceBrush");
        dlg.Resources["ContentDialogBackground"] = surface;
        dlg.Resources["ContentDialogBorderBrush"] = Ui.Brush("GlassStrokeBrush");
        dlg.Resources["ContentDialogBorderThemeThickness"] = new Thickness(1);
        dlg.Resources["OverlayCornerRadius"] = new CornerRadius(10);
        dlg.Resources["ContentDialogPadding"] = new Thickness(0);
        dlg.Resources["ContentDialogMinWidth"] = width;
        dlg.Resources["ContentDialogMaxWidth"] = width;
        dlg.Resources["ContentDialogMaxHeight"] = 900.0;
        dlg.CornerRadius = new CornerRadius(10);
        dlg.BorderThickness = new Thickness(1);
        dlg.BorderBrush = Ui.Brush("GlassStrokeBrush");
        dlg.Background = surface;

        var ok = false;
        gotClose?.Invoke(() => dlg.Hide());
        var problem = new TextBlock { Foreground = Ui.Hex("#FF7A7A"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Button? primaryBtn = null;

        async Task Confirm()
        {
            if (primaryBtn is { IsEnabled: false }) return;
            if (primaryBtn is not null) primaryBtn.IsEnabled = false;
            try
            {
                var err = onPrimary is null ? null : await onPrimary();
                if (err is null) { ok = true; dlg.Hide(); return; }
                problem.Text = err; problem.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { problem.Text = ex.Message; problem.Visibility = Visibility.Visible; }
            finally { if (primaryBtn is not null) primaryBtn.IsEnabled = true; }
        }

        var stack = new StackPanel { Spacing = 16, Padding = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = title, Style = Ui.Style("TitleText"), FontSize = 22, TextWrapping = TextWrapping.Wrap });
        if (body is not null) stack.Children.Add(body);
        stack.Children.Add(problem);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        if (secondary is not null)
        {
            var quiet = new Button { Content = secondary, Style = Ui.Style("GlassButton"), Padding = new Thickness(16, 7, 16, 7), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            quiet.Click += (_, _) => dlg.Hide();
            buttons.Children.Add(quiet);
        }
        if (primary is not null)
        {
            primaryBtn = new Button { Content = primary, Style = Ui.Style("GlassAccentButton"), Padding = new Thickness(18, 7, 18, 7) };
            primaryBtn.Click += async (_, _) => await Confirm();
            buttons.Children.Add(primaryBtn);
        }
        stack.Children.Add(buttons);

        dlg.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        dlg.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { e.Handled = true; dlg.Hide(); }
            else if (e.Key == VirtualKey.Enter && enterConfirms && primary is not null && e.OriginalSource is not (Button or ToggleButton or TextBox { AcceptsReturn: true }))
            {
                e.Handled = true; await Confirm();
            }
        };
        await dlg.ShowAsync();
        return ok;
    }

    /// <summary>A short message with an OK button.</summary>
    public static Task ShowMessageAsync(XamlRoot root, string title, UIElement body) => ShowAsync(root, title, body, null, "Close");

    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Ui.Brush("GlassTextPrimaryBrush") };
}

/// <summary>A row of selectable chips of which exactly one is on, like radio buttons that look like the rest of the app.</summary>
internal sealed class ChipGroup
{
    public WrapPanel Panel { get; } = new() { Spacing = 6 };
    public string Value { get; private set; } = "";
    public event Action? Changed;
    private readonly List<(string Key, ToggleButton Chip)> _chips = [];

    public ChipGroup(IEnumerable<(string Key, string Label)> options, string? initial = null)
    {
        foreach (var (key, label) in options)
        {
            var chip = new ToggleButton
            {
                Style = Ui.Style("GlassChip"), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6),
                Content = new TextBlock { Text = label, FontSize = 12.5, Foreground = Ui.Brush("GlassTextPrimaryBrush") },
            };
            var k = key;
            chip.Click += (_, _) => Select(k);
            _chips.Add((key, chip)); Panel.Children.Add(chip);
        }
        if (_chips.Count > 0) Select(initial ?? _chips[0].Key, raise: false);
    }

    public void Select(string key, bool raise = true)
    {
        foreach (var (k, c) in _chips) c.IsChecked = k == key;
        if (Value == key) return;
        Value = key;
        if (raise) Changed?.Invoke();
    }

    public bool IsEnabled { set { foreach (var (_, c) in _chips) c.IsEnabled = value; } }
}
