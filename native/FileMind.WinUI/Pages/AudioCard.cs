using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace FileMind.Pages;

/// <summary>A sound in the Library: a warm gradient card with its waveform and a play button that plays right there.</summary>
public sealed class AudioCard : Grid
{
    private static readonly Windows.UI.Color Idle = Windows.UI.Color.FromArgb(170, 214, 150, 150);
    private static readonly Windows.UI.Color Hot = Windows.UI.Color.FromArgb(255, 255, 106, 40);
    private static readonly Dictionary<int, double[]> Peaks = [];
    private static MediaPlayer? _player;
    private static AudioCard? _current;
    private static readonly DispatcherTimer Tick = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private const int N = 36;
    private readonly List<Rectangle> _bars = [];
    private readonly Border _play = new();
    private readonly TextBlock _meta = new() { FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)) };
    private readonly TextBlock _time = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)) };
    private readonly TextBlock _name = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Foreground = new SolidColorBrush(Colors.White) };
    private bool _loadedWave;
    private double _seekTo = -1;
    private int _hover = -1;
    private static readonly Windows.UI.Color Warm = Windows.UI.Color.FromArgb(235, 255, 176, 150);

    static AudioCard() { Tick.Tick += (_, _) => _current?.Progress(); }

    public static readonly DependencyProperty VmProperty = DependencyProperty.Register(nameof(Vm), typeof(TileVm), typeof(AudioCard), new PropertyMetadata(null, (d, _) => ((AudioCard)d).Bind()));
    public TileVm? Vm { get => (TileVm?)GetValue(VmProperty); set => SetValue(VmProperty, value); }

    public AudioCard()
    {
        CornerRadius = new CornerRadius(6);
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 30, 24, 30), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 78, 40, 56), Offset = 0.7 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 120, 52, 52), Offset = 1 },
            }
        };
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Padding = new Thickness(14, 12, 14, 12);

        var head = new Grid();
        head.Children.Add(_meta); head.Children.Add(_time);
        Children.Add(head);

        var mid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 8, 0, 8) };
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        _play.Width = 38; _play.Height = 38; _play.CornerRadius = new CornerRadius(19); _play.VerticalAlignment = VerticalAlignment.Center;
        _play.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255));
        _play.Child = new LineIcon { Kind = "play", Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _play.Tapped += (_, e) => { e.Handled = true; Toggle(); };
        _play.PointerPressed += (_, e) => e.Handled = true;      // a press on the button must not start a tile drag or selection
        mid.Children.Add(_play);

        var wave = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 54, Background = new SolidColorBrush(Colors.Transparent) };
        wave.PointerPressed += (_, e) => e.Handled = true;               // scrubbing must not start a tile drag or selection
        wave.PointerMoved += (_, e) => { _hover = Math.Clamp((int)(e.GetCurrentPoint(wave).Position.X / Math.Max(wave.ActualWidth, 1) * N), 0, N - 1); Paint(); };
        wave.PointerExited += (_, _) => { _hover = -1; Paint(); };
        wave.Tapped += (_, e) => { e.Handled = true; Scrub(e.GetPosition(wave).X / Math.Max(wave.ActualWidth, 1)); };
        for (var i = 0; i < N; i++)
        {
            wave.ColumnDefinitions.Add(new ColumnDefinition());
            var r = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.2, 0, 1.2, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Idle) };
            Grid.SetColumn(r, i); wave.Children.Add(r); _bars.Add(r);
        }
        Grid.SetColumn(wave, 1); mid.Children.Add(wave);
        Grid.SetRow(mid, 1); Children.Add(mid);

        Grid.SetRow(_name, 2); Children.Add(_name);
        Loaded += async (_, _) => await LoadWaveAsync();
        Unloaded += (_, _) => { if (_current == this) Reset(); };
    }

    private void Bind()
    {
        if (Vm is not { } vm) return;
        var f = vm.Item;
        _meta.Text = $"{f.Ext.TrimStart('.')} · {f.SizeText}";
        _time.Text = f.DurationText;
        _name.Text = f.Name;
        Shape(Peaks.TryGetValue(f.Id, out var p) ? p : Fake(f.Id));
        _loadedWave = Peaks.ContainsKey(f.Id);
        if (_current != this) { _played = 0; Paint(); Look(false); }
    }

    private static double[] Fake(int id)
    {
        var rnd = new Random(id * 7919 + 13);
        var a = new double[N];
        for (var i = 0; i < N; i++) a[i] = 0.25 + 0.6 * Math.Abs(Math.Sin(i * 0.55 + id)) * (0.5 + rnd.NextDouble() * 0.5);
        return a;
    }

    private void Shape(double[] p)
    {
        for (var i = 0; i < _bars.Count; i++) _bars[i].Height = 6 + 46 * Math.Clamp(p[Math.Min(i * p.Length / _bars.Count, p.Length - 1)], 0, 1);
    }

    private async Task LoadWaveAsync()
    {
        if (_loadedWave || Vm is not { } vm || AppRuntime.Current.Api is not { } api) return;
        try
        {
            var w = await api.GetWaveAsync(vm.Item.Id, CancellationToken.None);
            if (w.Peaks.Count == 0) return;
            var a = new double[N];
            for (var i = 0; i < N; i++) a[i] = w.Peaks[Math.Min(i * w.Peaks.Count / N, w.Peaks.Count - 1)];
            Peaks[vm.Item.Id] = a; _loadedWave = true;
            if (Vm?.Item.Id == vm.Item.Id) Shape(a);
        }
        catch { }
    }

    private void Toggle()
    {
        if (Vm is not { } vm || AppRuntime.Current.Api is not { } api) return;
        if (_current == this && _player is not null)
        {
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) { _player.Pause(); Look(false); }
            else { _player.Play(); Look(true); }
            return;
        }
        _current?.Reset();
        _current = this;
        _player ??= new MediaPlayer { AutoPlay = false, Volume = AppRuntime.Current.Settings.Volume };
        _player.Source = MediaSource.CreateFromUri(api.FileUri(vm.Item.Id));
        _player.MediaEnded -= Ended; _player.MediaEnded += Ended;
        _player.MediaOpened -= Opened; _player.MediaOpened += Opened;
        _player.Play(); Look(true); Tick.Start();
    }

    private static void Opened(MediaPlayer s, object a)
    {
        var c = _current;
        c?.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (c._seekTo >= 0 && s.PlaybackSession.NaturalDuration.TotalSeconds > 0)
                    s.PlaybackSession.Position = TimeSpan.FromSeconds(s.PlaybackSession.NaturalDuration.TotalSeconds * c._seekTo);
            }
            catch { }
            c._seekTo = -1;
        });
    }

    /// <summary>Click on the waveform: jump there and play (starting the sound if it was not playing yet).</summary>
    private void Scrub(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        if (_current != this || _player is null) { _seekTo = ratio; Toggle(); return; }
        try
        {
            var d = _player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (d > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(d * ratio);
            if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) { _player.Play(); Look(true); }
            Progress();
        }
        catch { }
    }

    private static void Ended(MediaPlayer s, object a) => _current?.DispatcherQueue.TryEnqueue(() => _current?.Reset());

    private void Look(bool playing)
    {
        _play.Background = new SolidColorBrush(playing ? Hot : Windows.UI.Color.FromArgb(70, 255, 255, 255));
        _play.Child = new LineIcon { Kind = playing ? "pause" : "play", Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private int _played;
    private void Paint()
    {
        for (var i = 0; i < _bars.Count; i++)
            ((SolidColorBrush)_bars[i].Fill).Color = i < _played ? Hot : (_hover >= 0 && i <= _hover ? Warm : Idle);
    }

    private void Reset()
    {
        Look(false); _played = 0; Paint(); _time.Text = Vm?.Item.DurationText ?? "";
        if (_current == this) { _current = null; Tick.Stop(); try { _player?.Pause(); } catch { } }
    }

    private void Progress()
    {
        try
        {
            if (_player is null) return;
            var d = _player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (d <= 0) return;
            var pos = _player.PlaybackSession.Position.TotalSeconds;
            _played = (int)(pos / d * _bars.Count);
            _time.Text = $"{Clock(pos)} / {Clock(d)}";
            Paint();
        }
        catch { }
    }

    private static string Clock(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)t.TotalMinutes}:{t.Seconds:00}"; }
}
