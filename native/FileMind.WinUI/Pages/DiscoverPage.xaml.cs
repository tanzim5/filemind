using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileMind.Pages;

/// <summary>Two sections only: the visual themes in the library, and a few searches worth trying. Everything opens in the Library.</summary>
public sealed partial class DiscoverPage : Page
{
    private const int SuggestionCount = 10;
    private readonly CancellationTokenSource _cts = new();
    private List<IdeaEntry> _ideas = [];
    private int _at;

    private ApiClient Api => AppRuntime.Current.Api!;
    public DiscoverPage()
    {
        InitializeComponent();
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        Loaded += async (_, _) => { App.Window?.SetTitle("Discover"); App.Window?.SetActions(null); await LoadAsync(); };
        Unloaded += (_, _) => { _cts.Cancel(); _momentTimer?.Stop(); };
        Loaded += async (_, _) => { try { ShowMomentQueue(await Api.GetMomentQueueAsync()); } catch (Exception) { } };
    }

    private async Task LoadAsync()
    {
        try
        {
            // The ones with pictures first: a chip with faces is worth more than a bare phrase.
            _ideas = (await Api.GetIdeasAsync(36, _cts.Token)).OrderByDescending(i => i.Top.Count > 0).ToList();
            DrawSuggestions();
            for (var tries = 0; tries < 72 && !_cts.IsCancellationRequested; tries++)
            {
                var d = await Api.GetDiscoverAsync(_cts.Token);
                DrawThemes(d.Groups);
                if (!d.Building) break;
                Note.Text = "Updating themes in the background…";
                await Task.Delay(2500, _cts.Token);
            }
            Note.Text = "";
            if (_ideas.Count == 0 && ThemesBox.Visibility == Visibility.Collapsed)
                Note.Text = "Add a folder in the Library first. Once files are indexed, themes and searches appear here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("discover", ex, ex.Message); Note.Text = "Couldn't read your library just now."; }
    }

    private DispatcherTimer? _momentTimer;

    private async void Moments_Click(object sender, RoutedEventArgs e)
    {
        MomentsBtn.IsEnabled = false;
        try { ShowMomentQueue(await Api.QueueMomentsAsync(true)); }
        catch (Exception ex) { App.Log("moments-queue", ex, ex.Message); MomentsStatus.Text = "Couldn't start. " + ex.Message; MomentsBtn.IsEnabled = true; }
    }

    private void ShowMomentQueue(MomentQueue q)
    {
        if (q.Running)
        {
            MomentsBtn.IsEnabled = false;
            MomentsStatus.Text = $"Looking through {q.Done:N0} of {q.Total:N0}" + (q.Current is { Length: > 0 } c ? $" · {c}" : "");
            _momentTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _momentTimer.Tick -= MomentTick; _momentTimer.Tick += MomentTick; _momentTimer.Start();
        }
        else
        {
            _momentTimer?.Stop();
            MomentsBtn.IsEnabled = true;
            MomentsStatus.Text = q.Total > 0 || q.Done > 0 ? "Done. Search for something and the moments will show above the results." : "";
        }
    }

    private async void MomentTick(object? sender, object e)
    {
        try { ShowMomentQueue(await Api.GetMomentQueueAsync(_cts.Token)); } catch (Exception) { _momentTimer?.Stop(); }
    }

    private static void Open(LibraryRequest request)
    {
        LibraryPage.Request = request;
        App.Window?.ShowLibrary();
    }

    // ------------------------------------------------------------ themes
    private List<DiscoverGroup> _themes = [];

    private void DrawThemes(List<DiscoverGroup> groups)
    {
        _themes = groups.Where(g => g.Key.StartsWith("theme:") && g.Count > 0).ToList();
        Themes.Children.Clear();
        foreach (var g in _themes) Themes.Children.Add(ThemeCard(g));
        ThemesBox.Visibility = _themes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Themes.Columns = ColumnsFor(Themes.ActualWidth);
    }

    /// <summary>Equal columns that follow the width, so the grid fills the page with no ragged edge.</summary>
    private static int ColumnsFor(double width) => Math.Max(2, (int)(width / 230));

    private void Themes_SizeChanged(object sender, SizeChangedEventArgs e) => Themes.Columns = ColumnsFor(e.NewSize.Width);

    /// <summary>A 4:3 picture with rounded corners, the theme's name and its file count underneath.</summary>
    private UIElement ThemeCard(DiscoverGroup g)
    {
        var pic = new Grid { MinHeight = 90, CornerRadius = new CornerRadius(8), Background = Ui.Brush("GlassFillStrongBrush") };
        pic.SizeChanged += (_, e) => { var h = Math.Round(e.NewSize.Width * 0.75); if (double.IsNaN(pic.Height) || Math.Abs(pic.Height - h) > 1) pic.Height = h; };
        if (g.Sample.Count > 0) pic.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(g.Sample[0].Id)) { DecodePixelWidth = 480 }, Stretch = Stretch.UniformToFill });
        pic.Children.Add(new Microsoft.UI.Xaml.Controls.Border { CornerRadius = new CornerRadius(8), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        var card = new StackPanel { Spacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        card.Children.Add(pic);
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        text.Children.Add(new TextBlock { Text = g.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
        text.Children.Add(new TextBlock { Text = $"{g.Count:N0}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        card.Children.Add(text);
        card.Tapped += (_, _) => Open(new LibraryRequest(new FileQuery { Discover = g.Key }, g.Title));
        card.PointerEntered += (_, _) => { card.Opacity = 0.92; Glow.On(card, 6, false); };
        card.PointerExited += (_, _) => { card.Opacity = 1; Glow.Off(card, false); };
        return card;
    }

    // ------------------------------------------------------------ searches worth trying
    private void DrawSuggestions()
    {
        ThoughtsBox.Visibility = _ideas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Thoughts.Children.Clear();
        if (_ideas.Count == 0) return;
        var shown = Math.Min(SuggestionCount, _ideas.Count);
        for (var i = 0; i < shown; i++)
        {
            var idea = _ideas[(_at + i) % _ideas.Count];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -8 };
            foreach (var c in idea.Top.Take(2))
            {
                var face = new Grid { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillStrongBrush") };
                face.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(c.Id)) { DecodePixelWidth = 60 }, Stretch = Stretch.UniformToFill });
                faces.Children.Add(face);
            }
            if (faces.Children.Count > 0) row.Children.Add(faces);
            row.Children.Add(new TextBlock { Text = Capital(idea.Q), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(10, 6, 14, 6), CornerRadius = new CornerRadius(8), Content = row };
            chip.Click += (_, _) => Open(new LibraryRequest(new FileQuery(), "", [(idea.Q, idea.Q)]));
            Thoughts.Children.Add(chip);
        }
        if (_ideas.Count > shown)
        {
            var moreRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            moreRow.Children.Add(new LineIcon { Kind = "refresh", Size = 13 });
            moreRow.Children.Add(new TextBlock { Text = "More", FontSize = 13 });
            var more = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(8), Content = moreRow };
            more.Click += (_, _) => { _at = (_at + SuggestionCount) % _ideas.Count; DrawSuggestions(); };
            Thoughts.Children.Add(more);
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
