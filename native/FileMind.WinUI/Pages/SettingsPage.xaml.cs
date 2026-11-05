using Microsoft.UI.Xaml.Automation;
using System.Diagnostics;
using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileMind.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly (string Key, string Label)[] Menus =
        [("library", "Library"), ("discover", "Discover"), ("spaces", "Spaces"), ("duplicates", "Duplicates")];
    private static readonly (string Key, string Label)[] Sections =
        [("types", "Types"), ("sources", "Sources"), ("colours", "Colours"), ("folders", "Folders"), ("moods", "Looks & moods"), ("spaces", "Spaces"), ("details", "Refine")];
    private static readonly (string Key, string Label)[] Layouts =
        [("masonry", "Masonry"), ("rows", "Rows"), ("contact", "Contact sheet"), ("story", "Story")];

    private bool _ready;
    private AppSettings St => AppRuntime.Current.Settings;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await FillAsync();
        GlassSlider.Value = St.Transparency * 100;
        SizeSlider.Value = St.TileSize;
        GapSlider.Value = St.TileGap;
        NamesSwitch.IsOn = St.ShowNames;
        VolumeSlider.Value = St.Volume * 100;
        BuildAll();
        _ready = true;
        Labels();
    }

    private void BuildAll() { BuildThemes(); BuildAccents(); BuildLayouts(); BuildList(MenuList, Menus, St.MenuOrder, St.MenuHidden, RailChanged); BuildList(SectionList, Sections, St.SectionOrder, St.SectionHidden, SectionsChanged); }

    private void Save() { try { St.Save(AppRuntime.Current.Paths); } catch (Exception) { } }

    private void Labels()
    {
        SizeLabel.Text = $"Tile size · {St.TileSize:0}px";
        GapLabel.Text = $"Spacing · {St.TileGap:0}px";
        VolumeLabel.Text = $"Default volume · {St.Volume * 100:0}%";
    }

    // ---- Appearance ----
    // Each theme is a small picture of the window (sidebar, page, accent line), so you choose by look, not by name.
    private void BuildThemes()
    {
        ThemeChips.Children.Clear();
        var accent = (SolidColorBrush)Ui.Brush("AccentBrush");
        foreach (var p in ThemeEngine.All)
        {
            var selected = p.Key == St.Theme;
            var ink = p.Dark ? Windows.UI.Color.FromArgb(0x55, 255, 255, 255) : Windows.UI.Color.FromArgb(0x40, 0, 0, 0);
            var pic = new Grid { Width = 104, Height = 64, ColumnSpacing = 0 };
            pic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            pic.ColumnDefinitions.Add(new ColumnDefinition());
            var rail = new StackPanel { Background = new SolidColorBrush(p.Rail), Padding = new Thickness(6, 8, 6, 0), Spacing = 5 };
            rail.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = accent });
            for (var i = 0; i < 3; i++) rail.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ink) });
            var page = new Grid { Background = new SolidColorBrush(p.Panel), Padding = new Thickness(7) };
            var tiles = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            tiles.ColumnDefinitions.Add(new ColumnDefinition()); tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.RowDefinitions.Add(new RowDefinition()); tiles.RowDefinitions.Add(new RowDefinition());
            for (var i = 0; i < 4; i++)
            {
                var t = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ink) };
                Grid.SetColumn(t, i % 2); Grid.SetRow(t, i / 2); tiles.Children.Add(t);
            }
            page.Children.Add(tiles);
            Grid.SetColumn(page, 1);
            pic.Children.Add(rail); pic.Children.Add(page);
            var frame = new Border { CornerRadius = new CornerRadius(6), Child = pic,
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected ? accent : Ui.Brush("GlassStrokeBrush") };
            var col = new StackPanel { Spacing = 6 };
            col.Children.Add(frame);
            col.Children.Add(new TextBlock { Text = p.Name, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = selected ? null : Ui.Brush("GlassTextSecondaryBrush") });
            var btn = new Button { Content = col, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(btn, p.Name + " theme");
            btn.Click += (_, _) => { St.Theme = p.Key; Save(); App.Window?.ApplyGlass(); BuildThemes(); BuildAccents(); };
            ThemeChips.Children.Add(btn);
        }
    }

    private void BuildAccents()
    {
        AccentRow.Children.Clear();
        foreach (var hex in ThemeEngine.Accents)
        {
            var c = Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
            var selected = string.Equals(hex, St.Accent, StringComparison.OrdinalIgnoreCase);
            var b = new Button { Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(selected ? (ThemeEngine.Find(St.Theme).Dark ? Colors.White : Windows.UI.Color.FromArgb(255, 24, 24, 28)) : Colors.Transparent), BorderThickness = new Thickness(2.5) };
            ToolTipService.SetToolTip(b, hex);
            b.Click += (_, _) => { St.Accent = hex; Save(); App.Window?.ApplyGlass(); BuildAccents(); };
            AccentRow.Children.Add(b);
        }
    }

    private void Glass_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        St.Transparency = GlassSlider.Value / 100;
        GlassValue.Text = $"{GlassSlider.Value:0} %";
        Save();
        App.Window?.ApplyGlass();
    }

    // ---- Library look ----
    private void BuildLayouts()
    {
        LayoutRow.Children.Clear();
        foreach (var (key, label) in Layouts)
        {
            var chip = new ToggleButton { Style = Ui.Style("GlassChip"), Content = label, IsChecked = key == St.Layout, Padding = new Thickness(13, 6, 13, 6), CornerRadius = new CornerRadius(6) };
            chip.Click += (_, _) => { St.Layout = key; Save(); LibraryPage.Current?.ApplyLook(); BuildLayouts(); };
            LayoutRow.Children.Add(chip);
        }
    }

    private void Look_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        St.TileSize = SizeSlider.Value; St.TileGap = GapSlider.Value;
        Save(); Labels(); LibraryPage.Current?.ApplyLook();
    }

    private void Names_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        St.ShowNames = NamesSwitch.IsOn; Save(); LibraryPage.Current?.ApplyLook();
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        St.Volume = VolumeSlider.Value / 100; Save(); Labels(); ViewerPage.SetDefaultVolume(St.Volume);
    }

    // ---- Reorderable lists (sidebar menu and filter sections) ----
    private void RailChanged() { Save(); App.Window?.BuildRail(); }
    private void SectionsChanged() { Save(); LibraryPage.Current?.ApplySections(); }

    private void BuildList(StackPanel host, (string Key, string Label)[] known, List<string> order, List<string> hidden, Action changed)
    {
        host.Children.Clear();
        var labels = known.ToDictionary(k => k.Key, k => k.Label);
        var keys = order.Where(labels.ContainsKey).Distinct().Concat(labels.Keys.Except(order)).ToList();
        order.Clear(); order.AddRange(keys);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i]; var idx = i;
            var row = new Grid { Padding = new Thickness(12, 6, 8, 6), CornerRadius = new CornerRadius(4), Background = Ui.Brush("GlassFillBrush"), ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = labels[key], VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 });
            var up = Arrow("back", idx > 0, () => Move(order, idx, -1, host, known, hidden, changed), 90);
            var down = Arrow("back", idx < keys.Count - 1, () => Move(order, idx, 1, host, known, hidden, changed), -90);
            var sw = new ToggleSwitch { IsOn = !hidden.Contains(key), OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0) };
            sw.Toggled += (_, _) => { if (sw.IsOn) hidden.Remove(key); else if (!hidden.Contains(key)) hidden.Add(key); changed(); };
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); Grid.SetColumn(sw, 3);
            row.Children.Add(up); row.Children.Add(down); row.Children.Add(sw);
            host.Children.Add(row);
        }
    }

    private static Button Arrow(string icon, bool enabled, Action click, double rotate)
    {
        var b = new Button { Style = Ui.Style("GlassButton"), Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(5), IsEnabled = enabled,
            Content = new LineIcon { Kind = icon, Size = 13, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = rotate } } };
        b.Click += (_, _) => click();
        return b;
    }

    private void Move(List<string> order, int i, int d, StackPanel host, (string Key, string Label)[] known, List<string> hidden, Action changed)
    {
        (order[i], order[i + d]) = (order[i + d], order[i]);
        BuildList(host, known, order, hidden, changed);
        changed();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        St.Theme = d.Theme; St.Accent = d.Accent; St.Transparency = d.Transparency;
        St.Layout = d.Layout; St.TileSize = d.TileSize; St.TileGap = d.TileGap; St.ShowNames = d.ShowNames; St.Volume = d.Volume;
        St.MenuOrder = d.MenuOrder; St.MenuHidden = d.MenuHidden; St.SectionOrder = d.SectionOrder; St.SectionHidden = d.SectionHidden;
        Save();
        _ready = false;
        GlassSlider.Value = St.Transparency * 100;
        SizeSlider.Value = St.TileSize; GapSlider.Value = St.TileGap; NamesSwitch.IsOn = St.ShowNames; VolumeSlider.Value = St.Volume * 100;
        BuildAll(); _ready = true; Labels();
        App.Window?.ApplyGlass(); LibraryPage.Current?.ApplyLook(); LibraryPage.Current?.ApplySections(); ViewerPage.SetDefaultVolume(St.Volume);
    }

    // ---- Engine, folders, about ----
    private async Task FillAsync()
    {
        var rt = AppRuntime.Current;
        var gpu = rt.Gpu;
        string? device = null;
        try { device = (await rt.Api!.GetModelAsync()).Device; } catch (Exception) { }

        Running.Text = rt.SourceLabel is not null
            ? $"Running from a {rt.SourceLabel}" + (device is null ? "." : $" on {device}.")
            : rt.HasBundledEngine ? "Running" + (device is null ? "." : $" on {device}.")
            : $"Running the {rt.EditionInfo.Title} engine" + (device is null ? "." : $" on {device}.");
        GpuLine.Text = $"This PC: {gpu.Headline}. {gpu.Reason}";

        NvidiaRadio.IsEnabled = gpu.CudaUsable;
        foreach (RadioButton r in Choice.Items)
            if ((string)r.Tag == rt.Settings.Engine) Choice.SelectedItem = r;
        if (Choice.SelectedItem is null) Choice.SelectedIndex = 0;

        var fromSource = rt.SourceLabel is not null;
        Apply.IsEnabled = !fromSource;
        ChoiceNote.Text = fromSource
            ? "This copy runs from a source checkout, so the choice only applies to installed builds."
            : rt.HasBundledEngine ? "Switching to NVIDIA downloads GPU support (about 2.8 GB) the first time. AMD and Intel graphics run on the processor. Your library is kept."
            : "Switching downloads the other engine the first time. Your library and settings are kept.";

        try
        {
            var roots = await rt.Api!.GetRootsAsync();
            Roots.ItemsSource = roots.Select(r => $"{r.Path}   ·   {r.Files:N0} files").ToList();
        }
        catch (Exception) { }

        About.Text = $"File Mind {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} (native)\nData: {rt.Paths.Root}";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var rt = AppRuntime.Current;
        rt.Settings.Engine = (Choice.SelectedItem as RadioButton)?.Tag as string ?? "auto";
        rt.Settings.Save(rt.Paths);
        Apply.IsEnabled = false;
        await App.Window!.RestartEngineAsync();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppRuntime.Current.Paths.Root}\"") { UseShellExecute = false });
}
