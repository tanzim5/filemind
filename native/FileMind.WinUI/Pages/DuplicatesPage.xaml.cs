using System.Diagnostics;
using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileMind.Pages;

/// <summary>Duplicates: exact copies (same bytes) and look-alikes (same picture, different file). Nothing is selected
/// until you ask, a whole set can never be removed, and removal goes to the Recycle Bin after a confirmation.</summary>
public sealed partial class DuplicatesPage : Page
{
    private DuplicatesResponse? _data;
    private string _tab = "exact";
    private readonly HashSet<int> _sel = [];
    private readonly Dictionary<int, FileItem> _byId = [];
    private readonly Dictionary<int, (Border Ring, Border Check)> _marks = [];
    private ApiClient Api => AppRuntime.Current.Api!;

    // The last scan survives leaving the page, so coming back is instant.
    private static DuplicatesResponse? _cached;

    public DuplicatesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_cached is not null) Show(_cached); };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        Scroll.Visibility = Visibility.Collapsed; Bar.Visibility = Visibility.Collapsed; RescanBtn.Visibility = Visibility.Collapsed;
        Hero.Visibility = Visibility.Visible; ScanBtn.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible; Busy.IsActive = true;
        HeroIcon.Glyph = "duplicates"; HeroIcon.Tone = "duplicates";
        HeroTitle.Text = "Comparing your files…";
        HeroBody.Text = "Large libraries take a minute: every file that shares its size with another is read and fingerprinted.";
        try { _cached = await Api.GetDuplicatesAsync(); Show(_cached); }
        catch (Exception ex)
        {
            App.Log("duplicates", ex, ex.Message);
            HeroTitle.Text = "Couldn't finish the scan"; HeroBody.Text = ex.Message; ScanBtn.Visibility = Visibility.Visible;
        }
        finally { Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed; }
    }

    private void Show(DuplicatesResponse d)
    {
        _data = d; _sel.Clear(); _byId.Clear();
        foreach (var f in d.Exact.Concat(d.Visual).SelectMany(g => g)) _byId[f.Id] = f;
        var waste = d.Exact.Sum(g => g.Skip(1).Sum(f => f.Size));
        ExactTab.Content = $"Exact copies   {d.Exact.Count:N0}";
        VisualTab.Content = $"Look-alikes   {d.Visual.Count:N0}";
        var sets = d.Exact.Count + d.Visual.Count;
        App.Window?.SetCount("duplicates", sets > 0 ? sets.ToString("N0") : "");
        RescanBtn.Visibility = Visibility.Visible;
        if (sets == 0)
        {
            Hero.Visibility = Visibility.Visible; Scroll.Visibility = Visibility.Collapsed; Bar.Visibility = Visibility.Collapsed;
            HeroIcon.Glyph = "check"; HeroIcon.Tone = "spaces";
            HeroTitle.Text = "No duplicates"; HeroBody.Text = "Every file in your folders is one of a kind."; ScanBtn.Visibility = Visibility.Collapsed;
            Summary.Text = "Nothing to clean up.";
            return;
        }
        Summary.Text = waste > 0 ? $"Extra exact copies take up {Size(waste)} you could get back." : "Copies and near-copies across your folders. Nothing is removed until you choose.";
        Hero.Visibility = Visibility.Collapsed; Scroll.Visibility = Visibility.Visible; Bar.Visibility = Visibility.Visible;
        if (_tab == "exact" && d.Exact.Count == 0) _tab = "visual";
        Render();
    }

    private void Tab_Click(object sender, RoutedEventArgs e) { _tab = (string)((FrameworkElement)sender).Tag; Render(); }

    private List<List<FileItem>> Current => _data is null ? [] : _tab == "exact" ? _data.Exact : _data.Visual;

    private void Render()
    {
        ExactTab.IsChecked = _tab == "exact"; VisualTab.IsChecked = _tab == "visual";
        Groups.Children.Clear(); _marks.Clear();
        foreach (var g in Current.Where(g => g.Count > 1)) Groups.Children.Add(GroupCard(g));
        if (Groups.Children.Count == 0)
            Groups.Children.Add(new TextBlock { Text = _tab == "exact" ? "No exact copies." : "No look-alikes.", Style = Ui.Style("SecondaryText"), Margin = new Thickness(4, 10, 0, 0) });
        Scroll.ChangeView(null, 0, null, true);
        Sync();
    }

    private UIElement GroupCard(List<FileItem> g)
    {
        var card = new Border { Style = Ui.Style("GlassCard"), Padding = new Thickness(16, 12, 16, 10) };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            FontSize = 12.5, Foreground = Ui.Brush("GlassTextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            Text = _tab == "exact" ? $"{g.Count} copies  ·  {g[0].SizeText} each  ·  {g[0].Name}" : $"{g.Count} look alike  ·  {g[0].Name}",
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var keep = Keeper(g);
        foreach (var f in g) row.Children.Add(FileTile(f, f.Id == keep.Id));
        stack.Children.Add(new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 8),
        });
        card.Child = stack;
        return card;
    }

    /// <summary>Which file of a set to keep: the oldest exact copy (usually the original), or the sharpest look-alike.</summary>
    private FileItem Keeper(List<FileItem> g) => _tab == "exact"
        ? g.OrderBy(f => f.Mtime).First()
        : g.OrderByDescending(f => (long)(f.Width ?? 0) * (f.Height ?? 0)).ThenByDescending(f => f.Size).First();

    private static SolidColorBrush Dim => new(Windows.UI.Color.FromArgb(0x88, 0, 0, 0));

    private UIElement FileTile(FileItem f, bool suggested)
    {
        const double W = 168, H = 118;
        var img = new Grid { Width = W, Height = H, CornerRadius = new CornerRadius(5), Background = Ui.Brush("GlassFillBrush") };
        if (f.HasThumb) img.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(f.Id)) { DecodePixelWidth = 340 }, Stretch = Stretch.UniformToFill });
        else img.Children.Add(new LineIcon { Kind = f.Kind is "video" or "audio" or "text" ? f.Kind : "file", Size = 30, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var ring = new Border { BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(5), Opacity = 0, IsHitTestVisible = false };
        var check = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Dim, BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 255, 255, 255)), BorderThickness = new Thickness(1.5),
            Child = new LineIcon { Kind = "check", Size = 12, Stroke = 2.2, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = 0 },
        };
        img.Children.Add(ring); img.Children.Add(check);
        if (suggested)
            img.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0, 0, 0)),
                Child = new TextBlock { Text = _tab == "exact" ? "Original" : "Sharpest", FontSize = 10.5, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            });
        _marks[f.Id] = (ring, check);

        var info = new StackPanel { Spacing = 1, Width = W };
        info.Children.Add(new TextBlock { Text = f.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f.Path) ?? "");
        var when = DateTimeOffset.FromUnixTimeSeconds((long)f.Mtime).LocalDateTime.ToString("d MMM yyyy");
        var dims = f.Width is > 0 && f.Height is > 0 ? $" · {f.Width}×{f.Height}" : "";
        info.Children.Add(new TextBlock
        {
            Text = _tab == "exact" ? $"{folder} · {when}" : $"{f.SizeText}{dims}", FontSize = 11,
            Foreground = Ui.Brush("GlassTextTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var tile = new StackPanel { Spacing = 7, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        tile.Children.Add(img); tile.Children.Add(info);
        ToolTipService.SetToolTip(tile, f.Path);
        tile.Tapped += (_, _) => Toggle(f.Id);
        tile.DoubleTapped += (_, _) => Open(f.Path);
        var menu = new MenuFlyout();
        void Item(string text, Action a) { var m = new MenuFlyoutItem { Text = text }; m.Click += (_, _) => a(); menu.Items.Add(m); }
        Item("Open", () => Open(f.Path));
        Item("Show in folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.Path}\"") { UseShellExecute = false }));
        tile.ContextFlyout = menu;
        return tile;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("open", ex, path); }
    }

    private void Toggle(int id) { if (!_sel.Remove(id)) _sel.Add(id); Sync(); }

    private void Sync()
    {
        foreach (var (id, (ring, check)) in _marks)
        {
            var on = _sel.Contains(id);
            ring.Opacity = on ? 1 : 0;
            check.Background = on ? Ui.Brush("AccentBrush") : Dim;
            ((LineIcon)check.Child).Opacity = on ? 1 : 0;
        }
        var bytes = _sel.Where(_byId.ContainsKey).Sum(i => _byId[i].Size);
        SelText.Text = _sel.Count == 0 ? "Click files to select them, or let Smart select choose." : $"{_sel.Count:N0} selected  ·  {Size(bytes)}";
        RemoveBtn.IsEnabled = _sel.Count > 0;
        RemoveText.Text = _sel.Count == 0 ? "Move to Recycle Bin" : $"Move {_sel.Count:N0} to Recycle Bin";
    }

    private void Smart_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in Current)
        {
            var keep = Keeper(g).Id;
            foreach (var f in g) { if (f.Id == keep) _sel.Remove(f.Id); else _sel.Add(f.Id); }
        }
        Sync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_sel.Count == 0 || _data is null) return;
        // A whole set never goes: at least one file of every group must stay.
        var whole = _data.Exact.Concat(_data.Visual).FirstOrDefault(g => g.Count > 1 && g.All(f => _sel.Contains(f.Id)));
        if (whole is not null)
        {
            await Dialog("Keep at least one", $"Every copy of “{whole[0].Name}” is selected. Leave one unticked so the file isn't lost.", null);
            return;
        }
        var bytes = _sel.Where(_byId.ContainsKey).Sum(i => _byId[i].Size);
        if (!await Dialog($"Move {_sel.Count:N0} files to the Recycle Bin?", $"That frees {Size(bytes)}. You can restore them from the Recycle Bin.", "Move to Recycle Bin")) return;
        try
        {
            foreach (var chunk in _sel.ToList().Chunk(500)) await Api.FileActionAsync("delete", chunk);
            _sel.Clear();
            await ScanAsync();
        }
        catch (Exception ex) { await Dialog("Some files couldn't be moved", ex.Message, null); await ScanAsync(); }
    }

    private async Task<bool> Dialog(string title, string body, string? primary)
    {
        var dlg = new ContentDialog { Title = title, Content = body, CloseButtonText = primary is null ? "OK" : "Cancel", XamlRoot = XamlRoot, RequestedTheme = ThemeEngine.Element() };
        if (primary is not null) { dlg.PrimaryButtonText = primary; dlg.DefaultButton = ContentDialogButton.Close; }
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string Size(long b) => new FileItem { Size = b }.SizeText;
}
