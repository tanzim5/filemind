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

/// <summary>The Raycast-style launcher that appears over everything on the global shortcut: a query line with a type
/// filter, a compact result list on the left, a large preview with the file's details on the right, and a footer with
/// the actions and their keys. Created once at startup and only hidden, so it appears instantly.</summary>
public sealed class QuickSearchWindow : Window
{
    private const int Width_ = 840, CollapsedHeight = 62, ExpandedHeight = 500, Limit = 40;

    private readonly TextBox _box;
    private readonly Button _mic;
    private readonly LineIcon _micIcon;
    private readonly ComboBox _kind;
    private readonly TextBlock _status;
    private readonly ListView _list;
    private readonly Grid _body, _footer;
    private readonly Border _divider, _preview;
    private readonly StackPanel _info;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SpeechInput _speech = new();
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";
    private bool _listening, _shown;

    public IntPtr Hwnd { get; }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // A keycap like Raycast's: a small filled square with the key on it.
    private static Border Key(string text) => new()
    {
        Background = Res("GlassFillStrongBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), MinWidth = 22,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Res("GlassTextSecondaryBrush") },
    };

    private static TextBlock Label(string text, bool strong = false) => new()
    {
        Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = strong ? null : Res("GlassTextSecondaryBrush"),
    };

    private static Border Bar() => new() { Width = 1, Height = 14, Background = Res("DividerBrush"), Margin = new Thickness(6, 0, 6, 0) };

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

        // ---- top: query, microphone, type filter
        _box = new TextBox
        {
            PlaceholderText = "Search files, or describe one…",
            FontSize = 18, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 6, 2, 6),
        };
        _box.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _box.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _box.TextChanged += (_, _) => { if (!_listening) { _debounce.Stop(); _debounce.Start(); } };
        _box.PreviewKeyDown += Box_KeyDown;

        _kind = new ComboBox { MinWidth = 140, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(6) };
        foreach (var (label, tag) in new[] { ("All files", "all"), ("Images", "image"), ("Videos", "video"), ("Sounds", "audio"), ("Documents", "doc") })
            _kind.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += async (_, _) => { _lastQuery = ""; await SearchAsync(_box.Text); _box.Focus(FocusState.Programmatic); };

        _micIcon = new LineIcon { Kind = "mic", Size = 17 };
        _mic = new Button
        {
            Content = _micIcon, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(_mic, "Talk to search (Alt+V)");
        _mic.Click += async (_, _) => await ToggleListenAsync();

        var top = new Grid { Padding = new Thickness(18, 10, 12, 10), ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new LineIcon { Kind = "search", Size = 18, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_box, 1); top.Children.Add(_box);
        Grid.SetColumn(_mic, 2); top.Children.Add(_mic);
        Grid.SetColumn(_kind, 3); top.Children.Add(_kind);

        _divider = new Border { Height = 1, Background = Res("DividerBrush"), Visibility = Visibility.Collapsed };

        // ---- body: results on the left, preview and details on the right
        _list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Padding = new Thickness(8, 6, 8, 8) };
        _list.ItemClick += (_, e) => Open((e.ClickedItem as FrameworkElement)?.Tag as FileItem, reveal: false);
        _list.SelectionChanged += (_, _) => ShowPreview(Selected);

        _preview = new Border { Height = 220, CornerRadius = new CornerRadius(8), Background = Res("GlassFillBrush"), Margin = new Thickness(0, 0, 0, 16) };
        _info = new StackPanel();
        var details = new StackPanel();
        details.Children.Add(_preview);
        var heading = Label("Information", strong: true);
        heading.Margin = new Thickness(0, 0, 0, 4);
        details.Children.Add(heading);
        details.Children.Add(_info);
        var right = new ScrollViewer { Padding = new Thickness(16, 12, 18, 12), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = details };

        _body = new Grid { Visibility = Visibility.Collapsed };
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _body.Children.Add(_list);
        var split = new Border { Width = 1, Background = Res("DividerBrush") };
        Grid.SetColumn(split, 1); _body.Children.Add(split);
        Grid.SetColumn(right, 2); _body.Children.Add(right);

        // ---- footer: app and status on the left, the actions and their keys on the right
        _status = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("GlassTextSecondaryBrush") };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Width = 16, Height = 16, Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png"))) });
        brand.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Label("Open", strong: true)); actions.Children.Add(Key("↵"));
        actions.Children.Add(Bar());
        actions.Children.Add(Label("Show in folder")); actions.Children.Add(Key("Ctrl")); actions.Children.Add(Key("↵"));
        actions.Children.Add(Bar());
        actions.Children.Add(Label("Copy path")); actions.Children.Add(Key("Ctrl")); actions.Children.Add(Key("Shift")); actions.Children.Add(Key("C"));
        _footer = new Grid { Padding = new Thickness(16, 9, 14, 9), Visibility = Visibility.Collapsed, Background = Res("GlassFillBrush") };
        _footer.ColumnDefinitions.Add(new ColumnDefinition());
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.Children.Add(brand);
        Grid.SetColumn(actions, 1); _footer.Children.Add(actions);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(_divider, 1); root.Children.Add(_divider);
        Grid.SetRow(_body, 2); root.Children.Add(_body);
        Grid.SetRow(_footer, 3); root.Children.Add(_footer);
        root.RequestedTheme = ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Dark ? ElementTheme.Dark : ElementTheme.Light;
        Content = root;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SearchAsync(_box.Text); };
        _speech.Hypothesis += text => DispatcherQueue.TryEnqueue(() => { if (_listening) _box.Text = text; });
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && _shown && !_listening) HideLauncher(); };
        AppWindow.Closing += (_, e) => { e.Cancel = true; HideLauncher(); };
    }

    private string KindFilter => (_kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

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
        _footer.Visibility = _body.Visibility = _divider.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        if (text == _lastQuery && _list.Items.Count > 0) return;
        _lastQuery = text;
        _searchCts?.Cancel();
        if (text.Length == 0) { _list.ItemsSource = null; ShowPreview(null); Resize(false); return; }
        var api = AppRuntime.Current.Api;
        if (api is null) { Resize(true); _status.Text = "File Mind is still starting…"; return; }

        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var files = await api.SearchAsync([text], new FileQuery { Kind = KindFilter }, Limit, 0, cts.Token);
            if (cts.IsCancellationRequested) return;
            _list.ItemsSource = files.Select(BuildRow).ToList();
            if (files.Count > 0) _list.SelectedIndex = 0; else ShowPreview(null);
            Resize(true);
            _status.Text = files.Count == 0 ? "Nothing matches. Try describing it differently." : $"{files.Count} results";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Resize(true);
            _status.Text = ex is EngineException ee ? ee.Message : "The search didn't work. Is File Mind still loading its model?";
            App.Log("quick search", ex, text);
        }
    }

    // The right-hand pane: a large preview and the facts about the file, label left and value right.
    private void ShowPreview(FileItem? f)
    {
        _info.Children.Clear();
        if (f is null) { _preview.Child = null; return; }
        var api = AppRuntime.Current.Api;
        _preview.Child = f.HasThumb && api is not null
            ? new Image { Stretch = Stretch.Uniform, Source = new BitmapImage(api.ThumbUri(f.Id)) { DecodePixelWidth = 900 } }
            : new LineIcon { Kind = KindIcon(f), Size = 56, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        void Row(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var g = new Grid { Padding = new Thickness(0, 7, 0, 7), BorderBrush = Res("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Label(label));
            var v = new TextBlock { Text = value, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(v, value);
            Grid.SetColumn(v, 1); g.Children.Add(v);
            _info.Children.Add(g);
        }
        Row("Name", f.Name);
        Row("Kind", f.Kind switch { "image" => "Image", "video" => "Video", "audio" => "Sound", "doc" => "Document", "text" => "Text", _ => "File" }
                    + (f.Ext.Length > 0 ? $" · {f.Ext.TrimStart('.').ToUpperInvariant()}" : ""));
        Row("Dimensions", f.DimensionsText);
        Row("Length", f.Kind is "video" or "audio" ? f.DurationText : "");
        Row("Frame rate", f.Fps is > 0 ? $"{f.Fps:0.##} fps" : "");
        Row("Size", f.SizeText);
        Row("Modified", f.Modified.ToString("d MMM yyyy, HH:mm"));
        Row("Where", Path.GetDirectoryName(f.Path) ?? "");
    }

    private static string KindIcon(FileItem f) => f.Kind switch { "image" => "photo", "video" => "video", "audio" => "audio", "text" => "text", _ => "file" };

    private async Task ToggleListenAsync()
    {
        if (_listening) { _speech.Stop(); return; }
        _listening = true;
        _mic.Background = Res("AccentFillBrush");
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
            _mic.Background = new SolidColorBrush(Colors.Transparent);
            _box.PlaceholderText = "Search files, or describe one…";
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

    // One compact row, as in Raycast: a small thumbnail and the name; the details live in the preview pane.
    private static Grid BuildRow(FileItem f)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(2, 5, 2, 5), Tag = f };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var api = AppRuntime.Current.Api;
        row.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(4), Background = Res("GlassFillBrush"),
            Child = f.HasThumb && api is not null
                ? new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(api.ThumbUri(f.Id)) { DecodePixelWidth = 56 } }
                : new LineIcon { Kind = KindIcon(f), Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var name = new TextBlock { Text = f.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); row.Children.Add(name);
        var meta = new TextBlock
        {
            Text = f.Kind is "video" or "audio" ? f.DurationText : f.DimensionsText,
            FontSize = 11.5, Foreground = Res("GlassTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
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
