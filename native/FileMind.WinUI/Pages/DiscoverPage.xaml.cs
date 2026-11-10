using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FileMind.Pages;

/// <summary>Themes, questions and thoughts drawn from what is actually in the library. Everything opens in the Library.</summary>
public sealed partial class DiscoverPage : Page
{
    private const int QuestionCount = 12, ThoughtCount = 5;
    private readonly CancellationTokenSource _cts = new();
    private List<IdeaEntry> _ideas = [];
    private int _thoughtAt;
    private double _lastWidth;
    private bool _allQuestions;

    private ApiClient Api => AppRuntime.Current.Api!;
    private readonly SidePanel _side = new();

    private void BuildSide()
    {
        _side.Clear();
        _side.Header("On this page");
        void Add(FrameworkElement box, string icon, string label)
        {
            if (box.Visibility != Visibility.Visible) return;
            _side.Row(icon, label, null, () => box.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true }));
        }
        Add(ThemesBox, "photo", "Visual themes");
        Add(QuestionsBox, "search", "Questions worth asking");
        Add(DeeperBox, "sparkle", "Explore deeper");
        Add(ThoughtsBox, "sparkle", "Try a thought");
        Add(TypesBox, "file", "By file type");
    }

    public DiscoverPage()
    {
        InitializeComponent();
        BuildDeeper();
        // Sections appear as the engine answers; the sidebar list follows them.
        foreach (var box in new FrameworkElement[] { ThemesBox, QuestionsBox, DeeperBox, ThoughtsBox, TypesBox })
            box.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => BuildSide());
        Loaded += async (_, _) => { BuildSide(); App.Window?.SetSidebar(_side.Root); await LoadAsync(); };
        Unloaded += (_, _) => _cts.Cancel();
    }

    private void AllQuestions_Click(object sender, RoutedEventArgs e)
    {
        _allQuestions = !_allQuestions;
        if (sender is HyperlinkButton h) h.Content = _allQuestions ? "Show fewer" : "See all questions";
        BuildQuestions(_ideas.Take(_allQuestions ? _ideas.Count : QuestionCount).ToList(), Questions.ActualWidth > 0 ? Questions.ActualWidth : 1200);
    }

    private void BuildDeeper()
    {
        Deeper.Children.Add(DeepCard("duplicates", "Review duplicates", "Find copies and near-copies, then decide what to keep", () => App.Window?.ShowPage("duplicates")));
        Deeper.Children.Add(DeepCard("search", "Find a moment", "Describe a scene and jump to where it happens", () => { App.Window?.ShowLibrary(); LibraryPage.Current?.FocusSearch(); }));
        Deeper.Children.Add(DeepCard("photo", "Search with an image", "Pick a picture and find what looks like it", () => { App.Window?.ShowLibrary(); LibraryPage.Current?.ImageSearch_Click(this, new RoutedEventArgs()); }));
    }

    private static UIElement DeepCard(string icon, string title, string body, Action go)
    {
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = body, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillStrongBrush"), VerticalAlignment = VerticalAlignment.Center,
            Child = new LineIcon { Kind = icon, Size = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(badge); row.Children.Add(text);
        var card = new Border { Width = 330, Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush"), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), Child = row };
        card.Tapped += (_, _) => go();
        card.PointerEntered += (_, _) => card.Background = Ui.Brush("GlassFillHoverBrush");
        card.PointerExited += (_, _) => card.Background = Ui.Brush("GlassFillBrush");
        return card;
    }

    private async Task LoadAsync()
    {
        try
        {
            _ideas = await Api.GetIdeasAsync(36, _cts.Token);
            DrawIdeas();
            for (var tries = 0; tries < 24 && !_cts.IsCancellationRequested; tries++)
            {
                var d = await Api.GetDiscoverAsync(_cts.Token);
                DrawGroups(d.Groups);
                if (!d.Building) break;
                Note.Text = "Still looking through your library for more themes…";
                await Task.Delay(2500, _cts.Token);
            }
            Note.Text = "";
            if (_ideas.Count == 0 && ThemesBox.Visibility == Visibility.Collapsed && TypesBox.Visibility == Visibility.Collapsed)
                Note.Text = "Add a folder in the Library first. Once files are indexed, themes and questions appear here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("discover", ex, ex.Message); Note.Text = "Couldn't read your library just now."; }
    }

    private static void Open(LibraryRequest request)
    {
        LibraryPage.Request = request;
        App.Window?.ShowLibrary();
    }

    // ------------------------------------------------------------ themes and file types
    private void DrawGroups(List<DiscoverGroup> groups)
    {
        var themes = groups.Where(g => g.Key.StartsWith("theme:") && g.Count > 0).ToList();
        Themes.Children.Clear();
        foreach (var g in themes) Themes.Children.Add(Circle(g));
        ThemesBox.Visibility = themes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var types = groups.Where(g => g.Key.StartsWith("type:") && g.Count > 0).ToList();
        Types.Children.Clear();
        foreach (var g in types) Types.Children.Add(TypeCard(g));
        TypesBox.Visibility = types.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement Circle(DiscoverGroup g)
    {
        var pic = new Grid { Width = 92, Height = 92, CornerRadius = new CornerRadius(16), Background = Ui.Brush("GlassFillBrush") };
        if (g.Sample.Count > 0) pic.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(g.Sample[0].Id)) { DecodePixelWidth = 200 }, Stretch = Stretch.UniformToFill });
        pic.Children.Add(new Border { CornerRadius = new CornerRadius(16), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        var card = new StackPanel { Spacing = 6, Width = 128, Background = new SolidColorBrush(Colors.Transparent) };
        card.Children.Add(pic);
        card.Children.Add(new TextBlock { Text = g.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        card.Children.Add(new TextBlock { Text = $"{g.Count:N0} files", FontSize = 11, TextAlignment = TextAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
        card.Tapped += (_, _) => Open(new LibraryRequest(new FileQuery { Discover = g.Key }, g.Title));
        card.PointerEntered += (_, _) => card.Opacity = 0.8;
        card.PointerExited += (_, _) => card.Opacity = 1;
        return card;
    }

    private UIElement TypeCard(DiscoverGroup g)
    {
        var card = new Grid { Width = 300, Height = 190, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush") };
        if (g.Sample.Count > 0) card.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(g.Sample[0].Id)) { DecodePixelWidth = 600 }, Stretch = Stretch.UniformToFill });
        else card.Children.Add(new LineIcon { Kind = g.Key.EndsWith("audio") ? "music" : "video", Size = 40, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(new Rectangle { Height = 90, VerticalAlignment = VerticalAlignment.Bottom, Fill = Ui.Brush("TileScrimBrush") });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 14) };
        text.Children.Add(new TextBlock { Text = g.Title, Style = Ui.Style("TitleText"), FontSize = 26, Foreground = new SolidColorBrush(Colors.White) });
        text.Children.Add(new TextBlock { Text = $"{g.Count:N0} files", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) });
        card.Children.Add(text);
        card.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        card.Tapped += (_, _) => Open(new LibraryRequest(new FileQuery { Discover = g.Key }, g.Title));
        return card;
    }

    // ------------------------------------------------------------ questions and thoughts
    private void DrawIdeas()
    {
        var questions = _ideas.Take(_allQuestions ? _ideas.Count : QuestionCount).ToList();
        QuestionsBox.Visibility = questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildQuestions(questions, Questions.ActualWidth > 0 ? Questions.ActualWidth : 1200);
        ThoughtsBox.Visibility = _ideas.Count > QuestionCount ? Visibility.Visible : Visibility.Collapsed;
        DrawThoughts();
    }

    private void Questions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _lastWidth) < 40 || _ideas.Count == 0) return;
        BuildQuestions(_ideas.Take(_allQuestions ? _ideas.Count : QuestionCount).ToList(), e.NewSize.Width);
    }

    private void BuildQuestions(List<IdeaEntry> questions, double width)
    {
        _lastWidth = width;
        var cols = width switch { > 1100 => 4, > 800 => 3, > 520 => 2, _ => 1 };
        Questions.Children.Clear(); Questions.ColumnDefinitions.Clear(); Questions.RowDefinitions.Clear();
        for (var c = 0; c < cols; c++) Questions.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < (questions.Count + cols - 1) / cols; r++) Questions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var number = new TextBlock { Text = (i + 1).ToString("00"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 122, 69)) };
            var arrow = new TextBlock { Text = "↗", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Ui.Brush("GlassTextTertiaryBrush") };
            var title = new TextBlock { Text = Capital(q.Q), Style = Ui.Style("TitleText"), FontSize = 24, TextWrapping = TextWrapping.Wrap };
            var inner = new Grid { RowSpacing = 6 };
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.Children.Add(number); inner.Children.Add(arrow);
            Grid.SetRow(title, 1); inner.Children.Add(title);
            var card = new Border { Style = Ui.Style("GlassCard"), Padding = new Thickness(18, 14, 18, 16), CornerRadius = new CornerRadius(6), Child = inner, MinHeight = 96 };
            card.Tapped += (_, _) => Open(new LibraryRequest(new FileQuery(), "", [(q.Q, q.Q)]));
            card.PointerEntered += (_, _) => card.Background = Ui.Brush("GlassFillStrongBrush");
            card.PointerExited += (_, _) => card.Background = Ui.Brush("GlassFillBrush");
            Grid.SetRow(card, i / cols); Grid.SetColumn(card, i % cols);
            Questions.Children.Add(card);
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void DrawThoughts()
    {
        Thoughts.Children.Clear();
        var pool = _ideas.Skip(QuestionCount).ToList();
        if (pool.Count == 0) return;
        for (var i = 0; i < Math.Min(ThoughtCount, pool.Count); i++)
        {
            var idea = pool[(_thoughtAt + i) % pool.Count];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -8 };
            foreach (var c in idea.Top.Take(2))
            {
                var face = new Grid { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Background = Ui.Brush("GlassFillStrongBrush") };
                face.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(c.Id)) { DecodePixelWidth = 60 }, Stretch = Stretch.UniformToFill });
                faces.Children.Add(face);
            }
            if (faces.Children.Count > 0) row.Children.Add(faces);
            row.Children.Add(new TextBlock { Text = idea.Q, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(10, 6, 14, 6), CornerRadius = new CornerRadius(6), Content = row };
            chip.Click += (_, _) => Open(new LibraryRequest(new FileQuery(), "", [(idea.Q, idea.Q)]));
            Thoughts.Children.Add(chip);
        }
        var moreRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        moreRow.Children.Add(new LineIcon { Kind = "refresh", Size = 13 });
        moreRow.Children.Add(new TextBlock { Text = "More", FontSize = 13 });
        var more = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(6), Content = moreRow };
        more.Click += (_, _) => { _thoughtAt = (_thoughtAt + ThoughtCount) % Math.Max(1, pool.Count); DrawThoughts(); };
        Thoughts.Children.Add(more);
    }
}
