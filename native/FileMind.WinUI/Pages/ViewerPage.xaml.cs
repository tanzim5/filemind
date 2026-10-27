using System.Diagnostics;
using System.Text.Json;
using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
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

/// <summary>One file at full size: a stage for the picture, clip or sound, the details beside it, and more like it below.</summary>
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
    private Slider? _seek;
    private TextBlock? _now, _total;
    private List<Rectangle> _bars = [];
    private double _duration;

    private List<FileItem> _similar = [];
    private int _shown;
    private const int Batch = 24;

    private ApiClient Api => AppRuntime.Current.Api!;

    public ViewerPage()
    {
        InitializeComponent();
        DragHelper.Attach(StageHost, () => _item.Path, null);          // drag the picture, clip or track itself out of the viewer
        DragHelper.Attach(TitleBox, () => _item.Path, () => _item.Path);
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { GoBack(); e.Handled = true; };
        KeyboardAccelerators.Add(esc);
        _tick.Tick += (_, _) => UpdateProgress();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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
        Teardown();
        _cts.Cancel();
    }

    private void GoBack() { if (Frame.CanGoBack) Frame.GoBack(); }
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Prev_Click(object sender, RoutedEventArgs e) { if (_index > 0) Show(_index - 1); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_index < _siblings.Count - 1) Show(_index + 1); }

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
        Scroll.ChangeView(null, 0, null, true);

        TitleBox.Text = _item.Name;
        SubtitleBox.Text = $"{KindName(_item)} · {_item.SizeText}";
        PrevButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = index < _siblings.Count - 1 ? Visibility.Visible : Visibility.Collapsed;

        var visual = _item.Kind is "image" or "video";
        CopyFrameButton.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        CopyFrameText.Text = _item.Kind == "video" ? "Copy frame" : "Copy image";
        ZoomBox.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        VolumeBox.Visibility = _item.Kind is "video" or "audio" ? Visibility.Visible : Visibility.Collapsed;

        try { BuildStage(); } catch (Exception ex) { App.Log("viewer-stage", ex, ex.Message); ShowStageMessage("This file can't be previewed here."); }
        BuildDetails();
        _ = LoadInfoAsync(_cts.Token);
        _ = LoadSimilarAsync(_cts.Token);
    }

    private void Teardown()
    {
        _tick.Stop();
        var old = _player;
        _player = null; _imageScroller = null; _seek = null; _now = _total = null; _bars = [];
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

        // A minimal, quiet transport: one thin bar that fades in under the pointer.
        var play = new Button { Style = Ui.Style("GlassButton"), Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(5), Content = new LineIcon { Kind = "pause", Size = 13 } };
        _now = new TextBlock { Text = "0:00", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") };
        _total = new TextBlock { Text = "0:00", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") };
        _seek = new Slider { Minimum = 0, Maximum = 1000, Value = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Orange), Height = 28 };
        _seek.ValueChanged += (_, a) =>
        {
            if (_settingPosition || _player is null) return;
            var d = _player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (d > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(d * a.NewValue / 1000);
        };
        void Toggle() { if (_player is null) return; if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play(); }
        play.Click += (_, _) => Toggle();
        element.Tapped += (_, _) => Toggle();
        player.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            try { play.Content = new LineIcon { Kind = s.PlaybackState == MediaPlaybackState.Playing ? "pause" : "play", Size = 13 }; } catch { }
        });

        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 18, 12, 8) };
        row.Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1), GradientStops = { new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 }, new GradientStop { Color = Color.FromArgb(200, 0, 0, 0), Offset = 1 } } };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(play);
        Grid.SetColumn(_now, 1); row.Children.Add(_now);
        Grid.SetColumn(_seek, 2); row.Children.Add(_seek);
        Grid.SetColumn(_total, 3); row.Children.Add(_total);
        row.VerticalAlignment = VerticalAlignment.Bottom;
        row.Opacity = 0;
        MediaHost.PointerEntered += (_, _) => row.Opacity = 1;
        MediaHost.PointerExited += (_, _) => row.Opacity = 0;
        row.OpacityTransition = new ScalarTransition();
        MediaHost.Children.Add(row);
        _tick.Start();
    }

    private async void BuildAudio()
    {
        var player = new MediaPlayer { Volume = _volume, AutoPlay = false, Source = MediaSource.CreateFromUri(Api.FileUri(_item.Id)) };
        _player = player;
        VolumeSlider.Value = _volume * 100;

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

        var play = new Button { Style = Ui.Style("GlassAccentButton"), Width = 52, Height = 52, Padding = new Thickness(0), CornerRadius = new CornerRadius(26), Content = new LineIcon { Kind = "play", Size = 18 } };
        _now = new TextBlock { Text = "0:00", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _total = new TextBlock { Text = "0:00", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") };
        play.Click += (_, _) =>
        {
            if (_player is null) return;
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play();
        };
        player.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            try { play.Content = new LineIcon { Kind = s.PlaybackState == MediaPlaybackState.Playing ? "pause" : "play", Size = 18 }; } catch { }
        });
        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { try { if (_player is not null) _player.PlaybackSession.Position = TimeSpan.Zero; } catch { } });

        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 18, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(play);
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        times.Children.Add(_now); times.Children.Add(new TextBlock { Text = "/", FontSize = 13, Foreground = Ui.Brush("GlassTextTertiaryBrush") }); times.Children.Add(_total);
        Grid.SetColumn(times, 1); row.Children.Add(times);
        var nameText = new TextBlock { Text = _item.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") };
        Grid.SetColumn(nameText, 2); row.Children.Add(nameText);

        var head = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new TextBlock { Text = "NOW PLAYING", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 100, Foreground = new SolidColorBrush(Orange) });
        head.Children.Add(new TextBlock { Text = $"{_item.Ext.TrimStart('.').ToUpperInvariant()} · {_item.SizeText}", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Ui.Brush("GlassTextTertiaryBrush") });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(head); stack.Children.Add(bars); stack.Children.Add(row);

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
            MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 24, 0),
            CornerRadius = new CornerRadius(8), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(255, 24, 20, 26), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 46, 28, 36), Offset = 1 } }
            },
        };
        card.Children.Add(glow);
        card.Children.Add(new Border { Padding = new Thickness(30, 24, 30, 26), Child = stack });
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
                if (_total is not null && _duration > 0) _total.Text = Clock(_duration);
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
            if (_seek is not null) _seek.Value = d > 0 ? Math.Clamp(p / d * 1000, 0, 1000) : 0;
            _settingPosition = false;
            if (_now is not null) _now.Text = Clock(p);
            if (_total is not null && d > 0) _total.Text = Clock(d);
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

    private async void Drag_Starting(UIElement sender, DragStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_item.Path);
            e.Data.SetStorageItems([file]);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex) { App.Log("viewer-drag", ex, ex.Message); e.Cancel = true; }
        finally { deferral.Complete(); }
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
        var old = CopyFrameText.Text;
        CopyFrameText.Text = text;
        try { await Task.Delay(1600, _cts.Token); } catch { }
        CopyFrameText.Text = _item.Kind == "video" ? "Copy frame" : "Copy image";
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try { await Api.RevealAsync(_item.Id); } catch (Exception ex) { App.Log("viewer-reveal", ex, ex.Message); }
    }

    // ------------------------------------------------------------------ details panel
    private static TextBlock Small(string text) => new() { Text = text, FontSize = 11, Foreground = Ui.Brush("GlassTextTertiaryBrush") };
    private static TextBlock Value(string text) => new() { Text = text, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static UIElement Header(string icon, string text, string? hint = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon.Length > 0) row.Children.Add(new LineIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 70, VerticalAlignment = VerticalAlignment.Center });
        if (hint is not null) row.Children.Add(new TextBlock { Text = "· " + hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
        return row;
    }

    private static Border Divider() => new() { Height = 1, Background = Ui.Brush("GlassStrokeBrush"), Opacity = 0.6 };

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
        p.Children.Add(new TextBlock { Text = string.Join(" · ", one.Where(s => s.Length > 0)), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        if (two.Count > 0) p.Children.Add(new TextBlock { Text = string.Join(" · ", two), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
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
        actions.Children.Add(IconButton("text", "Copy the file path", (_, _) =>
        {
            var d = new DataPackage(); d.SetText(_item.Path); Clipboard.SetContent(d);
        }));
        actions.Children.Add(IconButton("file", "Copy the file", async (_, _) =>
        {
            try { var d = new DataPackage { RequestedOperation = DataPackageOperation.Copy }; d.SetStorageItems([await StorageFile.GetFileFromPathAsync(_item.Path)]); Clipboard.SetContent(d); }
            catch (Exception ex) { App.Log("viewer-copyfile", ex, ex.Message); }
        }));
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
                    _momentsBox.Children.Add(new TextBlock { Text = $"{s.Segments.Count} searchable moments. Click one to jump there.", FontSize = 11.5, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
                    var wrap = new WrapPanel { Spacing = 6 };
                    foreach (var seg in s.Segments)
                    {
                        var b = new Button { Style = Ui.Style("GlassChip"), Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(5), Content = new TextBlock { Text = $"{Clock(seg.Start)}–{Clock(seg.End)}", FontSize = 11.5 } };
                        var at = seg.Start; b.Click += (_, _) => Seek(at);
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

    // ------------------------------------------------------------------ more like this (infinite)
    private async Task LoadSimilarAsync(CancellationToken ct)
    {
        More.Children.Clear();
        _similar = []; _shown = 0;
        try
        {
            var list = await Api.SimilarAsync(_item.Id, new FileQuery(), 240, ct);
            if (ct.IsCancellationRequested) return;
            _similar = list.Where(f => f.Id != _item.Id).ToList();
            MoreNote.Text = _similar.Count == 0 ? "Nothing similar has been indexed yet." : "Closest first, by what is in them. Keep scrolling; click any to follow it.";
            AddMore();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("viewer-similar", ex, ex.Message); MoreNote.Text = "Couldn't find similar files just now."; }
    }

    private void Scroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_shown < _similar.Count && Scroll.ScrollableHeight - Scroll.VerticalOffset < 900) AddMore();
    }

    private void AddMore()
    {
        var end = Math.Min(_shown + Batch, _similar.Count);
        for (var i = _shown; i < end; i++) More.Children.Add(MoreTile(_similar[i]));
        _shown = end;
    }

    private UIElement MoreTile(FileItem f)
    {
        var tile = new Grid { Width = 232, Height = 168, CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillBrush") };
        DragHelper.Attach(tile, () => f.Path);
        if (f.Kind == "audio")
        {
            tile.Children.Add(new AudioCard { Vm = new TileVm(f, Api) });         // same inline player as the Library
        }
        else if (f.HasThumb)
        {
            tile.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(f.Id)) { DecodePixelWidth = 480 }, Stretch = Stretch.UniformToFill });
            if (f.Kind == "video")
                tile.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new LineIcon { Kind = "play", Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        }
        else
        {
            tile.Children.Add(new LineIcon { Kind = "file", Size = 34, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            tile.Children.Add(new TextBlock { Text = f.Name, FontSize = 11, Margin = new Thickness(10, 0, 10, 8), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        }
        tile.Children.Add(new Border { CornerRadius = new CornerRadius(5), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
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

