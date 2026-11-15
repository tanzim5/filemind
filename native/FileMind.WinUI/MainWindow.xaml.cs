using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FileMind;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = DesktopAcrylicController.IsSupported() ? new DesktopAcrylicBackdrop() : new MicaBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var bar = AppWindow.TitleBar;
        // The tall bar puts the window buttons on the same line as the page's toolbar, like a unified Mac toolbar.
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonInactiveForegroundColor = Color(0x80, 0xFF, 0xFF, 0xFF);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "filemind.ico"));
        RestorePlacement();
        AddShortcuts();
        ApplyGlass();

        // Back and Forward everywhere: the mouse's side buttons, Alt+Left/Right, and the same in every page.
        Root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            var pp = e.GetCurrentPoint(Root).Properties;
            if (pp.IsXButton1Pressed) { e.Handled = true; _ = GoBackAnyAsync(); }
            else if (pp.IsXButton2Pressed) { e.Handled = true; _ = GoForwardAnyAsync(); }
        }), true);
        Root.SizeChanged += (_, _) => QueueTitleRegions();
        // Follow Windows when it changes between light and dark or gets a new accent colour.
        _ui.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            var st = AppRuntime.Current.Settings;
            if (st.Theme == "system" || st.Accent == "system") ApplyGlass();
        });
        ContentFrame.Navigated += (_, e) =>
        {
            SyncRail(e.SourcePageType);
            SetTitleParts();
            TopGlass.Visibility = e.SourcePageType == typeof(Pages.LibraryPage) ? Visibility.Collapsed : Visibility.Visible;
            // Every page brings its own sidebar content; the viewer has none.
            if (e.SourcePageType == typeof(Pages.ViewerPage)) SetSidebar(null);
        };
        Closed += (_, e) =>
        {
            SavePlacement();
            // Keep running in the notification area so Ctrl+Alt+Space still works; the tray menu quits for real.
            if (!App.Quitting && AppRuntime.Current.Settings.KeepRunning && App.Launcher is not null)
            {
                e.Handled = true;
                AppWindow.Hide();
                return;
            }
            App.Quit();
        };
        Root.Loaded += async (_, _) => await BootAsync();
    }

    private readonly Windows.UI.ViewManagement.UISettings _ui = new();

    // ---- Title bar: the top 48 px of the window drags it, except where a page has put controls ----
    private readonly List<FrameworkElement> _titleParts = [];
    private bool _regionsQueued;

    /// <summary>A page names the controls it has placed in the top strip, so they take clicks instead of dragging the window.</summary>
    public void SetTitleParts(params FrameworkElement[] parts)
    {
        foreach (var e in _titleParts) e.SizeChanged -= TitlePart_SizeChanged;
        _titleParts.Clear(); _titleParts.AddRange(parts);
        foreach (var e in _titleParts) e.SizeChanged += TitlePart_SizeChanged;
        QueueTitleRegions();
    }

    private void TitlePart_SizeChanged(object sender, SizeChangedEventArgs e) => QueueTitleRegions();

    private void QueueTitleRegions()
    {
        if (_regionsQueued) return;
        _regionsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _regionsQueued = false; UpdateTitleRegions(); });
    }

    private void UpdateTitleRegions()
    {
        try
        {
            if (Root.XamlRoot is null) return;
            var scale = Root.XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();
            void Add(FrameworkElement e)
            {
                if (!e.IsLoaded || e.Visibility != Visibility.Visible || e.ActualWidth <= 0) return;
                var b = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
                if (b.Top >= TitleBar.Height || b.Bottom <= 0) return;
                rects.Add(new RectInt32((int)(b.X * scale), (int)(b.Y * scale), (int)Math.Ceiling(b.Width * scale), (int)Math.Ceiling(b.Height * scale)));
            }
            if (Shell.Visibility == Visibility.Visible && RailCard.Visibility == Visibility.Visible) Add(NavStack);
            foreach (var e in _titleParts) Add(e);
            Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [.. rects]);
        }
        catch (Exception ex) { App.Log("title-regions", ex, ex.Message); }
    }

    /// <summary>Applies the user's glass opacity choices to the rail and page panel.</summary>
    /// <summary>Cheap update for the transparency slider: only colours change, so dragging is smooth (no backdrop swap,
    /// no rail rebuild).</summary>
    public void ApplyGlassLight()
    {
        try
        {
            var st = AppRuntime.Current.Settings;
            var p = ThemeEngine.Find(st.Theme);
            if (p.Key == "amoled") return;
            byte a(double v) => (byte)(Math.Clamp(v, 0.05, 1) * 255);
            ThemeEngine.Apply(st, Root);
            RailCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(ThemeEngine.RailOpacity(st)), p.Rail.R, p.Rail.G, p.Rail.B));
            PanelCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(ThemeEngine.PanelOpacity(st)), p.Panel.R, p.Panel.G, p.Panel.B));
        }
        catch (Exception ex) { App.Log("glass", ex, ""); }
    }

    public void ApplyGlass()
    {
        try
        {
            var st = AppRuntime.Current.Settings;
            byte a(double v) => (byte)(Math.Clamp(v, 0.05, 1) * 255);
            var p = ThemeEngine.Find(st.Theme);
            ThemeEngine.Apply(st, Root);
            var bar = AppWindow.TitleBar;
            var fg = p.Dark ? Colors.White : Windows.UI.Color.FromArgb(255, 24, 24, 28);
            bar.ButtonForegroundColor = fg; bar.ButtonHoverForegroundColor = fg; bar.ButtonPressedForegroundColor = fg;
            bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, fg.R, fg.G, fg.B);
            var hov = Windows.UI.Color.FromArgb(p.Dark ? (byte)0x24 : (byte)0x1A, fg.R, fg.G, fg.B);
            bar.ButtonHoverBackgroundColor = hov; bar.ButtonPressedBackgroundColor = hov;
            var amoled = p.Key == "amoled";
            SystemBackdrop = amoled ? null : (DesktopAcrylicController.IsSupported() ? new DesktopAcrylicBackdrop() : new MicaBackdrop());
            Root.Background = new SolidColorBrush(amoled ? Colors.Black : Colors.Transparent);
            if (amoled) { RailCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 8, 8, 8)); PanelCard.Background = new SolidColorBrush(Colors.Black); BuildRail(); return; }
            RailCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(ThemeEngine.RailOpacity(st)), p.Rail.R, p.Rail.G, p.Rail.B));
            PanelCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(ThemeEngine.PanelOpacity(st)), p.Panel.R, p.Panel.G, p.Panel.B));
            BuildRail();
        }
        catch (Exception ex) { App.Log("glass", ex, ""); }
    }

    // Opens where it was closed, as long as that spot is still on a connected display.
    private void RestorePlacement()
    {
        var st = AppRuntime.Current.Settings;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1400, area.Width), h = Math.Min(900, area.Height);
        int x = area.X + (area.Width - w) / 2, y = area.Y + (area.Height - h) / 2;
        if (st.WinX != int.MinValue && st.WinW >= 800 && st.WinH >= 560)
        {
            var rect = new RectInt32(st.WinX, st.WinY, st.WinW, st.WinH);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null) (x, y, w, h) = (rect.X, rect.Y, rect.Width, rect.Height);
        }
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        if (st.WinMax && AppWindow.Presenter is OverlappedPresenter pr) pr.Maximize();
    }

    private void SavePlacement()
    {
        try
        {
            var st = AppRuntime.Current.Settings;
            var max = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            st.WinMax = max;
            if (!max && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                st.WinX = AppWindow.Position.X; st.WinY = AppWindow.Position.Y;
                st.WinW = AppWindow.Size.Width; st.WinH = AppWindow.Size.Height;
            }
            st.Save(AppRuntime.Current.Paths);
        }
        catch (Exception ex) { App.Log("placement", ex, null); }
    }

    // The shortcuts a Windows app is expected to have: Ctrl+1..4 for the destinations, Ctrl+, for settings.
    private void AddShortcuts()
    {
        void Add(Windows.System.VirtualKey key, string tag)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            a.Invoked += (_, e) => { if (Shell.Visibility == Visibility.Visible) Select(tag); e.Handled = true; };
            Root.KeyboardAccelerators.Add(a);
        }
        Add(Windows.System.VirtualKey.Number1, "library");
        Add(Windows.System.VirtualKey.Number2, "discover");
        Add(Windows.System.VirtualKey.Number3, "spaces");
        Add(Windows.System.VirtualKey.Number4, "duplicates");
        Add(Windows.System.VirtualKey.K, "quick");
        Add((Windows.System.VirtualKey)188, "settings"); // Ctrl+,
        void Nav(Windows.System.VirtualKey key, Func<Task> go)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
            a.Invoked += async (_, e) => { e.Handled = true; await go(); };
            Root.KeyboardAccelerators.Add(a);
        }
        Nav(Windows.System.VirtualKey.Left, GoBackAnyAsync);
        Nav(Windows.System.VirtualKey.Right, GoForwardAnyAsync);
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (SettingsScrim.Visibility == Visibility.Visible) { CloseSettings(); e.Handled = true; } };
        Root.KeyboardAccelerators.Add(esc);
    }

    /// <summary>Back from wherever you are: close Settings, step back inside the Library, or return to the previous page.</summary>
    public async Task GoBackAnyAsync()
    {
        if (SettingsScrim.Visibility == Visibility.Visible) { CloseSettings(); return; }
        if (ContentFrame.Content is Pages.LibraryPage lp && lp.CanStepBack) { await lp.StepBackAsync(); return; }
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    public async Task GoForwardAnyAsync()
    {
        if (SettingsScrim.Visibility == Visibility.Visible) return;
        if (ContentFrame.Content is Pages.LibraryPage lp && lp.CanStepForward) { await lp.StepForwardAsync(); return; }
        if (ContentFrame.CanGoForward) ContentFrame.GoForward();
    }

    private void SyncRail(Type page)
    {
        var tag = page == typeof(Pages.LibraryPage) ? "library" : page == typeof(Pages.DiscoverPage) ? "discover" : page == typeof(Pages.SpacesPage) ? "spaces" : page == typeof(Pages.DuplicatesPage) ? "duplicates" : null;
        if (tag is null || tag == _current) return;
        _current = tag;
        foreach (var b in RailButtons) b.IsChecked = (string)b.Tag == tag;
    }

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    private bool _booting;
    private int _restarts;
    private DateTime _lastRestart = DateTime.MinValue;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(4) };

    private async Task BootAsync()
    {
        if (_booting) return;
        _booting = true;
        try
        {
            SetupFrame.Visibility = Visibility.Visible;
            SetupFrame.Navigate(typeof(Pages.SetupPage));
            await ((Pages.SetupPage)SetupFrame.Content).RunAsync();
            SetupFrame.Visibility = Visibility.Collapsed;
            SetupFrame.Content = null;
            Shell.Visibility = Visibility.Visible;
            QueueTitleRegions();
            Select("library");
            if (!_watch.IsEnabled) { _watch.Tick += async (_, _) => await WatchEngineAsync(); _watch.Start(); }
        }
        catch (Exception ex) { App.Log("boot", ex, ex.Message); }
        finally { _booting = false; }
    }

    /// <summary>If the engine process ever dies underneath the app, bring it back (up to three times a minute).</summary>
    private async Task WatchEngineAsync()
    {
        try
        {
            if (_booting || Shell.Visibility != Visibility.Visible) return;
            if (AppRuntime.Current.Host.IsRunning) { if (DateTime.UtcNow - _lastRestart > TimeSpan.FromMinutes(1)) _restarts = 0; return; }
            if (_restarts >= 3) return;
            _restarts++; _lastRestart = DateTime.UtcNow;
            App.Log("watchdog", null, $"engine is not running, restarting ({_restarts}/3)");
            await RestartEngineAsync();
        }
        catch (Exception ex) { App.Log("watchdog", ex, ex.Message); }
    }

    /// <summary>Back through the setup flow, for example after the engine edition was changed.</summary>
    public async Task RestartEngineAsync()
    {
        AppRuntime.Current.Host.Stop();
        ContentFrame.CacheSize = 0; ContentFrame.Content = null; ContentFrame.CacheSize = 10;   // drop cached pages that hold the old engine's data
        await BootAsync();
    }

    // ---- Exports: a corner card that follows the engine's export queue until it is empty ----
    private readonly DispatcherTimer _exportTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _exportHooked, _exportBusy;
    private string? _exportLast;

    /// <summary>Called after exports are queued: shows the card and keeps it up to date until they finish.</summary>
    public void WatchExports()
    {
        if (!_exportHooked) { _exportHooked = true; _exportTimer.Tick += async (_, _) => await PollExportsAsync(); }
        ExportTitle.Text = "Exporting"; ExportNote.Text = "";
        ExportBar.Visibility = Visibility.Visible; ExportBar.IsIndeterminate = true;
        ExportCancel.Visibility = Visibility.Visible; ExportShow.Visibility = ExportClose.Visibility = Visibility.Collapsed;
        ExportTray.Visibility = Visibility.Visible;
        _exportTimer.Start();
        _ = PollExportsAsync();
    }

    private async Task PollExportsAsync()
    {
        if (_exportBusy || AppRuntime.Current.Api is not { } api) return;
        _exportBusy = true;
        try
        {
            var jobs = await api.GetExportJobsAsync();
            var active = jobs.Where(j => j.Active).ToList();
            if (active.Count > 0)
            {
                var now = active.FirstOrDefault(j => j.Status != "queued") ?? active[0];
                ExportTitle.Text = jobs.Count == 1 ? "Exporting" : $"Exporting {jobs.Count - active.Count + 1} of {jobs.Count}";
                ExportNote.Text = now.Status == "cancelling" ? "Stopping…" : now.Name;
                ExportBar.IsIndeterminate = now.Progress <= 0;
                ExportBar.Value = now.Progress;
                return;
            }
            _exportTimer.Stop();
            if (jobs.Count == 0) { ExportTray.Visibility = Visibility.Collapsed; return; }
            var done = jobs.Where(j => j.Status == "done").ToList();
            var failed = jobs.Where(j => j.Status == "error").ToList();
            ExportTitle.Text = failed.Count > 0 ? (done.Count > 0 ? $"Exported {done.Count:N0}, {failed.Count:N0} failed" : "Export failed")
                : done.Count == 1 ? "Exported" : done.Count > 1 ? $"Exported {done.Count:N0} files" : "Export cancelled";
            ExportNote.Text = failed.Count > 0 ? $"{failed[0].Name}: {failed[0].Error}" : done.Count == 1 ? done[0].Name
                : done.Count > 1 ? Path.GetDirectoryName(done[^1].Output) ?? "" : "Nothing was saved.";
            _exportLast = done.Count > 0 ? done[^1].Output : null;
            ExportBar.Visibility = ExportCancel.Visibility = Visibility.Collapsed;
            ExportShow.Visibility = _exportLast is null ? Visibility.Collapsed : Visibility.Visible;
            ExportClose.Visibility = Visibility.Visible;
            await api.ClearExportsAsync();
        }
        catch (Exception ex) { App.Log("export-poll", ex, ex.Message); }
        finally { _exportBusy = false; }
    }

    private async void ExportCancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AppRuntime.Current.Api is not { } api) return;
            foreach (var job in (await api.GetExportJobsAsync()).Where(j => j.Active)) await api.CancelExportAsync(job.Id);
        }
        catch (Exception ex) { App.Log("export-cancel", ex, ex.Message); }
    }

    private void ExportShow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_exportLast is not null && File.Exists(_exportLast))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_exportLast}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log("export-show", ex, ex.Message); }
        ExportTray.Visibility = Visibility.Collapsed;
    }

    private void ExportClose_Click(object sender, RoutedEventArgs e) => ExportTray.Visibility = Visibility.Collapsed;

    /// <summary>Pages put their own sidebar content (the Library's filters) into the rail through this.</summary>
    public void SetSidebar(UIElement? content)
    {
        if (ReferenceEquals(SidebarHost.Content, content)) return;
        SidebarHost.Content = content;
        if (content is null) return;
        // The new content eases in, so changing page reads as one surface changing rather than a jump.
        try
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160) };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, SidebarHost);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade); sb.Begin();
        }
        catch (Exception) { SidebarHost.Opacity = 1; }
    }

    public void ClearSidebar(UIElement mine) { if (ReferenceEquals(SidebarHost.Content, mine)) SidebarHost.Content = null; }

    /// <summary>Full screen for the viewer: the window fills the display and the title bar and sidebar step aside.</summary>
    public void SetImmersive(bool on)
    {
        try
        {
            AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            RailColumn.Width = new GridLength(on ? 0 : 276);
            RailCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            TopGlass.Visibility = on || ContentFrame.Content is Pages.LibraryPage ? Visibility.Collapsed : Visibility.Visible;
            QueueTitleRegions();
        }
        catch (Exception ex) { App.Log("immersive", ex, ex.Message); }
    }

    /// <summary>Other pages hand the Library a request (set on LibraryPage.Request) and bring it to the front.</summary>
    public void ShowLibrary() => Select("library");
    public void ShowPage(string tag) => Select(tag);

    private void Rail_Click(object sender, RoutedEventArgs e) => Select((string)((FrameworkElement)sender).Tag);

    private void Select(string tag)
    {
        if (tag is not ("settings" or "quick")) { _current = tag; foreach (var b in RailButtons) b.IsChecked = (string)b.Tag == tag; }
        switch (tag)
        {
            case "library": ShowLibraryPage(); break;
            case "discover": ContentFrame.Navigate(typeof(Pages.DiscoverPage)); break;
            case "spaces": ContentFrame.Navigate(typeof(Pages.SpacesPage)); break;
            case "duplicates": ContentFrame.Navigate(typeof(Pages.DuplicatesPage)); break;
            case "settings": OpenSettings(); break;
            case "quick": App.Launcher?.Toggle(); break;
        }
    }

    private readonly List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> _rail = [];
    private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton[] RailButtons => [.. _rail];
    private string _current = "library";

    private static readonly (string Key, string Label)[] Destinations =
        [("library", "Library"), ("discover", "Discover"), ("spaces", "Spaces"), ("duplicates", "Duplicates")];

    private readonly Dictionary<string, TextBlock> _counts = [];

    /// <summary>Builds the four destinations: an accent-coloured icon, the name, and a quiet count on the right.</summary>
    public void BuildRail()
    {
        NavStack.Children.Clear(); _rail.Clear(); _counts.Clear();
        foreach (var (key, label) in Destinations)
        {
            // Finder-style rows: a solid glyph in the accent colour, the name, a dim count. The selected row gets a
            // soft fill; nothing else competes for attention.
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Pages.LineIcon { Kind = "f-" + key, Size = 17, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
            var name = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var count = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["GlassTextTertiaryBrush"] };
            Grid.SetColumn(count, 2); row.Children.Add(count);
            _counts[key] = count;
            var b = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Style = (Style)Application.Current.Resources["RailItem"], Tag = key, Content = row, IsChecked = key == _current, MinHeight = 32, CornerRadius = new CornerRadius(6) };
            b.Click += Rail_Click;
            _rail.Add(b); NavStack.Children.Add(b);
        }
        _ = FillCountsAsync();
    }

    /// <summary>Shows a count next to a destination ("" hides it). Pages call this when they learn a better number.</summary>
    public void SetCount(string key, string text) { if (_counts.TryGetValue(key, out var t)) t.Text = text; }

    private async Task FillCountsAsync()
    {
        try
        {
            var api = AppRuntime.Current.Api;
            if (api is null) return;
            var stats = await api.GetStatsAsync();
            SetCount("library", stats.Total > 0 ? stats.Total.ToString("N0") : "");
            var spaces = await api.GetProjectsAsync();
            SetCount("spaces", spaces.Count > 0 ? spaces.Count.ToString("N0") : "");
        }
        catch (Exception) { }
    }

    private void Quick_Click(object sender, RoutedEventArgs e) => App.Launcher?.Toggle();

    /// <summary>The sidebar is the Library's filter panel; using it from another page brings the Library forward.</summary>
    public void EnsureLibrary() { if (_current != "library" || ContentFrame.Content is not Pages.LibraryPage) Select("library"); }

    /// <summary>Shows the Library once: from a viewer or another page it steps back to the Library already in history instead of stacking another.</summary>
    private void ShowLibraryPage()
    {
        for (var i = 0; i < 6 && ContentFrame.Content is not Pages.LibraryPage && ContentFrame.CanGoBack
                         && ContentFrame.BackStack.Any(e => e.SourcePageType == typeof(Pages.LibraryPage)); i++)
            ContentFrame.GoBack();
        if (ContentFrame.Content is Pages.LibraryPage lp) _ = lp.ShowAgainAsync();
        else ContentFrame.Navigate(Environment.GetEnvironmentVariable("FILEMIND_DEV_PAGE") switch
        {
            "discover" => typeof(Pages.DiscoverPage), "spaces" => typeof(Pages.SpacesPage), "duplicates" => typeof(Pages.DuplicatesPage), _ => typeof(Pages.LibraryPage),
        });
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsPanel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.Handled = true;
    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        SettingsFrame.Navigate(typeof(Pages.SettingsPage));
        SettingsScrim.Visibility = Visibility.Visible;
        SettingsRail.IsChecked = true;
    }

    private void CloseSettings()
    {
        SettingsRail.IsChecked = false;
        SettingsScrim.Visibility = Visibility.Collapsed;
        SettingsFrame.Content = null;
    }
}
