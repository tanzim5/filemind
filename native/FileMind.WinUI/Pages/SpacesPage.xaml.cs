using FileMind.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileMind.Pages;

/// <summary>Every space as a small stack of its own files; a click opens it in the Library.</summary>
public sealed partial class SpacesPage : Page
{
    private readonly CancellationTokenSource _cts = new();
    private ApiClient Api => AppRuntime.Current.Api!;
    private readonly SidePanel _side = new();

    public SpacesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { App.Window?.SetSidebar(_side.Root); await LoadAsync(); };
        Unloaded += (_, _) => _cts.Cancel();
    }

    private async Task LoadAsync()
    {
        try
        {
            var spaces = (await Api.GetProjectsAsync(_cts.Token)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Summary.Text = $"{spaces.Count:N0} spaces";
            Grid.Children.Clear();
            foreach (var p in spaces) Grid.Children.Add(Card(p));
            Grid.Children.Add(NewTile());
            _side.Clear();
            _side.Header("Spaces");
            foreach (var p in spaces) { var sp = p; _side.Row("folder", sp.Name, sp.Count.ToString("N0"), () => Open(sp)); }
            if (spaces.Count == 0) _side.Note("No spaces yet.");
            _side.Row("plus", "New space", null, () => NewSpace_Click(this, new RoutedEventArgs()));
            Note.Text = spaces.Count == 0 ? "Spaces are collections. Select files in the Library and add them to a space, or create an empty one here." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("spaces", ex, ex.Message); Note.Text = "Couldn't read your spaces just now."; }
    }

    private UIElement Card(ProjectEntry p)
    {
        const double W = 172, H = 124;
        var stack = new Grid { Width = W + 16, Height = H + 16 };
        var covers = p.Cover.Take(3).Reverse().ToList();
        for (var i = 0; i < covers.Count; i++)
        {
            var depth = covers.Count - 1 - i;              // 0 is the front card
            var img = new Border { Width = W, Height = H, CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillStrongBrush"), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = new CompositeTransform { TranslateX = depth * 8, TranslateY = -depth * 8, ScaleX = 1 - depth * 0.03, ScaleY = 1 - depth * 0.03 }, Opacity = 1 - depth * 0.18 };
            var g = new Grid { CornerRadius = new CornerRadius(5) };
            g.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(covers[i].Id)) { DecodePixelWidth = 340 }, Stretch = Stretch.UniformToFill });
            img.Child = g;
            stack.Children.Add(img);
        }
        if (covers.Count == 0)
            stack.Children.Add(new Border { Width = W, Height = H, CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillBrush"), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = new LineIcon { Kind = "spaces", Size = 28, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
        label.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 });
        label.Children.Add(new TextBlock { Text = p.Count.ToString("N0"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") });
        var card = new StackPanel { Spacing = 10, Background = new SolidColorBrush(Colors.Transparent), Width = W + 16 };
        card.Children.Add(stack); card.Children.Add(label);
        card.Tapped += (_, _) => Open(p);
        card.PointerEntered += (_, _) => card.Opacity = 0.82;
        card.PointerExited += (_, _) => card.Opacity = 1;
        return card;
    }

    private static void Open(ProjectEntry p)
    {
        LibraryPage.Request = new LibraryRequest(new FileQuery { Project = p.Id }, p.Name);
        App.Window?.ShowLibrary();
    }

    private UIElement NewTile()
    {
        var tile = new Border { Width = 172, Height = 124, CornerRadius = new CornerRadius(5), BorderBrush = Ui.Brush("GlassStrokeBrush"), BorderThickness = new Thickness(1), Background = Ui.Brush("GlassFillBrush"),
            Child = new LineIcon { Kind = "plus", Size = 26, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var card = new StackPanel { Spacing = 10, Width = 188, Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(8, 0, 8, 0) };
        card.Children.Add(tile);
        card.Children.Add(new TextBlock { Text = "New space", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
        card.Tapped += (_, _) => NewSpace_Click(this, new RoutedEventArgs());
        return card;
    }

    private async void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Name this space" };
        var dlg = new ContentDialog { Title = "New space", Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ThemeEngine.Element() };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        try { await Api.CreateProjectAsync(box.Text.Trim(), []); await LoadAsync(); }
        catch (Exception ex) { Note.Text = ex.Message; }
    }
}
