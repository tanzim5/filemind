using System.Diagnostics;
using System.Text.Json;
using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace FileMind.Pages;

/// <summary>One file at full size: a stage for the picture, clip or sound with a floating control pill, and an Info | Similar panel beside it.</summary>
public sealed partial class ViewerPage : Page
{
    private static double _volume = AppRuntime.Current.Settings.Volume;   // remembered between files, starts at the Settings default
    public static void SetDefaultVolume(double v) => _volume = v;
    private static readonly Color Orange = Color.FromArgb(255, 255, 90, 31);

    private CancellationTokenSource _cts = new();
    private IReadOnlyList<FileItem> _siblings = [];
    private int _index;
    private FileItem _item = new();
    private FileDetails? _info;
    private MediaPlayer? _player;
    private ScrollViewer? _imageScroller;
    private double _zoom = 1;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _settingPosition;
    private List<Rectangle> _bars = [];
    private double _duration;

    private List<FileItem> _similar = [];
    private int _shown;
    private const int Batch = 24;

    private ApiClient Api => AppRuntime.Current.Api!;

    private readonly Button RevealButton, ExportBar, MoreBar;
    private readonly StackPanel _actions;
    private readonly MenuFlyoutItem _copyFrameItem;
    // The volume slider lives in a flyout built here, because XAML names inside a flyout are not reachable from code.
    private readonly Slider VolumeSlider = new() { Minimum = 0, Maximum = 100, Value = 60, Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock VolumeText = new() { Text = "60%", FontSize = 12, Width = 36, VerticalAlignment = VerticalAlignment.Center };

    public ViewerPage()
    {
        InitializeComponent();
        DragHelper.Attach(StageHost, () => _item.Path, null);          // drag the picture, clip or track itself out of the viewer
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (_full) SetFull(false); else GoBack(); e.Handled = true; };
        IsTabStop = true;
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        // The shell's top bar carries the file name and, on the right, Show in Explorer, Export and a menu.
        static Button Bar(string icon, string tip)
        {
            var b = new Button { Style = Ui.Style("BarButton"), Content = new LineIcon { Kind = icon, Size = 16 } };
            ToolTipService.SetToolTip(b, tip); AutomationProperties.SetName(b, tip);
            return b;
        }
        RevealButton = Bar("folder", "Show in Explorer"); RevealButton.Click += Reveal_Click;
        ExportBar = Bar("export", "Export"); ExportBar.Click += Export_Click;
        MoreBar = Bar("more", "More");
        var menu = new MenuFlyout();
        var copyFile = new MenuFlyoutItem { Text = "Copy file" }; copyFile.Click += async (_, _) => await CopyFileAsync();
        var copyPath = new MenuFlyoutItem { Text = "Copy path" }; copyPath.Click += (_, _) => CopyPath();
        _copyFrameItem = new MenuFlyoutItem { Text = "Copy frame" }; _copyFrameItem.Click += CopyFrame_Click;
        menu.Items.Add(copyFile); menu.Items.Add(copyPath); menu.Items.Add(_copyFrameItem);
        menu.Closed += (_, _) => Focus(FocusState.Programmatic);
        MoreBar.Flyout = menu;
        _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _actions.Children.Add(RevealButton); _actions.Children.Add(ExportBar); _actions.Children.Add(MoreBar);

        VolumeSlider.ValueChanged += Volume_Changed;
        var vol = new Flyout { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { VolumeSlider, VolumeText } } };
        vol.Closed += (_, _) => Focus(FocusState.Programmatic);
        VolumeBtn.Flyout = vol;
        PrevButton.OpacityTransition = new ScalarTransition(); NextButton.OpacityTransition = new ScalarTransition();
        SetTab(false);
        SizeChanged += (_, _) => FitStage();

        PreviewKeyDown += Keys_Down;
        Loaded += (_, _) => { PushTitle(); App.Window?.SetActions(_actions); App.Window?.SyncNav(); Focus(FocusState.Programmatic); };
        KeyboardAccelerators.Add(esc);
        _tick.Tick += (_, _) => UpdateProgress();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SetSimilarPlacement(AppRuntime.Current.Settings.SimilarFiles == "side");
        if (e.Parameter is ViewerRequest r)
        {
            _siblings = r.Siblings.Count > 0 ? r.Siblings : [r.Item];
            var i = -1;
            for (var k = 0; k < _siblings.Count; k++) if (_siblings[k].Id == r.Item.Id) { i = k; break; }
            if (i < 0) { _siblings = [r.Item]; i = 0; }
            Show(i);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_full) SetFull(false);
        Teardown();
        _cts.Cancel();
    }

    private void PushTitle() => App.Window?.SetTitle(_item.Name, $"{KindName(_item)} · {_item.SizeText}");

    private void GoBack() { if (Frame.CanGoBack) Frame.GoBack(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { if (_index > 0) Show(_index - 1); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_index < _siblings.Count - 1) Show(_index + 1); }

    // ------------------------------------------------------------------ keyboard, speed, full screen
    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];
    private double _speed = 1;
    private bool _full;

    private static bool Down(Windows.System.VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Space plays or pauses, the arrows skip or step between files, M mutes, F fills the screen.</summary>
    private void Keys_Down(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox) return;
        var ctrl = Down(Windows.System.VirtualKey.Control);
        if (Down(Windows.System.VirtualKey.Menu)) return;                 // Alt+Left/Right stay Back and Forward
        var media = _player is not null;
        var handled = true;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space when media: TogglePlay(); break;
            case Windows.System.VirtualKey.K when media && !ctrl: TogglePlay(); break;
            case Windows.System.VirtualKey.Left when ctrl || !media: Prev_Click(this, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.Right when ctrl || !media: Next_Click(this, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.Left: Skip(-5); break;
            case Windows.System.VirtualKey.Right: Skip(5); break;
            case Windows.System.VirtualKey.J when media && !ctrl: Skip(-10); break;
            case Windows.System.VirtualKey.L when media && !ctrl: Skip(10); break;
            case Windows.System.VirtualKey.PageUp: Prev_Click(this, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.PageDown: Next_Click(this, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.Up when media: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); break;
            case Windows.System.VirtualKey.Down when media: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
            case Windows.System.VirtualKey.M when media && !ctrl: Mute_Tapped(this, new TappedRoutedEventArgs()); break;
            case Windows.System.VirtualKey.F when !ctrl: SetFull(!_full); break;
            case Windows.System.VirtualKey.I when media && !ctrl: MarkIn(); break;
            case Windows.System.VirtualKey.O when media && !ctrl: MarkOut(); break;
            case Windows.System.VirtualKey.E when !ctrl && ExportSpec.CanExport(_item.Kind): _ = ExportAsync(); break;
            case (Windows.System.VirtualKey)188 when !ctrl && _item.Kind == "video": StepFrame(-1); break;   // ,
            case (Windows.System.VirtualKey)190 when !ctrl && _item.Kind == "video": StepFrame(1); break;    // .
            case (Windows.System.VirtualKey)219 when media: StepSpeed(-1); break;   // [
            case (Windows.System.VirtualKey)221 when media: StepSpeed(1); break;    // ]
            case Windows.System.VirtualKey.C when ctrl && Down(Windows.System.VirtualKey.Shift): CopyPath(); break;
            case Windows.System.VirtualKey.C when ctrl: _ = CopyFileAsync(); break;
            case (Windows.System.VirtualKey)187 when _item.Kind is "image" or "video": SetZoom(_zoom * 1.25); break;   // +
            case (Windows.System.VirtualKey)189 when _item.Kind is "image" or "video": SetZoom(_zoom / 1.25); break;   // -
            case Windows.System.VirtualKey.Number0 when !ctrl && _item.Kind is "image" or "video": SetZoom(1); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void TogglePlay()
    {
        try
        {
            if (_player is null) return;
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play();
        }
        catch (Exception ex) { App.Log("viewer-keys", ex, ex.Message); }
    }

    private void Skip(double seconds)
    {
        try
        {
            if (_player is null) return;
            var s = _player.PlaybackSession;
            var d = s.NaturalDuration.TotalSeconds;
            var to = s.Position.TotalSeconds + seconds;
            s.Position = TimeSpan.FromSeconds(d > 0 ? Math.Clamp(to, 0, Math.Max(0, d - 0.2)) : Math.Max(0, to));
        }
        catch (Exception ex) { App.Log("viewer-keys", ex, ex.Message); }
    }

    // ---- Trim marks and export ----
    private double? _in, _out;

    private double Position() { try { return _player?.PlaybackSession.Position.TotalSeconds ?? 0; } catch (Exception) { return 0; } }

    private void MarkIn()
    {
        if (_player is null) return;
        _in = Position();
        if (_out is { } o && o <= _in) _out = null;
        PaintTrim();
    }

    private void MarkOut()
    {
        if (_player is null) return;
        var t = Position();
        if (t < 0.05) return;                      // an end at the very start would be an empty clip
        _out = t;
        if (_in is { } i && i >= t) _in = null;
        PaintTrim();
    }

    private void MarkRange(double from, double to) { _in = from; _out = to; PaintTrim(); }

    private void PaintTrim()
    {
        InText.Text = _in is { } i ? Clock(i) : ""; InText.Visibility = _in is null ? Visibility.Collapsed : Visibility.Visible;
        OutText.Text = _out is { } o ? Clock(o) : ""; OutText.Visibility = _out is null ? Visibility.Collapsed : Visibility.Visible;
        TrimClear.Visibility = _in is null && _out is null ? Visibility.Collapsed : Visibility.Visible;
        PaintRange();
    }

    /// <summary>The marked part, drawn on the scrubber as an accent range (the slider's thumb is inset about 10 px each side).</summary>
    private void PaintRange()
    {
        var d = 0.0;
        try { d = _player?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0; } catch (Exception) { }
        if (d <= 0) d = _duration;
        var w = SeekHost.ActualWidth - 20;
        if (d <= 0 || w <= 0 || (_in is null && _out is null)) { TrimRange.Visibility = Visibility.Collapsed; return; }
        var a = Math.Clamp((_in ?? 0) / d, 0, 1); var b = Math.Clamp((_out ?? d) / d, 0, 1);
        TrimRange.Margin = new Thickness(10 + a * w, 0, 0, 0);
        TrimRange.Width = Math.Max(4, (b - a) * w);
        TrimRange.Visibility = Visibility.Visible;
    }

    private void SeekHost_SizeChanged(object sender, SizeChangedEventArgs e) => PaintRange();

    private void MarkIn_Click(object sender, RoutedEventArgs e) { MarkIn(); Focus(FocusState.Programmatic); }
    private void MarkOut_Click(object sender, RoutedEventArgs e) { MarkOut(); Focus(FocusState.Programmatic); }
    private void TrimClear_Click(object sender, RoutedEventArgs e) { _in = _out = null; PaintTrim(); Focus(FocusState.Programmatic); }
    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync();

    private async Task ExportAsync()
    {
        try
        {
            try { _player?.Pause(); } catch (Exception) { }
            await ExportDialog.ShowAsync(XamlRoot, [_item], _in, _out);
        }
        catch (Exception ex) { App.Log("viewer-export", ex, ex.Message); }
        Focus(FocusState.Programmatic);
    }

    /// <summary>One frame back or forward, paused, for finding the exact picture.</summary>
    private void StepFrame(int by)
    {
        try
        {
            if (_player is null) return;
            _player.Pause();
            var fps = _item.Fps is > 1 and < 240 ? _item.Fps.Value : 30;
            var s = _player.PlaybackSession;
            s.Position = TimeSpan.FromSeconds(Math.Max(0, s.Position.TotalSeconds + by / fps));
        }
        catch (Exception ex) { App.Log("viewer-step", ex, ex.Message); }
    }

    private void StepSpeed(int by)
    {
        var i = Array.IndexOf(Speeds, _speed);
        if (i < 0) i = 2;
        SetSpeed(Speeds[Math.Clamp(i + by, 0, Speeds.Length - 1)]);
    }

    private void SetSpeed(double v)
    {
        _speed = v;
        SpeedText.Text = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
        try { if (_player is not null) _player.PlaybackSession.PlaybackRate = v; } catch (Exception ex) { App.Log("viewer-speed", ex, ex.Message); }
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        var i = Array.IndexOf(Speeds, _speed);
        SetSpeed(Speeds[(Math.Max(i, 0) + 1) % Speeds.Length]);
        Focus(FocusState.Programmatic);
    }

    private void Full_Click(object sender, RoutedEventArgs e) { SetFull(!_full); Focus(FocusState.Programmatic); }

    /// <summary>Fills the screen with the stage: the window goes full screen and the sidebar, bar and panel step aside.</summary>
    private void SetFull(bool on)
    {
        if (_full == on) return;
        _full = on;
        try
        {
            App.Window?.SetImmersive(on);
            PageStack.Padding = on ? new Thickness(0) : new Thickness(20, 60, 20, 40);
            StageCard.CornerRadius = new CornerRadius(on ? 0 : 8); StageCard.BorderThickness = new Thickness(on ? 0 : 1);
            DetailsCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            MoreSection.Visibility = on || _side ? Visibility.Collapsed : Visibility.Visible;
            Scroll.ChangeView(null, 0, null, true);
            FitStage();
        }
        catch (Exception ex) { App.Log("viewer-full", ex, ex.Message); }
    }

    /// <summary>The stage and the panel beside it fill the window under the bar, leaving the heading of More like this in view below.</summary>
    private void FitStage()
    {
        var h = Scroll.ActualHeight;
        if (h < 100) return;
        var v = _full ? h : Math.Clamp(h - 150, 520, 760);
        StageCard.Height = DetailsCard.Height = v;
    }

    private void CopyPath()
    {
        var d = new DataPackage(); d.SetText(_item.Path); Clipboard.SetContent(d);
    }

    private async Task CopyFileAsync()
    {
        try { var d = new DataPackage { RequestedOperation = DataPackageOperation.Copy }; d.SetStorageItems([await StorageFile.GetFileFromPathAsync(_item.Path)]); Clipboard.SetContent(d); }
        catch (Exception ex) { App.Log("viewer-copyfile", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ one file
    private void Show(int index)
    {
        Teardown();
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _index = index;
        _item = _siblings[index];
        _info = null;
        _zoom = 1; ZoomText.Text = "100%"; ZoomTransform.ScaleX = ZoomTransform.ScaleY = 1;
        SimilarScroll.ChangeView(null, 0, null, true);
        Scroll.ChangeView(null, 0, null, true);

        if (IsLoaded) PushTitle();
        PrevZone.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextZone.Visibility = index < _siblings.Count - 1 ? Visibility.Visible : Visibility.Collapsed;

        var visual = _item.Kind is "image" or "video";
        var timed = _item.Kind is "video" or "audio";
        ZoomPill.Visibility = _item.Kind == "image" ? Visibility.Visible : Visibility.Collapsed;
        TransportPill.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        StageHost.Margin = new Thickness(24, 24, 24, visual || timed ? 88 : 24);
        _copyFrameItem.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        _copyFrameItem.Text = _item.Kind == "video" ? "Copy frame" : "Copy image";
        ExportBar.Visibility = ExportSpec.CanExport(_item.Kind) ? Visibility.Visible : Visibility.Collapsed;
        _in = _out = null; PaintTrim();
        _speed = 1; SpeedText.Text = "1×";

        try { BuildStage(); } catch (Exception ex) { App.Log("viewer-stage", ex, ex.Message); ShowStageMessage("This file can't be previewed here."); }
        BuildDetails();
        _ = LoadInfoAsync(_cts.Token);
        _ = LoadSimilarAsync(_cts.Token);
    }

    // The hover arrows: each shows only while the pointer is in the strip it sits in.
    private void Zone_Enter(object sender, PointerRoutedEventArgs e) { if (sender is Grid { Children.Count: > 0 } g) g.Children[0].Opacity = 1; }
    private void Zone_Exit(object sender, PointerRoutedEventArgs e) { if (sender is Grid { Children.Count: > 0 } g) g.Children[0].Opacity = 0; }

    // ---- the Info | Similar tabs ----
    private bool _similarTab;
    private void InfoTab_Click(object sender, RoutedEventArgs e) => SetTab(false);
    private void SimilarTab_Click(object sender, RoutedEventArgs e) => SetTab(true);

    private void SetTab(bool similar)
    {
        _similarTab = similar;
        InfoScroll.Visibility = similar ? Visibility.Collapsed : Visibility.Visible;
        SimilarView.Visibility = SimilarActions.Visibility = similar ? Visibility.Visible : Visibility.Collapsed;
        InfoTabLine.Visibility = similar ? Visibility.Collapsed : Visibility.Visible;
        SimilarTabLine.Visibility = similar ? Visibility.Visible : Visibility.Collapsed;
        if (similar) { InfoTabText.Foreground = Ui.Brush("GlassTextSecondaryBrush"); SimilarTabText.ClearValue(TextBlock.ForegroundProperty); }
        else { SimilarTabText.Foreground = Ui.Brush("GlassTextSecondaryBrush"); InfoTabText.ClearValue(TextBlock.ForegroundProperty); }
    }

    private void Teardown()
    {
        _tick.Stop();
        var old = _player;
        _player = null; _imageScroller = null; _bars = []; ResetTransport();
        try
        {
            // Detach the picture from the player first, then let go of the player a moment later,
            // so no frame is being rendered from a player that is already gone.
            foreach (var child in MediaHost.Children.OfType<MediaPlayerElement>()) child.SetMediaPlayer(null);
            MediaHost.Children.Clear();
            if (old is not null)
            {
                old.Pause();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try { old.Source = null; old.Dispose(); } catch (Exception ex) { App.Log("viewer-teardown", ex, ex.Message); }
                });
            }
        }
        catch (Exception ex) { App.Log("viewer-teardown", ex, ex.Message); }
    }

    private static string KindName(FileItem f) => $"{char.ToUpperInvariant(f.Kind.FirstOrDefault())}{(f.Kind.Length > 1 ? f.Kind[1..] : "")}";

    // ------------------------------------------------------------------ stage
    private void ShowStageMessage(string text)
    {
        MediaHost.Children.Clear();
        MediaHost.Children.Add(new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
    }

    private void BuildStage()
    {
        switch (_item.Kind)
        {
            case "image": BuildImage(); break;
            case "video": BuildVideo(); break;
            case "audio": BuildAudio(); break;
            case "doc" when _item.Ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase): BuildPdf(); break;
            case "text" or "code": BuildText(); break;
            case "doc" when _item.HasThumb:
                MediaHost.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(_item.Id)), Stretch = Stretch.Uniform, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                break;
            default: ShowStageMessage("No preview for this kind of file yet. Use Open to view it in its own app."); break;
        }
    }

    /// <summary>PDF pages drawn by Windows itself, one under another, a few at a time so a long file opens at once.</summary>
    private async void BuildPdf()
    {
        var pages = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 6, 16, 18) };
        var sv = new ScrollViewer { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Stretch };
        MediaHost.Children.Add(sv);
        var ct = _cts.Token;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_item.Path);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            var count = (int)Math.Min(doc.PageCount, 40);
            for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                using var page = doc.GetPage((uint)i);
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var w = 1100.0;
                await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)w, DestinationHeight = (uint)(w * page.Size.Height / Math.Max(page.Size.Width, 1)), BackgroundColor = Colors.White });
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                if (ct.IsCancellationRequested) return;
                pages.Children.Add(new Border
                {
                    MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Colors.White), CornerRadius = new CornerRadius(2),
                    Child = new Image { Source = bmp, Stretch = Stretch.Uniform }
                });
            }
            if (doc.PageCount > count) pages.Children.Add(new TextBlock { Text = $"Showing the first {count} of {doc.PageCount} pages.", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log("viewer-pdf", ex, ex.Message);
            if (!ct.IsCancellationRequested) ShowStageMessage("This PDF can't be shown here. Use Open to view it in its own app.");
        }
    }

    /// <summary>The start of a text, Markdown or code file, selectable, in a quiet page-like panel.</summary>
    private async void BuildText()
    {
        var box = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var page = new Border { MaxWidth = 860, Margin = new Thickness(24, 6, 24, 18), Padding = new Thickness(28, 24, 28, 24), CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush"), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Child = box };
        MediaHost.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Stretch });
        var ct = _cts.Token;
        try
        {
            var text = await Task.Run(() =>
            {
                using var r = new StreamReader(_item.Path, System.Text.Encoding.UTF8, true);
                var buf = new char[60000];
                var n = r.Read(buf, 0, buf.Length);
                var t = new string(buf, 0, n).Replace("\t", "    ");
                return r.Peek() >= 0 ? t + "\n\n…" : t;
            }, ct);
            if (!ct.IsCancellationRequested) box.Text = text.Length == 0 ? "This file is empty." : text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-text", ex, ex.Message); box.Text = "This file can't be read here."; }
    }

    private void BuildImage()
    {
        var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Source = new BitmapImage(Api.FileUri(_item.Id)) };
        var frame = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        frame.Children.Add(img);
        var sv = new ScrollViewer
        {
            ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.5f, MaxZoomFactor = 8f,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Content = frame,
        };
        sv.SizeChanged += (_, a) => { frame.Width = a.NewSize.Width; frame.Height = a.NewSize.Height; };
        sv.ViewChanged += (_, _) => { _zoom = sv.ZoomFactor; ZoomText.Text = $"{Math.Round(_zoom * 100)}%"; };
        sv.DoubleTapped += (_, _) => SetZoom(Math.Abs(_zoom - 1) < 0.01 ? 2 : 1);
        _imageScroller = sv;
        MediaHost.Children.Add(sv);
    }

    private void BuildVideo()
    {
        var player = new MediaPlayer { Volume = _volume, AutoPlay = true, IsLoopingEnabled = true, Source = MediaSource.CreateFromUri(Api.FileUri(_item.Id)) };
        _player = player;
        var element = new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, AutoPlay = true, Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        element.SetMediaPlayer(player);
        // The player paints black wherever the picture does not reach, so the element is sized to the picture itself: no bars.
        double vw = _item.Width ?? 0, vh = _item.Height ?? 0;
        void Fit()
        {
            if (vw <= 0 || vh <= 0 || MediaHost.ActualWidth < 10 || MediaHost.ActualHeight < 10) return;
            var k = Math.Min(MediaHost.ActualWidth / vw, MediaHost.ActualHeight / vh);
            element.Width = Math.Floor(vw * k); element.Height = Math.Floor(vh * k);
        }
        player.PlaybackSession.NaturalVideoSizeChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (s.NaturalVideoWidth > 0 && s.NaturalVideoHeight > 0) { vw = s.NaturalVideoWidth; vh = s.NaturalVideoHeight; Fit(); }
        });
        MediaHost.SizeChanged += (_, _) => Fit();
        Fit();
        player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => App.Log("viewer-video", null, a.ErrorMessage));
        MediaHost.Children.Add(element);
        VolumeSlider.Value = _volume * 100;
        HookPlayer(player, true);
        element.Tapped += (_, _) => TogglePlay();
        _tick.Start();
    }

    /// <summary>The pill's play button follows the player's state.</summary>
    private void HookPlayer(MediaPlayer player, bool playing)
    {
        PlayIcon.Kind = playing ? "pause" : "play";
        player.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_player, player)) return;
            try { PlayIcon.Kind = s.PlaybackState == MediaPlaybackState.Playing ? "pause" : "play"; } catch { }
        });
    }

    private void ResetTransport()
    {
        _settingPosition = true;
        try { SeekSlider.Value = 0; } finally { _settingPosition = false; }
        NowText.Text = TotalText.Text = "0:00";
        _duration = 0; _playedBars = 0;
    }

    private void Play_Click(object sender, RoutedEventArgs e) { TogglePlay(); Focus(FocusState.Programmatic); }

    private void Seek_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_settingPosition || _player is null) return;
        var d = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (d > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(d * e.NewValue / 1000);
    }

    private async void BuildAudio()
    {
        var player = new MediaPlayer { Volume = _volume, AutoPlay = false, Source = MediaSource.CreateFromUri(Api.FileUri(_item.Id)) };
        _player = player;
        VolumeSlider.Value = _volume * 100;
        HookPlayer(player, false);

        const int n = 64;
        var bars = new Grid { Height = 190, Background = new SolidColorBrush(Colors.Transparent) };
        for (var i = 0; i < n; i++) bars.ColumnDefinitions.Add(new ColumnDefinition());
        _bars = [];
        var rnd = new Random(_item.Id);
        for (var i = 0; i < n; i++)
        {
            var r = new Rectangle { RadiusX = 2, RadiusY = 2, Height = 24 + 70 * Math.Abs(Math.Sin(i * 0.4 + _item.Id)) * (0.5 + rnd.NextDouble() * 0.5), Margin = new Thickness(1.6, 0, 1.6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(AudioIdle) };
            Grid.SetColumn(r, i);
            bars.Children.Add(r); _bars.Add(r);
        }
        bars.PointerMoved += (_, e) => PaintBars(Math.Clamp((int)(e.GetCurrentPoint(bars).Position.X / Math.Max(bars.ActualWidth, 1) * n), 0, n - 1));
        bars.PointerExited += (_, _) => PaintBars(-1);
        bars.Tapped += (_, e) =>
        {
            var d = _player?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0;
            if (d <= 0) d = _duration;
            if (d > 0) Seek(d * Math.Clamp(e.GetPosition(bars).X / Math.Max(bars.ActualWidth, 1), 0, 1));
            else _player?.Play();
        };
        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { try { if (_player is not null) _player.PlaybackSession.Position = TimeSpan.Zero; } catch { } });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new TextBlock { Text = "NOW PLAYING", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 100, Foreground = new SolidColorBrush(Orange) });
        head.Children.Add(new TextBlock { Text = $"{_item.Ext.TrimStart('.').ToUpperInvariant()} · {_item.SizeText}", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(head); stack.Children.Add(bars);

        // a warm glow rising from the bottom edge of the card
        var glow = new Rectangle
        {
            VerticalAlignment = VerticalAlignment.Bottom, Height = 220, IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0, 255, 90, 31), Offset = 0 }, new GradientStop { Color = Color.FromArgb(70, 255, 90, 31), Offset = 1 } }
            }
        };
        var card = new Grid
        {
            MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(255, 24, 20, 26), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 46, 28, 36), Offset = 1 } }
            },
        };
        card.Children.Add(glow);
        card.Children.Add(new Border { Padding = new Thickness(30, 28, 30, 30), Child = stack });
        MediaHost.Children.Add(card);
        _tick.Start();

        try
        {
            var wave = await Api.GetWaveAsync(_item.Id, _cts.Token);
            if (wave.Peaks.Count > 0)
            {
                _duration = wave.Duration;
                for (var i = 0; i < _bars.Count; i++)
                    _bars[i].Height = 14 + 150 * wave.Peaks[Math.Min(i * wave.Peaks.Count / _bars.Count, wave.Peaks.Count - 1)];
                if (_duration > 0) { TotalText.Text = Clock(_duration); PaintRange(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-wave", ex, ex.Message); }
    }

    private static readonly Color AudioIdle = Color.FromArgb(170, 214, 150, 150);
    private int _playedBars;
    private void PaintBars(int hover)
    {
        for (var i = 0; i < _bars.Count; i++)
            ((SolidColorBrush)_bars[i].Fill).Color = i < _playedBars ? Orange : (hover >= 0 && i <= hover ? Color.FromArgb(235, 255, 176, 150) : AudioIdle);
    }

    private void UpdateProgress()
    {
        try
        {
            if (_player is null) return;
            var d = _player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (d <= 0) d = _duration;
            var p = _player.PlaybackSession.Position.TotalSeconds;
            _settingPosition = true;
            SeekSlider.Value = d > 0 ? Math.Clamp(p / d * 1000, 0, 1000) : 0;
            _settingPosition = false;
            NowText.Text = Clock(p);
            if (d > 0) { var t = Clock(d); if (TotalText.Text != t) { TotalText.Text = t; PaintRange(); } }
            _playedBars = d > 0 ? (int)(p / d * _bars.Count) : 0;
            PaintBars(-1);
        }
        catch { _settingPosition = false; }
    }

    private static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void Seek(double seconds)
    {
        try { if (_player is not null) { _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds); _player.Play(); } }
        catch (Exception ex) { App.Log("viewer-seek", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ controls: zoom, volume, drag, copy
    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.5, 4);
        if (_imageScroller is not null) _imageScroller.ChangeView(null, null, (float)_zoom);
        else { ZoomTransform.ScaleX = ZoomTransform.ScaleY = _zoom; }
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void ZoomReset_Tapped(object sender, TappedRoutedEventArgs e) => SetZoom(1);

    private double _beforeMute = 60;
    private void Mute_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0) { _beforeMute = VolumeSlider.Value; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = _beforeMute;
    }

    private void Volume_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _volume = e.NewValue / 100;
        if (VolumeText is not null) VolumeText.Text = $"{Math.Round(e.NewValue)}%";
        try { if (_player is not null) _player.Volume = _volume; } catch { }
    }

    private async void CopyFrame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StorageFile file;
            if (_item.Kind == "video")
            {
                var ffmpeg = FindFfmpeg();
                var png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"filemind-frame-{Guid.NewGuid():N}.png");
                var at = _player?.PlaybackSession.Position.TotalSeconds ?? 0;
                if (ffmpeg is null) { await Toast("ffmpeg isn't available, so the frame can't be copied."); return; }
                var psi = new ProcessStartInfo(ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                foreach (var a in new[] { "-v", "error", "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", _item.Path, "-frames:v", "1", "-y", png }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                await proc.WaitForExitAsync(_cts.Token);
                if (!File.Exists(png)) { await Toast("Couldn't read a frame at this moment."); return; }
                file = await StorageFile.GetFileFromPathAsync(png);
            }
            else file = await StorageFile.GetFileFromPathAsync(_item.Path);

            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            data.SetStorageItems([file]);
            Clipboard.SetContent(data);
            try { Clipboard.Flush(); } catch (Exception fx) { App.Log("viewer-copyframe", fx, "flush: " + fx.Message); }   // keeps it on the clipboard after the app moves on
            App.Log("viewer-copyframe", null, $"copied {file.Path}");
            await Toast(_item.Kind == "video" ? "Frame copied" : "Image copied");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-copyframe", ex, ex.Message); await Toast("Couldn't copy that."); }
    }

    private static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        foreach (var p in new[] { @"C:\ffmpeg\bin\ffmpeg.exe", System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    private async Task Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        try { await Task.Delay(1600, _cts.Token); } catch { }
        ToastBox.Visibility = Visibility.Collapsed;
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try { await Api.RevealAsync(_item.Id); } catch (Exception ex) { App.Log("viewer-reveal", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ details panel
    private static TextBlock Small(string text) => new() { Text = text, FontSize = 12, Foreground = Ui.Brush("GlassTextSecondaryBrush") };
    private static TextBlock Value(string text) => new() { Text = text, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Ui.Brush("GlassTextPrimaryBrush") };

    private static UIElement Header(string icon, string text, string? hint = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon.Length > 0) row.Children.Add(new LineIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        row.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        if (hint is not null) row.Children.Add(new TextBlock { Text = "· " + hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        return row;
    }

    private static Border Divider() => new() { Height = 1, Background = Ui.Brush("DividerBrush") };

    private static string Ago(double mtime)
    {
        if (mtime <= 0) return "";
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)mtime);
        return d.TotalHours < 24 && DateTime.Now.Date == DateTimeOffset.FromUnixTimeSeconds((long)mtime).LocalDateTime.Date ? "Today"
            : d.TotalDays < 2 ? "Yesterday"
            : d.TotalDays < 14 ? $"{(int)d.TotalDays} days ago"
            : d.TotalDays < 60 ? $"{(int)(d.TotalDays / 7)} weeks ago"
            : d.TotalDays < 700 ? $"{(int)(d.TotalDays / 30)} months ago" : $"{(int)(d.TotalDays / 365)} years ago";
    }

    // Two lines, no more: what it is, then its numbers.
    private StackPanel DetailGrid(FileDetails? info)
    {
        var one = new List<string> { KindName(_item), _item.Ext.TrimStart('.').ToUpperInvariant(), _item.SizeText };
        var ago = Ago(_item.Mtime); if (ago.Length > 0) one.Add(ago);
        var two = new List<string>();
        var w = _item.Width ?? info?.Dimensions?.ElementAtOrDefault(0); var h = _item.Height ?? info?.Dimensions?.ElementAtOrDefault(1);
        if (w > 0 && h > 0) two.Add($"{w:N0}×{h:N0}");
        if (_item.Duration is > 0) two.Add(Clock(_item.Duration.Value));
        if (_item.Fps is > 0) two.Add($"{_item.Fps:0.#} fps");
        if (!string.IsNullOrEmpty(_item.Codec)) two.Add(_item.Codec!.ToUpperInvariant());
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(new TextBlock { Text = string.Join(" · ", one.Where(s => s.Length > 0)), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("GlassTextPrimaryBrush") });
        if (two.Count > 0) p.Children.Add(new TextBlock { Text = string.Join(" · ", two), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        return p;
    }

    private Button Pill(string icon, string text, RoutedEventHandler click, bool accent = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new LineIcon { Kind = icon, Size = 14 });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var b = new Button { Style = Ui.Style(accent ? "GlassAccentButton" : "GlassButton"), Padding = new Thickness(13, 7, 13, 7), CornerRadius = new CornerRadius(6), Content = row };
        b.Click += click;
        return b;
    }

    private Button IconButton(string icon, string tip, RoutedEventHandler click)
    {
        var b = new Button { Style = Ui.Style("GlassButton"), Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Content = new LineIcon { Kind = icon, Size = 14 } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    // Everything that does not need the engine's answer is drawn at once; the rest fills in when it arrives.
    private void BuildDetails()
    {
        Details.Children.Clear();
        Details.Children.Add(DetailGrid(null));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var reveal = IconButton("library", "Show in Explorer", Reveal_Click);
        reveal.Style = Ui.Style("GlassAccentButton");
        actions.Children.Add(reveal);
        actions.Children.Add(IconButton("fwd", "Open with the default app", async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(_item.Path)); } catch (Exception ex) { App.Log("viewer-open", ex, ex.Message); }
        }));
        actions.Children.Add(IconButton("text", "Copy the file path (Ctrl+Shift+C)", (_, _) => CopyPath()));
        actions.Children.Add(IconButton("file", "Copy the file (Ctrl+C)", async (_, _) => await CopyFileAsync()));
        actions.Children.Add(IconButton("search", "Search with this file", (_, _) =>
        {
            LibraryPage.Request = new LibraryRequest(new FileQuery(), "", null, _item.Id, _item.Name);
            App.Window?.ShowLibrary();
        }));
        if (_item.Kind is "video" or "audio") actions.Children.Add(IconButton("rows", "Find moments", async (_, _) => await ToggleMomentsAsync()));
        Details.Children.Add(actions);
        _momentsBox = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        Details.Children.Add(_momentsBox);

        _slot = new StackPanel { Spacing = 16 };      // the engine-provided sections land here
        Details.Children.Add(_slot);
    }

    private StackPanel? _slot, _momentsBox;

    private async Task LoadInfoAsync(CancellationToken ct)
    {
        try
        {
            var info = await Api.GetInfoAsync(_item.Id, ct);
            if (ct.IsCancellationRequested) return;
            _info = info;
            DrawInfo(info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-info", ex, ex.Message); }
    }

    private void DrawInfo(FileDetails info)
    {
        if (_slot is null) return;
        _slot.Children.Clear();

        // refresh the grid now that the real dimensions are known
        if (Details.Children.Count > 0 && Details.Children[0] is StackPanel) Details.Children[0] = DetailGrid(info);

        var tags = info.Tags.Select(TagText).Where(t => t.Length > 0).Distinct().Take(14).ToList();
        if (tags.Count > 0 && _item.Kind != "audio")
        {
            _slot.Children.Add(Header("photo", "Tags", "What's in it?"));
            var wrap = new WrapPanel { Spacing = 6 };
            foreach (var t in tags)
            {
                var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Style = Ui.Style("GlassChip"), Content = new TextBlock { Text = t, FontSize = 11.5 }, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(5) };
                chip.Click += (_, _) => { LibraryPage.Request = new LibraryRequest(new FileQuery(), "", [(t, t)]); App.Window?.ShowLibrary(); };
                wrap.Children.Add(chip);
            }
            _slot.Children.Add(wrap);
        }

        // Colours sit right under the tags; a tap searches by that colour, like the Library's colour filter.
        if (info.Colors.Count > 0 || info.Swatches.Count > 0)
        {
            _slot.Children.Add(Header("palette", "Colors", "tap to search"));
            if (info.Colors.Count > 0)
            {
                var strip = new Grid { Height = 36, CornerRadius = new CornerRadius(4), ColumnSpacing = 3 };
                var caption = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                var cols = new List<ColumnDefinition>();
                var mostly = string.Join(" · ", info.Swatches.Take(3).Select(sw => $"{Title(sw.Key)} {Math.Round(sw.Share * 100)}%"));
                void Rest() { caption.Text = mostly.Length > 0 ? "Mostly " + mostly : "Tap a colour to find similar"; caption.Foreground = Ui.Brush("GlassTextSecondaryBrush"); foreach (var c in cols) c.Width = new GridLength(1, GridUnitType.Star); }
                for (var i = 0; i < info.Colors.Count; i++)
                {
                    var cd = new ColumnDefinition(); cols.Add(cd); strip.ColumnDefinitions.Add(cd);
                    var hex = info.Colors[i].StartsWith('#') ? info.Colors[i] : "#" + info.Colors[i];
                    var r = new Rectangle { Fill = Ui.Hex(hex), RadiusX = 6, RadiusY = 6 };
                    var idx = i;
                    r.PointerEntered += (_, _) =>
                    {
                        for (var k = 0; k < cols.Count; k++) cols[k].Width = new GridLength(k == idx ? 4 : 1, GridUnitType.Star);
                        var (name, share) = NearestSwatch(info, hex);
                        caption.Text = share > 0 ? $"{Title(name)} · {Math.Round(share * 100)}% · {hex.ToUpperInvariant()}" : hex.ToUpperInvariant();
                        caption.Foreground = Ui.Brush("GlassTextPrimaryBrush");
                    };
                    r.PointerExited += (_, _) => Rest();
                    r.Tapped += (_, _) => { LibraryPage.Request = new LibraryRequest(new FileQuery { Near = hex }, ""); App.Window?.ShowLibrary(); };
                    Grid.SetColumn(r, i); strip.Children.Add(r);
                }
                Rest();
                _slot.Children.Add(strip);
                _slot.Children.Add(caption);
            }
            if (info.Swatches.Count > 0)
            {
                var wrap = new WrapPanel { Spacing = 6 };
                foreach (var s in info.Swatches.Take(6))
                {
                    var key = s.Key;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Ui.Hex(s.Swatch), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(new TextBlock { Text = $"{Title(key)} {Math.Round(s.Share * 100)}%", FontSize = 11.5 });
                    var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Style = Ui.Style("GlassChip"), Content = row, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(5) };
                    chip.Click += (_, _) => { LibraryPage.Request = new LibraryRequest(new FileQuery { Color = key }, ""); App.Window?.ShowLibrary(); };
                    wrap.Children.Add(chip);
                }
                _slot.Children.Add(wrap);
            }
        }

        if (_item.Kind == "audio") _slot.Children.Add(TranscriptSection());

        _slot.Children.Add(Divider());
        var facts = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string k, UIElement v) { var r = facts.RowDefinitions.Count; facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var l = Small(k); l.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(l, r); Grid.SetRow((FrameworkElement)v, r); Grid.SetColumn((FrameworkElement)v, 1); facts.Children.Add(l); facts.Children.Add(v); }
        var meta = info.Meta;
        if (!string.IsNullOrEmpty(meta?.Source) || !string.IsNullOrEmpty(_item.Source)) Fact("Source", Value(Title(meta?.Source ?? _item.Source!)));
        if (!string.IsNullOrEmpty(meta?.Date)) Fact("Made", Value(meta!.Date!));
        if (!string.IsNullOrEmpty(meta?.Author)) Fact("By", Value(meta!.Author!));
        if (!string.IsNullOrEmpty(meta?.Prompt)) Fact("Prompt", Value(meta!.Prompt!));
        Fact("Folder", Value(info.Folder));
        _slot.Children.Add(facts);

        _slot.Children.Add(Divider());
        _slot.Children.Add(Header("spaces", "Space"));
        _spaceRow = new WrapPanel { Spacing = 6 };
        _slot.Children.Add(_spaceRow);
        DrawSpaces(info);

        _slot.Children.Add(Divider());
        _slot.Children.Add(Header("text", "New name", "applied only when you commit"));
        var name = new TextBox { Text = info.Proposed ?? _item.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var go = new Button { Style = Ui.Style("GlassAccentButton"), Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Content = new LineIcon { Kind = "check", Size = 14 } };
        ToolTipService.SetToolTip(go, "Save this as the proposed name");
        async void Save()
        {
            var text = name.Text.Trim();
            if (text.Length == 0 || text == _item.Name) return;
            try { await Api.SetProposedNameAsync(_item.Id, text); ToolTipService.SetToolTip(go, "Saved"); }
            catch (Exception ex) { App.Log("viewer-name", ex, ex.Message); }
        }
        go.Click += (_, _) => Save();
        name.KeyDown += (_, a) => { if (a.Key == Windows.System.VirtualKey.Enter) { Save(); a.Handled = true; } };
        var nameRow = new Grid { ColumnSpacing = 8 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition()); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.Children.Add(name); Grid.SetColumn(go, 1); nameRow.Children.Add(go);
        _slot.Children.Add(nameRow);
    }

    private static (string Name, double Share) NearestSwatch(FileDetails info, string hex)
    {
        var c = Ui.Hex(hex).Color; var best = ("", 0.0); var bd = double.MaxValue;
        foreach (var sw in info.Swatches)
        {
            var o = Ui.Hex(sw.Swatch).Color;
            var d = Math.Pow(c.R - o.R, 2) + Math.Pow(c.G - o.G, 2) + Math.Pow(c.B - o.B, 2);
            if (d < bd) { bd = d; best = (sw.Key, sw.Share); }
        }
        return best;
    }

    private WrapPanel? _spaceRow;

    private void DrawSpaces(FileDetails info)
    {
        if (_spaceRow is null) return;
        _spaceRow.Children.Clear();
        foreach (var p in info.Projects)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = p.Name, FontSize = 11.5 });
            var x = new LineIcon { Kind = "close", Size = 10, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(x);
            var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Style = Ui.Style("GlassChip"), Content = row, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(5) };
            chip.Click += async (_, _) =>
            {
                try { await Api.RemoveFromProjectAsync(p.Id, [_item.Id]); info.Projects.Remove(p); DrawSpaces(info); }
                catch (Exception ex) { App.Log("viewer-space", ex, ex.Message); }
            };
            _spaceRow.Children.Add(chip);
        }
        var add = Pill("plus", "Add", (_, _) => { });
        var flyout = new MenuFlyout();
        flyout.Opening += async (_, _) =>
        {
            flyout.Items.Clear();
            try
            {
                foreach (var p in (await Api.GetProjectsAsync(_cts.Token)).Where(p => !p.Smart && info.Projects.All(x => x.Id != p.Id)))
                {
                    var mi = new MenuFlyoutItem { Text = p.Name };
                    mi.Click += async (_, _) =>
                    {
                        try { await Api.AddToProjectAsync(p.Id, [_item.Id]); info.Projects.Add(new ProjectRef { Id = p.Id, Name = p.Name }); DrawSpaces(info); }
                        catch (Exception ex) { App.Log("viewer-space", ex, ex.Message); }
                    };
                    flyout.Items.Add(mi);
                }
                if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "No other spaces yet", IsEnabled = false });
            }
            catch (Exception ex) { App.Log("viewer-space", ex, ex.Message); }
        };
        add.Flyout = flyout;
        _spaceRow.Children.Add(add);
    }

    private static string TagText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array when e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.String => e[0].GetString() ?? "",
        JsonValueKind.Object when e.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String => l.GetString() ?? "",
        JsonValueKind.Object when e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString() ?? "",
        _ => "",
    };

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ transcript
    private UIElement TranscriptSection()
    {
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Header("text", "Transcript"));
        var body = new StackPanel { Spacing = 8 };
        box.Children.Add(body);
        _ = RefreshTranscriptAsync(body);
        return box;
    }

    private async Task RefreshTranscriptAsync(StackPanel body)
    {
        try
        {
            for (var tries = 0; tries < 400 && !_cts.IsCancellationRequested; tries++)
            {
                var t = await Api.GetTranscriptAsync(_item.Id, _cts.Token);
                body.Children.Clear();
                if (t.Status == "ready" && !string.IsNullOrWhiteSpace(t.Text))
                {
                    body.Children.Add(new TextBlock { Text = t.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxHeight = 220 });
                    body.Children.Add(Pill("file", "Copy text", (_, _) => { var d = new DataPackage(); d.SetText(t.Text!); Clipboard.SetContent(d); }));
                    return;
                }
                if (t.Status == "running")
                {
                    body.Children.Add(new TextBlock { Text = "Transcribing on this device…", FontSize = 12, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
                    await Task.Delay(1500, _cts.Token);
                    continue;
                }
                body.Children.Add(new TextBlock { Text = t.Status == "error" ? (t.Error ?? "Transcription failed.") : "Turn speech in this audio into copyable text. The multilingual model downloads once, then runs locally.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
                body.Children.Add(Pill("text", "Transcribe audio", async (_, _) =>
                {
                    try { await Api.StartTranscriptAsync(_item.Id, _cts.Token); await RefreshTranscriptAsync(body); }
                    catch (Exception ex) { App.Log("viewer-transcript", ex, ex.Message); }
                }));
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-transcript", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ moments
    private async Task ToggleMomentsAsync()
    {
        if (_momentsBox is null) return;
        if (_momentsBox.Visibility == Visibility.Visible) { _momentsBox.Visibility = Visibility.Collapsed; return; }
        _momentsBox.Visibility = Visibility.Visible;
        try
        {
            for (var tries = 0; tries < 600 && !_cts.IsCancellationRequested; tries++)
            {
                var s = await Api.GetMomentsAsync(_item.Id, _cts.Token);
                _momentsBox.Children.Clear();
                if (s.Status == "ready")
                {
                    _momentsBox.Children.Add(new TextBlock { Text = $"{s.Segments.Count} searchable moments. Click one to jump there and mark it; Export then saves just that part.", FontSize = 11.5, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
                    var wrap = new WrapPanel { Spacing = 6 };
                    foreach (var seg in s.Segments)
                    {
                        var b = new Button { Style = Ui.Style("GlassChip"), Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(5), Content = new TextBlock { Text = $"{Clock(seg.Start)}–{Clock(seg.End)}", FontSize = 11.5 } };
                        double at = seg.Start, until = seg.End; b.Click += (_, _) => { Seek(at); MarkRange(at, until); };
                        wrap.Children.Add(b);
                    }
                    _momentsBox.Children.Add(wrap);
                    return;
                }
                if (s.Status == "running")
                {
                    _momentsBox.Children.Add(new TextBlock { Text = $"Analyzing {s.Done} of {(s.Total > 0 ? s.Total.ToString() : "…")} moments on this device…", FontSize = 11.5, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
                    await Task.Delay(1500, _cts.Token);
                    continue;
                }
                _momentsBox.Children.Add(new TextBlock { Text = s.Status == "error" ? (s.Error ?? "Analysis failed.") : "This file hasn't been analyzed yet. Analysis runs only when you ask for it.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
                _momentsBox.Children.Add(Pill("rows", "Analyze moments", async (_, _) =>
                {
                    try { await Api.StartMomentsAsync(_item.Id, _cts.Token); _momentsBox.Visibility = Visibility.Collapsed; await ToggleMomentsAsync(); }
                    catch (Exception ex) { App.Log("viewer-moments", ex, ex.Message); }
                }));
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-moments", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ similar: below the file (default) or in the side panel
    private bool _side;

    /// <summary>Puts the one grid of similar files where Settings says: under the stage, or in the panel's Similar tab.</summary>
    private void SetSimilarPlacement(bool side)
    {
        _side = side;
        if (side)
        {
            MoreBelowHost.Children.Remove(More);
            SimilarScroll.Content = More;
            More.Columns = 2; More.Margin = new Thickness(14, 0, 14, 18);
            MoreSection.Visibility = Visibility.Collapsed;
            TabBar.Visibility = Visibility.Visible;
        }
        else
        {
            SimilarScroll.Content = null;
            if (!MoreBelowHost.Children.Contains(More)) MoreBelowHost.Children.Add(More);
            More.Margin = new Thickness(0);
            MoreSection.Visibility = Visibility.Visible;
            TabBar.Visibility = Visibility.Collapsed;
            FitMoreColumns();
        }
        SetTab(false);
    }

    private void More_SizeChanged(object sender, SizeChangedEventArgs e) { if (!_side) FitMoreColumns(); }

    /// <summary>Equal columns about 230 px wide, so the tiles always fill the row edge to edge.</summary>
    private void FitMoreColumns()
    {
        var w = MoreBelowHost.ActualWidth;
        if (w < 100) return;
        var cols = Math.Max(2, (int)((w + 8) / 238));
        if (More.Columns != cols) { More.Columns = cols; More.InvalidateMeasure(); }
    }

    private async Task LoadSimilarAsync(CancellationToken ct)
    {
        More.Children.Clear();
        _similar = []; _shown = 0;
        MoreNote.Text = SideNote.Text = "";
        try
        {
            var list = await Api.SimilarAsync(_item.Id, new FileQuery(), 240, ct);
            if (ct.IsCancellationRequested) return;
            _similar = list.Where(f => f.Id != _item.Id).ToList();
            MoreNote.Text = SideNote.Text = _similar.Count == 0 ? "Nothing similar has been indexed yet." : "Closest first, by what is in them. Click any to follow it.";
            AddMore();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-similar", ex, ex.Message); MoreNote.Text = SideNote.Text = "Couldn't find similar files just now."; }
    }

    private void Scroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_side && _shown < _similar.Count && Scroll.ScrollableHeight - Scroll.VerticalOffset < 900) AddMore();
    }

    private void Similar_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_side && _shown < _similar.Count && SimilarScroll.ScrollableHeight - SimilarScroll.VerticalOffset < 600) AddMore();
    }

    private void AddMore()
    {
        // whole rows at a time, so the grid never has a ragged end while more is still coming
        var end = Math.Min(_shown + Math.Max(1, More.Columns) * 4, _similar.Count);
        for (var i = _shown; i < end; i++) More.Children.Add(MoreTile(_similar[i]));
        _shown = end;
    }

    private UIElement MoreTile(FileItem f)
    {
        var tile = new Grid { Height = _side ? 108 : 150, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush") };
        DragHelper.Attach(tile, () => f.Path);
        if (f.Kind == "audio")
        {
            tile.Children.Add(new AudioCard { Vm = new TileVm(f, Api) });         // same inline player as the Library
        }
        else if (f.HasThumb)
        {
            tile.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(f.Id)) { DecodePixelWidth = 480 }, Stretch = Stretch.UniformToFill });
            if (f.Kind == "video")
                tile.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new LineIcon { Kind = "play", Size = 13, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        }
        else
        {
            tile.Children.Add(new LineIcon { Kind = "file", Size = 28, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            tile.Children.Add(new TextBlock { Text = f.Name, FontSize = 11, Margin = new Thickness(8, 0, 8, 7), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        }
        tile.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        if (f.Kind != "audio")
        {
            tile.PointerEntered += (_, _) => tile.Opacity = 0.85;
            tile.PointerExited += (_, _) => tile.Opacity = 1;
        }
        // Following a similar file replaces what is open: Back always returns to the Library in one step.
        tile.Tapped += (_, _) =>
        {
            var list = _similar.ToList();
            var at = list.FindIndex(x => x.Id == f.Id);
            if (at < 0) return;
            _siblings = list; Show(at);
        };
        return tile;
    }

    private void OpenAsResults_Click(object sender, RoutedEventArgs e)
    {
        LibraryPage.Request = new LibraryRequest(new FileQuery(), "", null, _item.Id, _item.Name);
        App.Window?.ShowLibrary();
    }

    private async void MakeCollection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ids = new List<int> { _item.Id };
            ids.AddRange(_similar.Take(40).Select(f => f.Id));
            await Api.CreateProjectAsync($"Like {_item.Name}".Length > 60 ? $"Like {_item.Name[..50]}…" : $"Like {_item.Name}", ids);
            MoreNote.Text = "Saved as a new space.";
        }
        catch (Exception ex) { App.Log("viewer-collection", ex, ex.Message); MoreNote.Text = "Couldn't save that space."; }
    }
}

