using System.Diagnostics;
using FileMind.Core;
using FileMind.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace FileMind.QuickSearch;

/// <summary>The Spotlight/Raycast-style launcher: a borderless box that appears over everything on Ctrl+Alt+Space.
/// Type or talk ("the invoice PDF from March", "videos of the beach without people"), arrow to a result, Enter opens it.
/// Created once at startup and only hidden, so it appears instantly.</summary>
public sealed class QuickSearchWindow : Window
{
    private const int Width_ = 760, CollapsedHeight = 76, ExpandedHeight = 520, Limit = 30;

    private readonly TextBox _box;
    private readonly Button _mic;
    private readonly LineIcon _micIcon;
    private readonly TextBlock _status;
    private readonly ListView _list;
    private readonly Grid _footer;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SpeechInput _speech = new();
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";
    private bool _listening, _shown;

    public IntPtr Hwnd { get; }

    public QuickSearchWindow()
    {
        Title = "File Mind quick search";
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, false);
        }
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "filemind.ico"));

        _box = new TextBox
        {
            PlaceholderText = "Search your files, or describe one…",
            FontSize = 20, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 6, 4, 6),
        };
        _box.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _box.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _box.TextChanged += (_, _) => { if (!_listening) { _debounce.Stop(); _debounce.Start(); } };
        _box.PreviewKeyDown += Box_KeyDown;

        _micIcon = new LineIcon { Kind = "mic", Size = 20, Width = 22, Height = 22 };
        _mic = new Button { Content = _micIcon, Padding = new Thickness(8), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_mic, "Talk to search (Alt+V)");
        _mic.Click += async (_, _) => await ToggleListenAsync();

        var top = new Grid { Padding = new Thickness(18, 12, 12, 12), ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glass = new LineIcon { Kind = "search", Size = 20, Width = 22, Height = 22, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(glass);
        Grid.SetColumn(_box, 1); top.Children.Add(_box);
        Grid.SetColumn(_mic, 2); top.Children.Add(_mic);

        _list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Padding = new Thickness(6, 0, 6, 6) };
        _list.ItemClick += (_, e) => Open((e.ClickedItem as FrameworkElement)?.Tag as FileItem, reveal: false);

        _status = new TextBlock { Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var keys = new TextBlock
        {
            Text = "↵ open   Ctrl+↵ show in folder   Ctrl+Shift+C copy path   Alt+V talk   Esc close",
            Opacity = 0.5, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        _footer = new Grid { Padding = new Thickness(18, 8, 18, 10), Visibility = Visibility.Collapsed };
        _footer.ColumnDefinitions.Add(new ColumnDefinition());
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.Children.Add(_status);
        Grid.SetColumn(keys, 1); _footer.Children.Add(keys);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(_list, 1); root.Children.Add(_list);
        Grid.SetRow(_footer, 2); root.Children.Add(_footer);
        root.RequestedTheme = ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Dark ? ElementTheme.Dark : ElementTheme.Light;
        Content = root;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SearchAsync(_box.Text); };
        _speech.Hypothesis += text => DispatcherQueue.TryEnqueue(() => { if (_listening) _box.Text = text; });
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && _shown && !_listening) HideLauncher(); };
        AppWindow.Closing += (_, e) => { e.Cancel = true; HideLauncher(); };
    }

    /// <summary>Shows the launcher on the monitor under the mouse, focused and ready to type; toggles it if open.</summary>
    public void Toggle()
    {
        if (_shown) { HideLauncher(); return; }
        var area = DisplayArea.GetFromPoint(CursorPos(), DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(Hwnd) / 96.0;
        int w = (int)(Width_ * scale), h = (int)((_list.Items.Count > 0 ? ExpandedHeight : CollapsedHeight) * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + area.Height / 5, w, h));
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
        _shown = true;
        _box.Focus(FocusState.Programmatic);
        _box.SelectAll();
    }

    private void HideLauncher()
    {
        if (_listening) _speech.Stop();
        _shown = false;
        AppWindow.Hide();
    }

    private void Resize(bool expanded)
    {
        var scale = GetDpiForWindow(Hwnd) / 96.0;
        var size = AppWindow.Size;
        var h = (int)((expanded ? ExpandedHeight : CollapsedHeight) * scale);
        if (size.Height != h) AppWindow.Resize(new SizeInt32(size.Width, h));
        _footer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        if (text == _lastQuery && _list.Items.Count > 0) return;
        _lastQuery = text;
        _searchCts?.Cancel();
        if (text.Length == 0) { _list.ItemsSource = null; Resize(false); return; }
        var api = AppRuntime.Current.Api;
        if (api is null) { Resize(true); _status.Text = "File Mind is still starting…"; return; }

        var cts = _searchCts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        try
        {
            var files = await api.SearchAsync([text], new FileQuery(), Limit, 0, cts.Token);
            if (cts.IsCancellationRequested) return;
            _list.ItemsSource = files.Select(BuildRow).ToList();
            if (files.Count > 0) _list.SelectedIndex = 0;
            Resize(true);
            _status.Text = files.Count == 0 ? "Nothing matches yet. Try describing it differently."
                                            : $"{files.Count} results · {clock.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Resize(true);
            _status.Text = ex is EngineException ee ? ee.Message : "The search didn't work. Is File Mind still indexing its model?";
            App.Log("quick search", ex, text);
        }
    }

    private async Task ToggleListenAsync()
    {
        if (_listening) { _speech.Stop(); return; }
        _listening = true;
        _micIcon.Opacity = 1;
        _mic.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _box.Text = ""; _box.PlaceholderText = "Listening… describe the file you want";
        try
        {
            var said = (await _speech.ListenAsync()).Trim();
            _listening = false;
            _box.Text = said;
            _box.SelectionStart = said.Length;
            if (said.Length > 0) await SearchAsync(said);
        }
        catch (Exception ex)
        {
            Resize(true);
            _status.Text = "Couldn't use the microphone. Check Windows Settings → Privacy → Microphone.";
            App.Log("quick search voice", ex, ex.Message);
        }
        finally
        {
            _listening = false;
            _mic.ClearValue(Control.BackgroundProperty);
            _box.PlaceholderText = "Search your files, or describe one…";
            _box.Focus(FocusState.Programmatic);
        }
    }

    private async void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (_listening) _speech.Stop(); else HideLauncher();
                break;
            case VirtualKey.Down or VirtualKey.Up when _list.Items.Count > 0:
                e.Handled = true;
                var i = Math.Clamp(_list.SelectedIndex + (e.Key == VirtualKey.Down ? 1 : -1), 0, _list.Items.Count - 1);
                _list.SelectedIndex = i;
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                _debounce.Stop();
                if (_lastQuery != _box.Text.Trim()) await SearchAsync(_box.Text);
                Open(Selected, reveal: ctrl);
                break;
            case VirtualKey.C when ctrl && shift && Selected is { } f:
                e.Handled = true;
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(f.Path);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                _status.Text = "Path copied";
                break;
            case VirtualKey.V when alt:
                e.Handled = true;
                await ToggleListenAsync();
                break;
        }
    }

    private void Open(FileItem? item, bool reveal)
    {
        if (item is null) return;
        try
        {
            if (reveal) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{item.Path}\"", UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            HideLauncher();
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't open {item.Name}: {ex.Message}";
            App.Log("quick search open", ex, item.Path);
        }
    }

    private FileItem? Selected => (_list.SelectedItem as FrameworkElement)?.Tag as FileItem;

    // One row: a small thumbnail (or a kind icon), the name, and the folder it lives in.
    private static Grid BuildRow(FileItem f)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6), Tag = f };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var api = AppRuntime.Current.Api;
        var thumb = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = f.HasThumb && api is not null
                ? new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(api.ThumbUri(f.Id)) { DecodePixelWidth = 88 } }
                : new LineIcon
                {
                    Kind = f.Kind switch { "image" => "photo", "video" => "video", "audio" => "audio", "text" => "text", _ => "file" },
                    Size = 20, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
        };
        row.Children.Add(thumb);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = f.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = Path.GetDirectoryName(f.Path) ?? "", FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(texts, 1); row.Children.Add(texts);
        var meta = new TextBlock { Text = f.Kind is "video" or "audio" ? f.DurationText : f.SizeText, FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(meta, 2); row.Children.Add(meta);
        return row;
    }

    private static bool IsDown(VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static PointInt32 CursorPos() { GetCursorPos(out var p); return new PointInt32(p.X, p.Y); }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
