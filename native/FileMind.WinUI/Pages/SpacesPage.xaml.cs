using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace FileMind.Pages;

/// <summary>Every space as a small fanned stack of its own files; a click opens it in the Library. "Organise for me" proposes new ones.</summary>
public sealed partial class SpacesPage : Page
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Button _organise;
    private readonly StackPanel _bar;
    private ApiClient Api => AppRuntime.Current.Api!;

    public SpacesPage()
    {
        InitializeComponent();
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        // The top bar's right side: the primary "Organise for me" button and a plus for an empty space.
        var white = new SolidColorBrush(Colors.White);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new LineIcon { Kind = "sparkle", Size = 14, Foreground = white, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = "Organise for me", FontSize = 12.5, Foreground = white, VerticalAlignment = VerticalAlignment.Center });
        _organise = new Button { Style = Ui.Style("GlassAccentButton"), Height = 28, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(6), Content = row, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_organise, "Group the files that are in no space into suggested spaces");
        _organise.Click += Organise_Click;
        var plus = new Button { Style = Ui.Style("BarButton"), Content = new LineIcon { Kind = "plus", Size = 16 } };
        ToolTipService.SetToolTip(plus, "Create a new space");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(plus, "Create a new space");
        plus.Click += NewSpace_Click;
        _bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _bar.Children.Add(_organise); _bar.Children.Add(plus);

        Loaded += async (_, _) => { App.Window?.SetTitle("Spaces"); App.Window?.SetActions(_bar); await LoadAsync(); _ = ShowSuggestionsAsync(false); };
        Unloaded += (_, _) => _cts.Cancel();
    }

    private async Task LoadAsync()
    {
        try
        {
            var spaces = (await Api.GetProjectsAsync(_cts.Token)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Cards.Children.Clear();
            foreach (var p in spaces) Cards.Children.Add(Card(p.Name, p.Count, p.Cover.Take(3).Select(c => c.Id).ToList(), LibraryPage.SpaceRing(p.Id), () => Open(p)));
            Cards.Children.Add(NewTile());
            App.Window?.SetTitle("Spaces", spaces.Count.ToString("N0"));
            App.Window?.SetCount("spaces", spaces.Count > 0 ? spaces.Count.ToString("N0") : "");
            Note.Text = spaces.Count == 0 ? "Spaces are collections. Select files in the Library and add them to a space, or let File Mind suggest some." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("spaces", ex, ex.Message); Note.Text = "Couldn't read your spaces just now."; }
    }

    /// <summary>Equal columns that follow the width, so the grid always fills the page with no ragged edge.</summary>
    private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is WrapPanel w) w.Columns = Math.Max(1, (int)(e.NewSize.Width / 210));
    }

    /// <summary>A stack of up to three thumbnails (first one in front, the others fanned behind), then ring, name and count.</summary>
    private UIElement Card(string name, int count, List<int> covers, string ring, Action open, UIElement? extra = null, bool dashed = false)
    {
        const double W = 152, H = 108;
        var stack = new Grid { Width = 190, Height = H + 30, HorizontalAlignment = HorizontalAlignment.Center };
        if (dashed)
            stack.Children.Add(new Rectangle { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Stroke = Ui.Brush("GlassTextTertiaryBrush"), Margin = new Thickness(0, 0, 0, 0) });
        if (covers.Count == 0)
            stack.Children.Add(StackCard(W, H, null, 0, 0, 0));
        // back to front: the last cover is drawn first, the first cover ends up on top
        var fan = new (double Rot, double X, double Y)[] { (0, 0, 0), (3, 10, -3), (-4, -10, -5) };
        for (var i = Math.Min(covers.Count, 3) - 1; i >= 0; i--)
            stack.Children.Add(StackCard(W, H, covers[i], fan[i].Rot, fan[i].X, fan[i].Y));

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
        label.Children.Add(new Ellipse { Width = 10, Height = 10, Stroke = Ui.Hex(ring), StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 });
        label.Children.Add(new TextBlock { Text = count.ToString("N0"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
        var card = new StackPanel { Spacing = 10, Background = new SolidColorBrush(Colors.Transparent) };
        card.Children.Add(stack); card.Children.Add(label);
        card.Tapped += (_, _) => open();
        card.PointerEntered += (_, _) => { card.Opacity = 0.92; Glow.On(card, 8, false); };
        card.PointerExited += (_, _) => { card.Opacity = 1; Glow.Off(card, false); };
        if (extra is null) return card;
        // a card that comes with its own buttons (the suggestions): the buttons must not trigger the open
        var outer = new StackPanel { Spacing = 10 };
        outer.Children.Add(card); outer.Children.Add(extra);
        return outer;
    }

    /// <summary>One slightly turned white card with a soft shadow; null shows an empty one.</summary>
    private UIElement StackCard(double w, double h, int? thumb, double rotation, double dx, double dy)
    {
        var inner = new Grid { CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillStrongBrush") };
        if (thumb is { } id) inner.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(id)) { DecodePixelWidth = 320 }, Stretch = Stretch.UniformToFill });
        else inner.Children.Add(new LineIcon { Kind = "spaces", Size = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var white = new SolidColorBrush(Colors.White);
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3), BorderBrush = white, Background = white, Child = inner,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new CompositeTransform { Rotation = rotation, TranslateX = dx, TranslateY = dy },
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 12),
        };
    }

    private static void Open(ProjectEntry p)
    {
        LibraryPage.Request = new LibraryRequest(new FileQuery { Project = p.Id }, p.Name);
        App.Window?.ShowLibrary();
    }

    private UIElement NewTile()
    {
        var dashed = new Rectangle
        {
            Width = 152, Height = 108, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.5, StrokeDashArray = [4, 3],
            Stroke = Ui.Brush("GlassTextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new Grid { Width = 190, Height = 138, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(dashed);
        stack.Children.Add(new LineIcon { Kind = "plus", Size = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var card = new StackPanel { Spacing = 10, Background = new SolidColorBrush(Colors.Transparent) };
        card.Children.Add(stack);
        card.Children.Add(new TextBlock { Text = "New space", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
        card.Tapped += (_, _) => NewSpace_Click(this, new RoutedEventArgs());
        card.PointerEntered += (_, _) => { card.Opacity = 0.92; Glow.On(card, 8, false); };
        card.PointerExited += (_, _) => { card.Opacity = 1; Glow.Off(card, false); };
        return card;
    }

    public void DevNewSpace() => NewSpace_Click(this, new RoutedEventArgs());

    private async void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        try { if (await NewSpaceDialog.ShowAsync(XamlRoot, Api)) await LoadAsync(); }
        catch (Exception ex) { Note.Text = ex.Message; }
    }

    // ---- Suggested: the engine proposes groups from files in no space; nothing is made until you keep one ----
    private static ClusterResponse? _cache;                 // for the session: opening the page again does not ask the engine again
    private static Task<ClusterResponse>? _pending;
    private static DismissedSpaces? _dismissed;
    private DismissedSpaces Dismissed => _dismissed ??= new DismissedSpaces(AppRuntime.Current.Paths.DismissedSpacesFile);

    private async void Organise_Click(object sender, RoutedEventArgs e)
    {
        _organise.IsEnabled = false;
        Note.Text = "Looking through the files that are in no space…";
        try { await ShowSuggestionsAsync(true); }
        finally { _organise.IsEnabled = true; }
    }

    private async Task ShowSuggestionsAsync(bool refresh)
    {
        try
        {
            if (refresh) _cache = null;
            var found = _cache;
            if (found is null)
            {
                _pending ??= Api.ProposeSpacesAsync(true);
                try { found = await _pending; } finally { _pending = null; }
                _cache = found;
            }
            if (!IsLoaded) return;
            Note.Text = "";
            var shown = Dismissed.Visible(found.Clusters);
            Review.Children.Clear();
            foreach (var c in shown) Review.Children.Add(Proposal(c));
            if (shown.Count == 0)
            {
                SuggestBox.Visibility = YourTitle.Visibility = Visibility.Collapsed;
                if (refresh) Note.Text = found.Leftover == 0 ? "Every file is already in a space." : "Nothing clear to suggest yet.";
                return;
            }
            SuggestNote.Text = "from files in no space";
            SuggestBox.Visibility = YourTitle.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { App.Log("organise", ex, ex.Message); if (refresh && IsLoaded) Note.Text = "Couldn't suggest spaces just now."; }
    }

    /// <summary>A suggestion drawn as a dashed stack, with Keep and Dismiss under it.</summary>
    private UIElement Proposal(ClusterProposal c)
    {
        var covers = c.Sample.Where(f => f.HasThumb).Take(3).Select(f => f.Id).ToList();
        var keep = new Button { Style = Ui.Style("GlassAccentButton"), Padding = new Thickness(14, 5, 14, 5), CornerRadius = new CornerRadius(6), Content = new TextBlock { Text = "Keep", FontSize = 12.5, Foreground = new SolidColorBrush(Colors.White) } };
        var dismiss = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(14, 5, 14, 5), CornerRadius = new CornerRadius(6), Content = new TextBlock { Text = "Dismiss", FontSize = 12.5 } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(keep); buttons.Children.Add(dismiss);
        var card = Card(c.Name, c.Count, covers, "#F2552C", () => { }, buttons, dashed: true);
        dismiss.Click += (_, _) => { Dismissed.Dismiss(c.Name); Remove(card); };
        keep.Click += async (_, _) =>
        {
            keep.IsEnabled = dismiss.IsEnabled = false;
            try
            {
                await Api.CreateProjectAsync(c.Name, c.FileIds, _cts.Token);
                _cache?.Clusters.Remove(c);
                Remove(card); await LoadAsync();
            }
            catch (Exception ex) { App.Log("organise", ex, ex.Message); keep.IsEnabled = dismiss.IsEnabled = true; Note.Text = "Couldn't save that space."; }
        };
        return card;
    }

    private void Remove(UIElement card)
    {
        Review.Children.Remove(card);
        if (Review.Children.Count == 0) SuggestBox.Visibility = YourTitle.Visibility = Visibility.Collapsed;
    }
}
