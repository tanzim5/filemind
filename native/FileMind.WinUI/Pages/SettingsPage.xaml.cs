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
    private static readonly (string Key, string Label)[] Layouts =
        [("masonry", "Masonry"), ("rows", "Rows"), ("contact", "Contact sheet"), ("story", "Story")];

    private bool _ready;
    private AppSettings St => AppRuntime.Current.Settings;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await FillAsync();
        GlassSlider.Value = St.Transparency * 100;
        GlassSwitch.IsOn = St.Glass; GlassSlider.IsEnabled = St.Glass;
        VoiceEngineBox.SelectedIndex = St.VoiceEngine == "windows" ? 1 : 0; ShowVoiceNote();
        MinSideBox.SelectedIndex = Math.Max(0, new[] { 0, 256, 300, 512, 720, 1080 }.ToList().IndexOf(St.MinSide));
        MinSideModeBox.SelectedIndex = St.MinSideMode == "long" ? 1 : 0;
        BuildOrder();
        _ = BuildStorageAsync();
        ShowHotkey(); KeepSwitch.IsOn = St.KeepRunning;
        SimilarChoice.SelectedIndex = St.SimilarFiles == "side" ? 1 : 0;
        SizeSlider.Value = St.TileSize;
        GapSlider.Value = St.TileGap;
        NamesSwitch.IsOn = St.ShowNames;
        VolumeSlider.Value = St.Volume * 100;
        BuildAll(); BuildSections();
        _ready = true;
        Labels(); GlassValue.Text = $"{GlassSlider.Value:0} %";
    }

    private void Similar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        St.SimilarFiles = SimilarChoice.SelectedIndex == 1 ? "side" : "below";
        Save();
    }

    private async void MinSide_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || MinSideBox.SelectedItem is not ComboBoxItem n || MinSideModeBox.SelectedItem is not ComboBoxItem m) return;
        St.MinSide = int.Parse((string)n.Tag); St.MinSideMode = (string)m.Tag;
        Save();
        if (AppRuntime.Current.Api is { } api) await api.SetMinSideAsync(St.MinSide, St.MinSideMode);
        LibraryPage.Current?.RefreshAfterPrefs();
    }

    private async void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SpeedChoice.SelectedItem is not RadioButton r) return;
        St.IndexSpeed = (string)r.Tag;
        Save();
        if (AppRuntime.Current.Api is { } api) await api.SetIndexSpeedAsync(St.IndexSpeed);
    }

    // ---- the order of the Library's sidebar sections: drag a row to move it ----
    private void BuildOrder()
    {
        OrderList.Items.Clear();
        foreach (var key in LibraryPage.OrderedSectionKeys())
        {
            var name = LibraryPage.SidebarSections.First(s => s.Key == key).Name;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 8, 6, 8), Tag = key };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = "\uE76F", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, name);
            OrderList.Items.Add(row);
        }
    }

    private void Order_DragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var keys = OrderList.Items.OfType<FrameworkElement>().Select(r => (string)r.Tag).ToList();
        if (keys.SequenceEqual(LibraryPage.OrderedSectionKeys())) return;
        St.SidebarOrder = keys;
        Save(); LibraryPage.Current?.ApplySections();
    }

    private void OrderReset_Click(object sender, RoutedEventArgs e)
    {
        St.SidebarOrder = [];
        Save(); BuildOrder(); LibraryPage.Current?.ApplySections();
    }

    // A tile's name is always shown. The chosen tile gets the accent ring (set by the caller) and a small check in its corner.
    private static UIElement Tile(Border frame, bool selected, Brush accent)
    {
        if (!selected) return frame;
        var g = new Grid();
        g.Children.Add(frame);
        g.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0),
            Child = new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 10, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        return g;
    }

    private static TextBlock Label(string text, bool selected) => new()
    {
        Text = text, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Ui.Brush(selected ? "GlassTextPrimaryBrush" : "GlassTextSecondaryBrush"),
    };

    // ---- Section list: chips at the top that scroll to a card; the chip of the card in view is lit ----
    private readonly List<(string Name, FrameworkElement Card, ToggleButton Chip)> _sections = [];
    private bool _jumping;

    private void BuildSections()
    {
        foreach (var (name, card) in new (string, FrameworkElement)[] { ("Appearance", SecAppearance), ("Library", SecLibrary), ("Quick search", SecQuick), ("Sidebar", SecSidebar), ("Viewer", SecViewer), ("Engine", SecEngine), ("Folders", SecFolders), ("Storage", SecStorage), ("About", SecAbout) })
        {
            var chip = new ToggleButton { Style = Ui.Style("GlassChip"), Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(6), Content = new TextBlock { Text = name, FontSize = 12.5 } };
            var c = card;
            chip.Click += (_, _) =>
            {
                var y = c.TransformToVisual((UIElement)Scroller.Content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                _jumping = true;
                Scroller.ChangeView(null, Math.Max(0, y - 4), null, !Anim.On);
                Highlight(c);
            };
            _sections.Add((name, card, chip)); SectionNav.Children.Add(chip);
        }
        Highlight(SecAppearance);
    }

    private void Highlight(FrameworkElement current) { foreach (var s in _sections) s.Chip.IsChecked = ReferenceEquals(s.Card, current); }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate && _jumping) return;
        _jumping = false;
        if (_sections.Count == 0) return;
        var off = Scroller.VerticalOffset;
        FrameworkElement cur = _sections[0].Card;
        foreach (var s in _sections)
            if (s.Card.TransformToVisual((UIElement)Scroller.Content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y <= off + 40) cur = s.Card;
        if (Scroller.ScrollableHeight - off < 4) cur = _sections[^1].Card;
        Highlight(cur);
    }

    private void BuildAll() { BuildThemes(); BuildSurfaces(); BuildAccents(); BuildLayouts(); }

    // ---- Quick search shortcut: click, then press the new combination ----
    private bool _recording;
    private bool _recordingVoice;
    private void ShowHotkey()
    {
        HotkeyBtn.Content = (App.ActiveHotkey ?? St.QuickSearchHotkey).Replace("+", "  +  ");
        VoiceHotkeyBtn.Content = (App.ActiveVoiceHotkey ?? St.VoiceHotkey).Replace("+", "  +  ");
    }
    private void VoiceHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recording = true; _recordingVoice = true; VoiceHotkeyBtn.Content = "Press the new shortcut..."; HotkeyNote.Visibility = Visibility.Collapsed;
    }
    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _recording = true; _recordingVoice = false; HotkeyBtn.Content = "Press the new shortcut..."; HotkeyNote.Visibility = Visibility.Collapsed;
    }
    private void Hotkey_Lost(object sender, RoutedEventArgs e) { _recording = false; ShowHotkey(); }
    private void Hotkey_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        var key = e.Key;
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows) { e.Handled = true; return; }
        e.Handled = true;
        if (key == Windows.System.VirtualKey.Escape) { _recording = false; ShowHotkey(); return; }
        bool Down(Windows.System.VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var parts = new List<string>();
        if (Down(Windows.System.VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(Windows.System.VirtualKey.Menu)) parts.Add("Alt");
        if (Down(Windows.System.VirtualKey.Shift)) parts.Add("Shift");
        var name = key == Windows.System.VirtualKey.Space ? "Space"
                 : key is >= Windows.System.VirtualKey.F1 and <= Windows.System.VirtualKey.F24 ? "F" + (key - Windows.System.VirtualKey.F1 + 1)
                 : key is >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z or >= Windows.System.VirtualKey.Number0 and <= Windows.System.VirtualKey.Number9 ? ((char)(int)key).ToString() : null;
        if (name is null || parts.Count == 0) { HotkeyNote.Text = "Use a modifier (Ctrl, Alt or Shift) plus a letter, number, F-key or Space."; HotkeyNote.Visibility = Visibility.Visible; return; }
        parts.Add(name);
        var combo = string.Join("+", parts);
        _recording = false;
        if (_recordingVoice ? App.SetVoiceHotkey(combo) : App.SetHotkey(combo)) { if (_recordingVoice) St.VoiceHotkey = combo; else St.QuickSearchHotkey = combo; Save(); HotkeyNote.Visibility = Visibility.Collapsed; }
        else { HotkeyNote.Text = $"{combo.Replace("+", " + ")} is already used by another app. Pick a different one."; HotkeyNote.Visibility = Visibility.Visible; }
        ShowHotkey();
    }
    private void Keep_Toggled(object sender, RoutedEventArgs e) { if (!_ready) return; St.KeepRunning = KeepSwitch.IsOn; Save(); }

    private void Save() { try { St.Save(AppRuntime.Current.Paths); } catch (Exception ex) { FileMind.Core.Diag.Log("Settings.save", ex); } }

    private void Labels()
    {
        SizeLabel.Text = $"{St.TileSize:0}px";
        GapLabel.Text = $"{St.TileGap:0}px";
        VolumeLabel.Text = $"{St.Volume * 100:0}%";
    }

    // ---- Appearance ----
    // Each theme is a small picture of the window (sidebar, page, accent line), so you choose by look, not by name.
    private void BuildThemes()
    {
        ThemeChips.Children.Clear();
        var accent = (SolidColorBrush)Ui.Brush("AccentBrush")!;
        // The last tile follows Windows' own light or dark setting.
        foreach (var p in ThemeEngine.All.Append(ThemeEngine.Find("system") with { Key = "system", Name = "Match Windows" }))
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
            col.Children.Add(Tile(frame, selected, accent));
            col.Children.Add(Label(p.Name, selected));
            var btn = new Button { Content = col, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(btn, p.Name + " theme");
            btn.Click += (_, _) => { St.Theme = p.Key; Save(); App.Window?.ApplyGlass(); BuildThemes(); BuildSurfaces(); BuildAccents(); };
            ThemeChips.Children.Add(btn);
        }
    }

    // Two tiles: a small picture of a floating bar in each style, so the choice is visible before it is made.
    private void BuildSurfaces()
    {
        SurfaceRow.Children.Clear();
        var accent = (SolidColorBrush)Ui.Brush("AccentBrush")!;
        foreach (var (key, name) in new[] { ("frosted", "Frosted glass"), ("liquid", "Liquid glass") })
        {
            var selected = St.Surface == key;
            var liquid = key == "liquid";
            var back = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
            back.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x3B, 0x5B, 0xDB), Offset = 0 });
            back.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xFF, 0x7A, 0x45), Offset = 1 });
            var stroke = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = liquid ? new Windows.Foundation.Point(0, 1) : new Windows.Foundation.Point(1, 1) };
            stroke.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(liquid ? (byte)0xF0 : (byte)0x70, 255, 255, 255), Offset = 0 });
            stroke.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(liquid ? (byte)0x30 : (byte)0x20, 255, 255, 255), Offset = 1 });
            var bar = new Border { Height = 26, Width = 76, CornerRadius = new CornerRadius(liquid ? 13 : 7), BorderThickness = new Thickness(1), BorderBrush = stroke,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(liquid ? (byte)0x30 : (byte)0x55, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++) dots.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 255, 255, 255)) });
            bar.Child = dots;
            var pic = new Grid { Width = 128, Height = 64, Background = back };
            pic.Children.Add(bar);
            var frame = new Border { CornerRadius = new CornerRadius(6), Child = pic, BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected ? accent : Ui.Brush("GlassStrokeBrush") };
            var col = new StackPanel { Spacing = 6 };
            col.Children.Add(Tile(frame, selected, accent));
            col.Children.Add(Label(name, selected));
            var btn = new Button { Content = col, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(btn, name);
            btn.Click += (_, _) => { St.Surface = key; Save(); App.Window?.ApplyGlass(); BuildSurfaces(); };
            SurfaceRow.Children.Add(btn);
        }
    }

    private void BuildAccents()
    {
        AccentRow.Children.Clear();
        // Six of our own, then the accent colour Windows itself is using.
        foreach (var hex in ThemeEngine.Accents.Append("system"))
        {
            var c = hex == "system" ? ThemeEngine.SystemAccent() : Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
            var selected = string.Equals(hex, St.Accent, StringComparison.OrdinalIgnoreCase);
            var b = new Button { Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(selected ? (ThemeEngine.Find(St.Theme).Dark ? Colors.White : Windows.UI.Color.FromArgb(255, 24, 24, 28)) : Colors.Transparent), BorderThickness = new Thickness(2.5) };
            ToolTipService.SetToolTip(b, hex == "system" ? "Windows accent colour" : hex);
            b.Click += (_, _) => { St.Accent = hex; Save(); App.Window?.ApplyGlass(); BuildAccents(); };
            AccentRow.Children.Add(b);
        }
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _glassSave;
    private void GlassSaveTick(object? s, object e) { _glassSave?.Stop(); Save(); }

    private void GlassSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        St.Glass = GlassSwitch.IsOn; GlassSlider.IsEnabled = St.Glass; Save(); App.Window?.ApplyGlass();
    }

    private void ShowVoiceNote() => VoiceEngineNote.Text = St.VoiceEngine == "windows"
        ? "Windows reads your voice, with no download. Windows may send the audio to Microsoft (needs Online speech recognition on)."
        : "Whisper reads your voice on this PC, so nothing is uploaded. One-time download of about 145 MB.";

    private void VoiceEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        St.VoiceEngine = VoiceEngineBox.SelectedIndex == 1 ? "windows" : "whisper"; Save(); ShowVoiceNote();
    }

    private void Glass_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        St.Transparency = GlassSlider.Value / 100;
        GlassValue.Text = $"{GlassSlider.Value:0} %";
        App.Window?.ApplyGlassLight();
        _glassSave ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _glassSave.Tick -= GlassSaveTick; _glassSave.Tick += GlassSaveTick; _glassSave.Stop(); _glassSave.Start();
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

    private void SidebarStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SidebarStyleChoice.SelectedItem is not RadioButton r) return;
        St.SidebarStyle = (string)r.Tag; Save(); BuildOrder(); LibraryPage.Current?.ApplySections();
    }

    private void Ocr_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || OcrChoice.SelectedItem is not RadioButton r) return;
        St.OcrMode = (string)r.Tag; Save();
    }

    private void MediaOnly_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        St.OnlyPicturesVideos = MediaOnlySwitch.IsOn; FileQuery.DefaultMediaOnly = MediaOnlySwitch.IsOn; Save();
    }

    private void Light_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        St.PointerLight = LightSwitch.IsOn; Save();
    }

    private void Updates_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        St.CheckUpdates = UpdatesSwitch.IsOn; Save();
    }

    private void ShowUpdate(UpdateInfo info)
    {
        UpdateStatus.Text = $"Version {info.Version} is available.";
        UpdateInstallBtn.Content = Updater.Mode switch { InstallMode.Winget => "Update with winget", _ => "Download and install" };
        UpdateInstallBtn.Visibility = Updater.Mode == InstallMode.Store ? Visibility.Collapsed : Visibility.Visible;
        if (Updater.Mode == InstallMode.Store) UpdateStatus.Text += " The Microsoft Store will install it.";
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckBtn.IsEnabled = false; UpdateStatus.Text = "Checking…";
        try
        {
            var info = await Updater.CheckAsync(true);
            if (info is null) { UpdateStatus.Text = $"File Mind {Updater.CurrentVersion} is the latest version (or GitHub could not be reached)."; UpdateInstallBtn.Visibility = Visibility.Collapsed; }
            else ShowUpdate(info);
        }
        finally { UpdateCheckBtn.IsEnabled = true; }
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Pending is not { } info) return;
        UpdateInstallBtn.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading… {p:P0}");
        try { await Updater.InstallAsync(info, progress); }
        catch (Exception ex) { App.Log("update-install", ex, ex.Message); UpdateStatus.Text = ex.Message; }
        finally { UpdateInstallBtn.IsEnabled = true; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        St.Theme = d.Theme; St.Surface = d.Surface; St.Accent = d.Accent; St.Transparency = d.Transparency;
        St.Layout = d.Layout; St.TileSize = d.TileSize; St.TileGap = d.TileGap; St.ShowNames = d.ShowNames; St.Volume = d.Volume; St.SimilarFiles = d.SimilarFiles;
        Save();
        _ready = false;
        GlassSlider.Value = St.Transparency * 100;
        SizeSlider.Value = St.TileSize; GapSlider.Value = St.TileGap; NamesSwitch.IsOn = St.ShowNames; VolumeSlider.Value = St.Volume * 100;
        SimilarChoice.SelectedIndex = St.SimilarFiles == "side" ? 1 : 0;
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
            : "Running" + (device is null ? "." : $" on {device}.");
        GpuLine.Text = $"This PC: {gpu.Headline}. {gpu.Reason}";

        NvidiaRadio.IsEnabled = gpu.CudaUsable;
        SpeedChoice.SelectedIndex = St.IndexSpeed switch { "quiet" => 0, "fast" => 2, _ => 1 };
        foreach (RadioButton r in Choice.Items)
            if ((string)r.Tag == rt.Settings.Engine) Choice.SelectedItem = r;
        if (Choice.SelectedItem is null) Choice.SelectedIndex = 0;

        var fromSource = rt.SourceLabel is not null;
        Apply.IsEnabled = !fromSource;
        ChoiceNote.Text = fromSource
            ? "This copy runs from a source checkout, so the choice only applies to installed builds."
            : "Switching to NVIDIA downloads GPU support (about 2.8 GB) the first time. AMD and Intel graphics run on the processor. Your library is kept.";

        try
        {
            var roots = await rt.Api!.GetRootsAsync();
            Roots.ItemsSource = roots.Select(r => $"{r.Path}   ·   {r.Files:N0} files").ToList();
        }
        catch (Exception ex) { FileMind.Core.Diag.Log("Settings.folders", ex); }

        About.Text = $"File Mind {Updater.CurrentVersion} (native)\nData: {rt.Paths.Root}\nInstalled as: {Updater.Mode switch { InstallMode.Store => "Microsoft Store (the Store updates it)", InstallMode.Winget => "winget", _ => "unzipped folder" }}";
        UpdatesSwitch.IsOn = rt.Settings.CheckUpdates;
        LightSwitch.IsOn = rt.Settings.PointerLight;
        MediaOnlySwitch.IsOn = rt.Settings.OnlyPicturesVideos;
        OcrChoice.SelectedIndex = rt.Settings.OcrMode switch { "all" => 1, "off" => 2, _ => 0 };
        SidebarStyleChoice.SelectedIndex = rt.Settings.SidebarStyle == "classic" ? 1 : 0;
        UpdateCheckBtn.IsEnabled = Updater.Available;
        if (!Updater.Available) UpdateStatus.Text = "Updates are off while running from a source checkout.";
        else if (Updater.Pending is { } p) ShowUpdate(p);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var rt = AppRuntime.Current;
        rt.Settings.Engine = (Choice.SelectedItem as RadioButton)?.Tag as string ?? "auto";
        rt.Settings.Save(rt.Paths);
        Apply.IsEnabled = false;
        await App.Window!.RestartEngineAsync();
    }

    // ---- Storage: what is kept on disk, how big, and what can be cleared ----
    private async Task BuildStorageAsync()
    {
        var rt = AppRuntime.Current;
        var (data, models, runtime, logs) = rt.StorageFolders();
        var entries = StorageInfo.Describe(data, models, runtime, logs, rt.Paths.SettingsFile, AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        await Task.Run(() => StorageInfo.Measure(entries, data, rt.Paths.SettingsFile));
        StorageList.Children.Clear();
        StorageTotal.Text = $"In total about {StorageInfo.Format(entries.Sum(e => e.Bytes))}.";
        foreach (var e in entries.Where(e => e.Bytes > 0 || e.Clearable))
        {
            var entry = e;
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 9, 8, 9), CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = entry.Name });
            text.Children.Add(new TextBlock { Text = entry.Purpose, Style = (Style)Resources["Helper"], TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = entry.Path, Style = (Style)Resources["Helper"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7 });
            row.Children.Add(text);
            var size = new TextBlock { Text = StorageInfo.Format(entry.Bytes), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(size, 1); row.Children.Add(size);
            var open = new Button { Style = Ui.Style("GlassButton"), Content = "Open", Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center };
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.Path}\"") { UseShellExecute = false }); } catch (Exception ex) { App.Log("storage-open", ex, ex.Message); } };
            Grid.SetColumn(open, 2); row.Children.Add(open);
            if (entry.Clearable && entry.Bytes > 0)
            {
                var clear = new Button { Style = Ui.Style("GlassButton"), Content = "Clear", Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center };
                clear.Click += async (_, _) =>
                {
                    var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = $"Clear {entry.Name.ToLowerInvariant()}?", Content = $"This frees {StorageInfo.Format(entry.Bytes)}. {entry.ClearNote} Your own files are not touched.", PrimaryButtonText = "Clear", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                    if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                    await Task.Run(() => StorageInfo.Clear(entry));
                    await BuildStorageAsync();
                };
                Grid.SetColumn(clear, 3); row.Children.Add(clear);
            }
            StorageList.Children.Add(row);
        }
    }
}
