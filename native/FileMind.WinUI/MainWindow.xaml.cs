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
        ContentFrame.Navigated += (_, e) => SyncRail(e.SourcePageType);
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

    /// <summary>Applies the user's glass opacity choices to the rail and page panel.</summary>
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
            RailCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(st.RailGlass), p.Rail.R, p.Rail.G, p.Rail.B));
            PanelCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a(st.PanelGlass), p.Panel.R, p.Panel.G, p.Panel.B));
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
        var tag = page == typeof(Pages.LibraryPage) ? "library" : page == typeof(Pages.DiscoverPage) ? "discover" : page == typeof(Pages.SpacesPage) ? "spaces" : null;
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

    /// <summary>Pages put their own sidebar content (the Library's filters) into the rail through this.</summary>
    public void SetSidebar(UIElement? content) => SidebarHost.Content = content;

    public void ClearSidebar(UIElement mine) { if (ReferenceEquals(SidebarHost.Content, mine)) SidebarHost.Content = null; }

    /// <summary>Other pages hand the Library a request (set on LibraryPage.Request) and bring it to the front.</summary>
    public void ShowLibrary() => Select("library");
    public void ShowPage(string tag) => Select(tag);

    private void Rail_Click(object sender, RoutedEventArgs e) => Select((string)((FrameworkElement)sender).Tag);

    private void Select(string tag)
    {
        if (tag != "settings") { _current = tag; foreach (var b in RailButtons) b.IsChecked = (string)b.Tag == tag; }
        switch (tag)
        {
            case "library": ShowLibraryPage(); break;
            case "discover": ContentFrame.Navigate(typeof(Pages.DiscoverPage)); break;
            case "spaces": ContentFrame.Navigate(typeof(Pages.SpacesPage)); break;
            case "duplicates": ContentFrame.Navigate(typeof(Pages.SoonPage), ("Duplicates", "Review copies and near-copies. Nothing is deleted for you.")); break;
            case "settings": OpenSettings(); break;
        }
    }

    private readonly List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> _rail = [];
    private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton[] RailButtons => [.. _rail];
    private string _current = "library";

    private static readonly Dictionary<string, (string Label, string Icon)> Destinations = new()
    {
        ["library"] = ("Library", "library"), ["discover"] = ("Discover", "discover"),
        ["spaces"] = ("Spaces", "spaces"), ["duplicates"] = ("Duplicates", "duplicates"),
    };

    /// <summary>Builds the sidebar destinations from the user's order and visibility choices.</summary>
    public void BuildRail()
    {
        var st = AppRuntime.Current.Settings;
        NavStack.Children.Clear(); _rail.Clear();
        foreach (var key in st.MenuOrder.Where(Destinations.ContainsKey).Distinct().Concat(Destinations.Keys.Except(st.MenuOrder)))
        {
            if (st.MenuHidden.Contains(key) && key != _current) continue;
            var (label, icon) = Destinations[key];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Pages.LineIcon { Kind = icon, Size = 18, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var b = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Style = (Style)Application.Current.Resources["RailItem"], Tag = key, Content = row, IsChecked = key == _current };
            b.Click += Rail_Click;
            _rail.Add(b); NavStack.Children.Add(b);
        }
    }

    /// <summary>The sidebar is the Library's filter panel; using it from another page brings the Library forward.</summary>
    public void EnsureLibrary() { if (_current != "library" || ContentFrame.Content is not Pages.LibraryPage) Select("library"); }

    /// <summary>Shows the Library once: from a viewer or another page it steps back to the Library already in history instead of stacking another.</summary>
    private void ShowLibraryPage()
    {
        for (var i = 0; i < 6 && ContentFrame.Content is not Pages.LibraryPage && ContentFrame.CanGoBack
                         && ContentFrame.BackStack.Any(e => e.SourcePageType == typeof(Pages.LibraryPage)); i++)
            ContentFrame.GoBack();
        if (ContentFrame.Content is Pages.LibraryPage lp) _ = lp.ShowAgainAsync();
        else ContentFrame.Navigate(typeof(Pages.LibraryPage));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsPanel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.Handled = true;
    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        SettingsFrame.Navigate(typeof(Pages.SettingsPage));
        SettingsScrim.Visibility = Visibility.Visible;
    }

    private void CloseSettings()
    {
        SettingsScrim.Visibility = Visibility.Collapsed;
        SettingsFrame.Content = null;
    }
}
