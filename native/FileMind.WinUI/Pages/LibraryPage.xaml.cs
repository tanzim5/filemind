using System.Collections.ObjectModel;
using System.Diagnostics;
using FileMind.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace FileMind.Pages;

/// <summary>Something another page wants the library to show (a Discover group, a space, a duplicate set…).</summary>
public sealed record LibraryRequest(FileQuery Query, string Title, List<(string Label, string Query)>? Tags = null, int Similar = 0, string SimilarName = "");

public sealed record ViewerRequest(FileItem Item, IReadOnlyList<FileItem> Siblings, double? StartAt = null, double? EndAt = null);

public sealed partial class LibraryPage : Page
{
    private const int PageSize = 200;

    /// <summary>Set by the shell or another page right before navigating here.</summary>
    public static LibraryRequest? Request { get; set; }

    private sealed record NavState(FileQuery Q, List<(string Label, string Query)> Tags, int Similar, string SimilarName, string? ImagePath, string Title);

    private readonly ObservableCollection<TileVm> _tiles = [];
    private readonly HashSet<int> _sel = [];
    private readonly Stack<NavState> _back = new(), _fwd = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer _detailDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource _cts = new();

    private FileQuery _q = new();
    private List<(string Label, string Query)> _tags = [];
    private int _similarTo;
    private string _similarName = "", _title = "";
    private string? _imagePath;
    private List<FileItem> _imageHits = [];

    private int _offset, _total, _lastChecked = -1;
    private bool _more, _loading, _wasIndexing, _syncing;
    private TileVm? _focused, _lastTapTile;
    private long _lastTapAt;

    private LibraryStats _stats = new();
    private List<ColorSwatch> _colors = [];
    private List<SourceEntry> _sources = [];
    private List<MoodEntry> _moods = [];
    private List<ProjectEntry> _projects = [];
    private List<RootFolder> _roots = [];

    private ApiClient Api => AppRuntime.Current.Api!;
    private AppSettings Settings => AppRuntime.Current.Settings;
    private TileLayout Layout => (TileLayout)TileRepeater.Layout;

    /// <summary>The live Library, so Settings can reshape its sidebar sections.</summary>
    public static LibraryPage? Current { get; private set; }

    /// <summary>Puts the sidebar sections in the order chosen in Settings and hides those switched off.</summary>
    // One deliberate order, most useful first: what kind of file, your own tags, your spaces, where it came from,
    // the hard facts (Refine), how it looks, and finally the folders.
    public void ApplySections()
    {
        SectionStack.Children.Clear();
        _filtersStack.Children.Clear();
        var byKey = new Dictionary<string, UIElement>
        {
            ["types"] = SecTypes, ["labels"] = SecLabels, ["spaces"] = SecSpaces, ["sources"] = SecSources,
            ["refine"] = SecDetails, ["colours"] = SecColours, ["moods"] = SecMoods, ["folders"] = SecFolders,
        };
        // The slim sidebar keeps what you reach for all the time; the detailed filters live in the Filters panel.
        foreach (var key in OrderedSectionKeys())
        {
            byKey[key].Visibility = Visibility.Visible;
            SectionStack.Children.Add(byKey[key]);
        }
        FiltersBtn.Visibility = Classic ? Visibility.Collapsed : Visibility.Visible;
        if (Classic) return;
        foreach (var key in new[] { "refine", "colours", "moods", "folders" })
        {
            byKey[key].Visibility = Visibility.Visible;
            _filtersStack.Children.Add(byKey[key]);
        }
    }

    /// <summary>The older layout: every section in the left sidebar, no Filters button.</summary>
    public static bool Classic => AppRuntime.Current.Settings.SidebarStyle == "classic";

    // The detailed filters (Refine, Colours, Looks & moods, Folders) open from the Filters button in the toolbar.
    private readonly StackPanel _filtersStack = new() { Spacing = 18, Padding = new Thickness(4, 4, 8, 8), Width = 340 };

    private void BuildFiltersFlyout()
    {
        var scroll = new ScrollViewer { Content = _filtersStack, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        FiltersBtn.Flyout = new Flyout { Content = scroll, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
    }

    /// <summary>How many of the Filters-panel filters are on: Refine choices, a colour, a folder.</summary>
    private int FilterCount() => _q.DetailFilters().Count + (_q.Color is not null ? 1 : 0) + (_q.Near is not null ? 1 : 0) + (_q.Root is not null ? 1 : 0);

    /// <summary>The sidebar sections in the default order, with their names for Settings.</summary>
    public static (string Key, string Name)[] SidebarSections => Classic ? AllSections : BarSections;

    private static readonly (string Key, string Name)[] BarSections =
    [
        ("types", "File types"), ("labels", "Labels"), ("sources", "Sources"), ("spaces", "Spaces"),
    ];

    private static readonly (string Key, string Name)[] AllSections =
    [
        ("types", "File types"), ("labels", "Labels"), ("spaces", "Spaces"), ("sources", "Sources"),
        ("refine", "Refine"), ("colours", "Colours"), ("moods", "Looks & moods"), ("folders", "Folders"),
    ];

    /// <summary>The order chosen in Settings; sections it doesn't mention keep their default place after it.</summary>
    public static List<string> OrderedSectionKeys()
    {
        var all = SidebarSections.Select(s => s.Key).ToList();
        var chosen = AppRuntime.Current.Settings.SidebarOrder.Select(k => k == "tags" ? "labels" : k).Where(all.Contains).Distinct().ToList();
        return chosen.Concat(all.Where(k => !chosen.Contains(k))).ToList();
    }

    private static bool IsFolded(string key) => AppRuntime.Current.Settings.Collapsed.Contains(key);

    private static void SetFolded(string key, bool folded)
    {
        var s = AppRuntime.Current.Settings;
        s.Collapsed.Remove(key);
        if (folded) s.Collapsed.Add(key);
        try { s.Save(AppRuntime.Current.Paths); } catch (Exception ex) { App.Log("fold", ex, null); }
    }

    /// <summary>Clicking a heading's text folds or unfolds what is under it. The choice is remembered.</summary>
    private static void Foldable(FrameworkElement hit, string key, Action<bool> apply)
    {
        apply(IsFolded(key));
        hit.Tapped += (_, e) => { var f = !IsFolded(key); SetFolded(key, f); apply(f); e.Handled = true; };
    }

    /// <summary>Makes a sidebar section foldable: everything under its heading moves into one body that can be hidden.</summary>
    private static void MakeCollapsible(StackPanel sec, string key)
    {
        var body = new StackPanel { Spacing = sec.Spacing };
        while (sec.Children.Count > 1) { var c = sec.Children[1]; sec.Children.RemoveAt(1); body.Children.Add(c); }
        sec.Children.Add(body);
        var first = (FrameworkElement)sec.Children[0];
        var label = first as TextBlock ?? ((Grid)first).Children.OfType<TextBlock>().First();
        Foldable(label, key, folded => body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>Frosted or liquid look for the floating pieces: the search pill, the selection bar and the toast.</summary>
    public void ApplySurface()
    {
        Ui.Surface(SearchPill, 16); Ui.Surface(ActionBar, 22); Ui.Surface(Toast, 18);
    }

    public LibraryPage()
    {
        InitializeComponent();
        foreach (var (sec, key) in new (StackPanel, string)[] { (SecLabels, "labels"), (SecSpaces, "spaces"), (SecSources, "sources"), (SecDetails, "refine"),
                                                               (SecColours, "colours"), (SecMoods, "moods"), (SecFolders, "folders") })
            MakeCollapsible(sec, key);
        TileRepeater.ElementPrepared += (_, a) => Anim.In(a.Element, 160);
        Current = this;
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        BuildFiltersFlyout();
        ApplySections();
        ApplySurface();
        TileRepeater.ItemsSource = _tiles;
        Layout.Target = Settings.TileSize; Layout.Gap = Settings.TileGap;
        Layout.Mode = ParseMode(Settings.Layout);
        _timer.Tick += async (_, _) => await PollAsync();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _detailDebounce.Tick += async (_, _) => { _detailDebounce.Stop(); await ApplyDetailFieldsAsync(); };

        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, async () => await GoBackAsync(), true);
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, async () => await GoForwardAsync(), true);
        AddAccelerator(VirtualKey.A, VirtualKeyModifiers.Control, async () => await SelectAllAsync(), false);
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => { SearchBox.Focus(FocusState.Programmatic); return Task.CompletedTask; }, true);
        // Single keys (never while typing in a box): / search, F favourite, 1-7 colour label, 0 no label for the selected files.
        AddAccelerator((VirtualKey)191, VirtualKeyModifiers.None, () => { SearchBox.Focus(FocusState.Programmatic); return Task.CompletedTask; }, false);
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.None, () => { if (_sel.Count > 0) Favourite_Click(this, new RoutedEventArgs()); return Task.CompletedTask; }, false);
        for (var i = 0; i <= 7; i++)
        {
            var n = i;
            AddAccelerator((VirtualKey)((int)VirtualKey.Number0 + n), VirtualKeyModifiers.None, async () =>
            {
                if (_sel.Count == 0) return;
                await ApplyLabelAsync(_sel.ToList(), n == 0 ? null : LabelInfo.All[n - 1].Name);
            }, false);
        }
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => { FiltersBtn.Flyout?.ShowAt(FiltersBtn); return Task.CompletedTask; }, true);
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => { ClearSelection(); return Task.CompletedTask; }, false);
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, async () => await DeleteSelectedAsync(), false);
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, async () => await CopySelectedAsync(false), false);
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, async () => await CopySelectedAsync(true), false);
        AddAccelerator(VirtualKey.Enter, VirtualKeyModifiers.None, () => { OpenSelected(); return Task.CompletedTask; }, false);

        // The filters live in the app's left rail on every page; the buttons live in the shell's top bar while this page shows.
        BodyGrid.Children.Remove(FilterPanel);
        Host.Children.Remove(ToolbarRow);
        App.Window?.SetSidebar(FilterPanel);

        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += async (_, _) =>
        {
            App.Window?.SetActions(null);
            App.Window?.SetCenter(ToolbarRow);
            PushTitle();
            App.Window?.SyncNav();
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            _timer.Start();
            if (_ready) return;          // coming back from the viewer: everything is exactly where it was
            _ready = true;
            ApplyRequest();
            await RefreshFacetsAsync();
            await ReloadAsync();
        };
        Unloaded += (_, _) => { _timer.Stop(); _cts.Cancel(); };
    }

    private bool _ready, _pending;

    // The toolbar band floats over the tiles, so everything beneath starts just below it, however tall it gets
    // (active filter chips can wrap onto more lines).
    private double _bandH = 88;

    /// <summary>Tiles start below the toolbar band and, when it shows, the suggestions strip.</summary>
    private void UpdateInsets()
    {
        var strip = SuggestStrip.Visibility == Visibility.Visible ? SuggestStrip.ActualHeight + 8 : 0;
        if (MomentStrip.Visibility == Visibility.Visible) strip = Math.Max(strip, MomentStrip.ActualHeight + 8);
        SuggestStrip.Margin = MomentStrip.Margin = new Thickness(0, _bandH + 4, 0, 0);
        TileRepeater.Margin = new Thickness(0, _bandH + 4 + strip, 0, 24);
    }

    private void SuggestStrip_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateInsets();

    private int _momentGen;

    /// <summary>For a text search: also look inside the analysed videos and sounds, and offer the best moments above the results.</summary>
    private async Task ShowMomentsAsync(IReadOnlyList<string> words)
    {
        var gen = ++_momentGen;
        var text = string.Join(" ", words.Where(w => !w.StartsWith("voice:")));
        if (text.Trim().Length < 2) { HideMoments(); return; }
        List<MomentHit> hits;
        try { hits = await Api.SearchMomentsAsync(text, 24); }
        catch (Exception ex) { App.Log("moments", ex, null); return; }
        if (gen != _momentGen) return;
        // Only the clearly best ones: close to the strongest match, one or two per file.
        var best = hits.Count > 0 ? hits.Max(h => h.Score) : 0;
        var shown = hits.Where(h => best >= 0.15 && h.Score >= best * 0.85).GroupBy(h => h.FileId).SelectMany(g => g.Take(2)).Take(12).ToList();
        if (shown.Count == 0) { HideMoments(); return; }
        MomentRow.Children.Clear();
        foreach (var m in shown) MomentRow.Children.Add(MomentCard(m));
        MomentNote.Text = $"{shown.Count} found";
        MomentStrip.Visibility = Visibility.Visible;
        UpdateInsets();
    }

    private void HideMoments()
    {
        _momentGen++;
        MomentRow.Children.Clear();
        MomentStrip.Visibility = Visibility.Collapsed;
        UpdateInsets();
    }

    private FrameworkElement MomentCard(MomentHit m)
    {
        var mid = (m.Start + m.End) / 2;
        var img = new Grid { Width = 190, Height = 110, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillBrush") };
        img.Children.Add(new Image { Source = new BitmapImage(Api.MomentFrameUri(m.FileId, mid)) { DecodePixelWidth = 380 }, Stretch = Stretch.UniformToFill });
        img.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6), Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0, 0, 0)), Child = new TextBlock { Text = m.TimeText, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } });
        var card = new StackPanel { Spacing = 5, Width = 190, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        card.Children.Add(img);
        card.Children.Add(new TextBlock { Text = m.File.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        card.Tapped += (_, _) => Frame.Navigate(typeof(ViewerPage), new ViewerRequest(m.File, [m.File], m.Start, m.End));
        card.PointerEntered += (_, _) => Glow.On(card, 6, false);
        card.PointerExited += (_, _) => Glow.Off(card, false);
        ToolTipService.SetToolTip(card, "Open at this moment");
        return card;
    }

    private void TopBand_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = Math.Ceiling(e.NewSize.Height + TopBand.Margin.Top);
        _bandH = h;
        UpdateInsets();
        Detail.Margin = new Thickness(16, h + 4, 0, 16);
        Toast.Margin = new Thickness(0, h + 8, 0, 0);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ShowAgainAsync();
    }

    /// <summary>Called whenever the Library is brought to the front: applies a waiting request or a filter chosen from the sidebar.</summary>
    public async Task ShowAgainAsync()
    {
        if (_ready && Request is not null)
        {
            _back.Push(Snapshot());
            ApplyRequest();
            await ReloadAsync();
        }
        else if (_ready && _pending)
        {
            _pending = false;
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            await ReloadAsync();
        }
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers mods, Func<Task> action, bool evenInTextBox)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
        a.Invoked += async (_, e) =>
        {
            if (!evenInTextBox && FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            e.Handled = true;
            await action();
        };
        a.ScopeOwner = null;
        KeyboardAccelerators.Add(a);
    }

    private static TileMode ParseMode(string s) => s switch { "rows" => TileMode.Rows, "contact" => TileMode.Contact, "story" => TileMode.Story, _ => TileMode.Masonry };

    private void ApplyRequest()
    {
        if (Request is not { } r) return;
        Request = null;
        _q = r.Query.Clone();
        _tags = r.Tags ?? [];
        _title = r.Title;
        _similarTo = r.Similar; _similarName = r.SimilarName; _imagePath = null;
        SyncDetailFields();
    }

    /// <summary>Fills the sidebar's filters without showing the page (the shell builds the Library first, even for another page).</summary>
    public async Task PrimeAsync() { if (!_ready) await RefreshFacetsAsync(); }

    // ------------------------------------------------------------------ loading
    private async Task RefreshFacetsAsync()
    {
        try
        {
            _stats = await Api.GetStatsAsync();
            _roots = await Api.GetRootsAsync();
            _colors = await Api.GetColorsAsync();
            _sources = await Api.GetSourcesAsync();
            _moods = await Api.GetMoodsAsync();
            _projects = await Api.GetProjectsAsync();
            _tagList = (await Api.GetTagsAsync()).Tags;
            _labelList = (await Api.GetLabelsAsync()).Labels;
        }
        catch (Exception ex) { FileMind.Core.Diag.Log("Library.panels", ex); }
        RebuildPanels();
    }

    /// <summary>A display preference changed (for example the smallest size shown): the next visit reloads, and so does this one if it is open.</summary>
    public void RefreshAfterPrefs() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        foreach (var t in _tiles) t.PropertyChanged -= null!;
        _tiles.Clear();
        _offset = 0;
        _more = true;
        _loading = false;
        _lastChecked = -1;
        _imageHits = [];
        SetFocus(null);
        EmptyCard.Visibility = FirstRun.Visibility = Visibility.Collapsed;
        RebuildPanels();
        _ = RefreshSuggestionsAsync(_cts.Token);
        await LoadMoreAsync(_cts.Token);
    }

    // ------------------------------------------------------------------ suggestions for an open space
    private List<FileItem> _suggested = [];
    private static bool _suggestCollapsed;

    private void HideSuggest()
    {
        _suggested = [];
        SuggestRow.Children.Clear();
        SuggestStrip.Visibility = Visibility.Collapsed;
        UpdateInsets();
    }

    private async Task RefreshSuggestionsAsync(CancellationToken ct)
    {
        var pid = _q.Project;
        if (pid is null || !IsPlainBrowse || _projects.FirstOrDefault(p => p.Id == pid)?.Smart == true) { HideSuggest(); return; }
        try
        {
            var list = await Api.GetSpaceSuggestionsAsync(pid.Value, 12, ct);
            if (ct.IsCancellationRequested || _q.Project != pid) return;
            _suggested = list;
            BuildSuggest();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("suggest", ex, ex.Message); if (!ct.IsCancellationRequested) HideSuggest(); }
    }

    private void BuildSuggest()
    {
        SuggestRow.Children.Clear();
        if (_suggested.Count == 0) { HideSuggest(); return; }
        SuggestCount.Text = _suggested.Count.ToString("N0");
        foreach (var f in _suggested) SuggestRow.Children.Add(SuggestTile(f));
        SuggestScroll.Visibility = SuggestAllBtn.Visibility = _suggestCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SuggestChevron.Kind = _suggestCollapsed ? "chevdown" : "chevup";
        SuggestStrip.Visibility = Visibility.Visible;
        UpdateInsets();
    }

    private FrameworkElement SuggestTile(FileItem f)
    {
        const double S = 78;
        var g = new Grid { Width = S, Height = S, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillStrongBrush") };
        if (f.HasThumb) g.Children.Add(new Image { Source = new BitmapImage(Api.ThumbUri(f.Id)) { DecodePixelHeight = 160 }, Stretch = Stretch.UniformToFill });
        else g.Children.Add(new LineIcon { Kind = f.Kind switch { "audio" => "music", "video" => "video", _ => "file" }, Size = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        g.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 34, VerticalAlignment = VerticalAlignment.Bottom, Fill = Ui.Brush("TileScrimBrush"), IsHitTestVisible = false });
        Button Round(string icon, Brush? bg, string tip, HorizontalAlignment h)
        {
            var b = new Button { Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(12), Background = bg ?? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                BorderThickness = new Thickness(0), HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5),
                Content = new LineIcon { Kind = icon, Size = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } };
            ToolTipService.SetToolTip(b, tip);
            AutomationProperties.SetName(b, tip);
            return b;
        }
        var yes = Round("check", Ui.Brush("AccentBrush"), "Add to this space", HorizontalAlignment.Left);
        var no = Round("close", null, "Not this one", HorizontalAlignment.Right);
        yes.Click += async (_, _) => await AcceptSuggestionsAsync([f.Id]);
        no.Click += async (_, _) => await DismissSuggestionAsync(f.Id);
        g.Children.Add(yes); g.Children.Add(no);
        ToolTipService.SetToolTip(g, f.Name);
        return g;
    }

    private async Task AcceptSuggestionsAsync(IReadOnlyList<int> ids)
    {
        if (_q.Project is not { } pid || ids.Count == 0) return;
        try
        {
            await Api.AddToProjectAsync(pid, ids);
            ShowToast(ids.Count == 1 ? "Added to this space." : $"Added {ids.Count:N0} files to this space.");
            await RefreshFacetsAsync();
            await ReloadAsync();          // the tiles and the suggestions both refresh
        }
        catch (Exception ex) { App.Log("suggest-add", ex, ex.Message); ShowToast("Couldn't add that."); }
    }

    private async Task DismissSuggestionAsync(int id)
    {
        if (_q.Project is not { } pid) return;
        try
        {
            await Api.DismissSuggestionsAsync(pid, [id]);
            await RefreshSuggestionsAsync(_cts.Token);      // a new one takes its place if there is another good match
        }
        catch (Exception ex) { App.Log("suggest-dismiss", ex, ex.Message); ShowToast("Couldn't do that."); }
    }

    private async void SuggestAll_Click(object sender, RoutedEventArgs e) => await AcceptSuggestionsAsync(_suggested.Select(f => f.Id).ToList());

    private void SuggestToggle_Click(object sender, RoutedEventArgs e) { _suggestCollapsed = !_suggestCollapsed; BuildSuggest(); }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_loading || !_more) return;
        _loading = true;
        if (_offset == 0 && _tags.Count == 0) HideMoments();
        Busy.IsActive = _tiles.Count == 0;
        try
        {
            List<FileItem> items;
            if (_imagePath is not null)
            {
                items = await Api.SearchByImageAsync(_imagePath, _q, RefineWords(), null, ct);
                _more = false;
            }
            else if (_similarTo > 0)
            {
                items = await Api.SimilarAsync(_similarTo, _q, 200, RefineWords(), null, ct);
                _more = false;
            }
            else if (_tags.Count > 0)
            {
                items = await Api.SearchAsync(_tags.Select(t => t.Query), _q, PageSize, _offset, ct);
                _more = items.Count == PageSize;
                if (_offset == 0) _ = ShowMomentsAsync(_tags.Select(t => t.Query).ToList());
            }
            else
            {
                var page = await Api.GetFilesAsync(_q, PageSize, _offset, ct);
                items = page.Files;
                _total = page.Total;
                _more = _offset + items.Count < page.Total;
            }
            if (ct.IsCancellationRequested) return;
            foreach (var item in items)
            {
                var vm = new TileVm(item, Api) { AnyChecked = _sel.Count > 0, AlwaysName = Layout.Mode == TileMode.Story || Settings.ShowNames, Checked = _sel.Contains(item.Id) };
                _tiles.Add(vm);
            }
            _offset += items.Count;
            UpdateTitle();
            ShowEmptyIfNeeded();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _more = false;
            ShowEmpty(e is EngineException ? "That didn't work" : "Can't reach the engine", e.Message, false);
        }
        finally
        {
            if (!ct.IsCancellationRequested) { _loading = false; Busy.IsActive = false; }
        }
    }

    private void TileScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_more || _loading) return;
        if (TileScroll.ExtentHeight - TileScroll.VerticalOffset - TileScroll.ViewportHeight < 1500)
            _ = LoadMoreAsync(_cts.Token);
    }

    /// <summary>With a reference picture, file or sound active, the words typed are not a separate search: they refine it.</summary>
    private string RefineWords() => _imagePath is null && _similarTo <= 0 ? "" : string.Join(" ", _tags.Select(t => t.Query).Where(q => !q.StartsWith("voice:")));

    private string RefineSuffix() => RefineWords().Length > 0 ? " + " + string.Join(" + ", _tags.Select(t => $"“{t.Label}”")) : "";

    private void UpdateTitle()
    {
        if (_imagePath is not null) { SetPageTitle("Looks (or sounds) like your file"); Summary.Text = $"{_tiles.Count:N0} closest matches" + RefineSuffix(); return; }
        if (_similarTo > 0) { SetPageTitle("Similar files"); Summary.Text = $"More like “{_similarName}”" + RefineSuffix(); return; }
        if (_tags.Count > 0) { SetPageTitle("Results"); Summary.Text = "Best matches for " + string.Join(" + ", _tags.Select(t => $"“{t.Label}”")); return; }
        string title = _title;
        if (title.Length == 0)
        {
            if (_q.Project is { } pid) title = _projects.FirstOrDefault(p => p.Id == pid)?.Name ?? "Space";
            else if (_q.Root is { } rid) title = Path.GetFileName(_roots.FirstOrDefault(r => r.Id == rid)?.Path.TrimEnd('\\', '/') ?? "") is { Length: > 0 } n ? n : "Folder";
            else if (_q.Unassigned) title = "Unsorted";
            else if (_q.Source is { } s) title = _sources.FirstOrDefault(x => x.Key == s)?.Label ?? "Source";
            else title = "Library";
        }
        SetPageTitle(title);
        if (_q.IsNarrowed) Summary.Text = $"{_total:N0} files";
        else
        {
            var parts = new List<string>();
            void Add(string key, string word) { if (_stats.Kinds.TryGetValue(key, out var n) && n > 0) parts.Add($"{n:N0} {word}"); }
            Add("image", "photos"); Add("video", "videos"); Add("audio", "sounds");
            Summary.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing here yet";
        }
    }

    private string _pageTitle = "Library";

    private void SetPageTitle(string title) { _pageTitle = title; PushTitle(); }

    /// <summary>The title lives in the shell's top bar; it is only written there while the Library is the page on screen.</summary>
    private void PushTitle()
    {
        PageTitle.Text = _pageTitle;
        if (!IsLoaded || App.Window is not { } w) return;
        PageTitle.Text = _pageTitle;
        w.SyncNav();
    }

    private void ShowEmptyIfNeeded()
    {
        if (_tiles.Count > 0) { EmptyCard.Visibility = FirstRun.Visibility = Visibility.Collapsed; return; }
        if (_roots.Count == 0) ShowEmpty("Add a folder to begin", "Choose a folder of photos, videos or sounds. File Mind reads it on this PC and nothing is uploaded.", true);
        else if (_wasIndexing) ShowEmpty("Reading your files…", "Files appear here as soon as they have been read.", false);
        else if (_tags.Count > 0 || _similarTo > 0 || _imagePath is not null) ShowEmpty("Nothing matched", "Try describing it differently, or loosen a filter.", false);
        else ShowEmpty("No files like that yet", "Clear a filter, or add another folder.", false);
    }

    private void ShowEmpty(string title, string body, bool offerAdd)
    {
        if (offerAdd) { ShowFirstRun(title, body); return; }
        FirstRun.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyAction.Visibility = Visibility.Collapsed;
        EmptyCard.Visibility = Visibility.Visible;
    }

    public void DevFirstRun() { TileRepeater.Visibility = Visibility.Collapsed; ShowFirstRun("Add a folder to begin", "Choose a folder of photos, videos or sounds. File Mind reads it on this PC and nothing is uploaded."); }

    /// <summary>Nothing added yet: the same deep gradient and breathing mark as the opening screen, one round button.</summary>
    private void ShowFirstRun(string title, string body)
    {
        EmptyCard.Visibility = Visibility.Collapsed;
        if (FirstRun.Children.Count == 0 || FirstRun.Tag is not string t || t != FileMind.ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Key + AppRuntime.Current.Settings.Accent)
        {
            FirstRun.Children.Clear();
            FirstRun.Tag = FileMind.ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Key + AppRuntime.Current.Settings.Accent;
            FirstRun.Children.Add(Hero.Backdrop(false));
            var col = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 460, Margin = new Thickness(0, 0, 0, 30) };
            col.Children.Add(Hero.Mark(104));
            col.Children.Add(new TextBlock { Text = title, Style = Ui.Style("TitleText"), FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
            col.Children.Add(new TextBlock { Text = body, Style = Ui.Style("SecondaryText"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var go = Hero.RoundButton("Add a folder");
            go.Margin = new Thickness(0, 18, 0, 0);
            go.Click += AddFolder_Click;
            col.Children.Add(go);
            FirstRun.Children.Add(col);
        }
        if (FirstRun.Visibility != Visibility.Visible) { FirstRun.Visibility = Visibility.Visible; Anim.In(FirstRun, 300, 8); }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ indexing status
    private async Task PollAsync()
    {
        try
        {
            var s = await Api.GetStatusAsync();
            IndexStrip.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
            if (s.Running)
            {
                IndexText.Text = s.Total > 0 ? $"Reading {s.Done:N0} of {s.Total:N0}" : "Looking for files…";
                IndexBar.IsIndeterminate = s.Total <= 0;
                if (s.Total > 0) IndexBar.Value = Math.Clamp((double)s.Done / s.Total, 0, 1);
                if (_tiles.Count == 0 && s.Done > 0 && IsPlainBrowse) await ReloadAsync();
            }
            else if (_wasIndexing)
            {
                await RefreshFacetsAsync();
                if (IsPlainBrowse) await ReloadAsync();
            }
            _wasIndexing = s.Running;
        }
        catch (Exception ex) { FileMind.Core.Diag.Log("Library.indexing-poll", ex); }
    }

    private bool IsPlainBrowse => _tags.Count == 0 && _similarTo == 0 && _imagePath is null;

    private async void PauseIndex_Click(object sender, RoutedEventArgs e)
    {
        try { await Api.StopIndexingAsync(); ShowToast("Paused. Reading resumes next time you add or re-read a folder."); } catch (Exception ex) { ShowToast(ex.Message); }
    }

    // ------------------------------------------------------------------ navigation (back / forward)
    private NavState Snapshot() => new(_q.Clone(), [.. _tags], _similarTo, _similarName, _imagePath, _title);

    private void Restore(NavState s)
    {
        _q = s.Q.Clone(); _tags = [.. s.Tags]; _similarTo = s.Similar; _similarName = s.SimilarName; _imagePath = s.ImagePath; _title = s.Title;
        SyncDetailFields();
    }

    /// <summary>Every change of what the library shows goes through here so Back and Forward work.</summary>
    private async Task GoAsync(Action change)
    {
        if (App.Window is { } w && !IsLoaded) { _back.Push(Snapshot()); _fwd.Clear(); change(); _pending = true; w.EnsureLibrary(); return; }
        _back.Push(Snapshot());
        _fwd.Clear();
        change();
        await ReloadAsync();
    }

    public bool CanStepBack => _back.Count > 0;
    public bool CanStepForward => _fwd.Count > 0;
    public Task StepBackAsync() => GoBackAsync();
    public Task StepForwardAsync() => GoForwardAsync();

    private async Task GoBackAsync()
    {
        if (_back.Count == 0) return;
        _fwd.Push(Snapshot());
        Restore(_back.Pop());
        await ReloadAsync();
    }

    private async Task GoForwardAsync()
    {
        if (_fwd.Count == 0) return;
        _back.Push(Snapshot());
        Restore(_fwd.Pop());
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ search
    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (e.Key == VirtualKey.Enter && text.Length > 0)
        {
            e.Handled = true;
            SearchBox.Text = "";
            await ApplySmartAsync(text);   // "4K vertical clips of a car": the details become Refine filters, "car" a search tag
        }
        else if (e.Key == VirtualKey.Back && SearchBox.Text.Length == 0 && _tags.Count > 0)
        {
            await GoAsync(() => _tags.RemoveAt(_tags.Count - 1));
        }
        else if (e.Key == VirtualKey.Escape && (_tags.Count > 0 || _similarTo > 0 || _imagePath is not null))
        {
            await GoAsync(() => { _tags.Clear(); _similarTo = 0; _imagePath = null; });
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SearchBox_Focus(object sender, RoutedEventArgs e)
    {
        var on = SearchBox.FocusState != FocusState.Unfocused;
        SearchPill.BorderBrush = Ui.Brush(on ? "AccentBrush" : "FieldBorderBrush");
    }

    /// <summary>Screenshot helper only: the first photo as a reference, with words added.</summary>
    public async Task DevReferenceAsync()
    {
        var first = _tiles.FirstOrDefault(t => t.Item.Kind == "image") ?? _tiles.FirstOrDefault();
        if (first is null) return;
        await GoAsync(() => { _similarTo = first.Item.Id; _similarName = first.Name; _tags = [("at night", "at night")]; _title = ""; });
    }

    private bool _listening;

    private void UpdateHint() => SearchHint.Text = _listening ? "Listening… say what you are looking for"
        : _imagePath is not null || _similarTo > 0 ? "Add words to refine…" : "Search your files…";

    /// <summary>The reference as a small chip at the left of the pill: its thumbnail, and × to drop it.</summary>
    private FrameworkElement? ReferenceChip()
    {
        if (_imagePath is null && _similarTo <= 0) return null;
        FrameworkElement thumb;
        try
        {
            string[] pics = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".avif"];
            if (_similarTo > 0) thumb = new Image { Source = new BitmapImage(Api.ThumbUri(_similarTo)) { DecodePixelHeight = 44 }, Stretch = Stretch.UniformToFill };
            else if (pics.Contains(Path.GetExtension(_imagePath!).ToLowerInvariant())) thumb = new Image { Source = new BitmapImage(new Uri(_imagePath!)) { DecodePixelHeight = 44 }, Stretch = Stretch.UniformToFill };
            else thumb = new LineIcon { Kind = "sound", Size = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        catch (Exception) { thumb = new LineIcon { Kind = "search", Size = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        var box = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Child = thumb, Background = Ui.Brush("GlassFillStrongBrush"), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        row.Children.Add(new LineIcon { Kind = "close", Size = 10, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Style = Ui.Style("GlassButton"), Padding = new Thickness(3, 2, 8, 2), CornerRadius = new CornerRadius(7), Background = Ui.Brush("AccentTintBrush"), Height = 28 };
        ToolTipService.SetToolTip(b, "Remove the reference picture");
        AutomationProperties.SetName(b, "Remove the reference picture");
        b.Click += async (_, _) => await GoAsync(() => { _similarTo = 0; _imagePath = null; });
        return b;
    }

    private void RebuildTags()
    {
        QueryTags.Children.Clear();
        if (ReferenceChip() is { } refChip) QueryTags.Children.Add(refChip);
        UpdateHint();
        for (var i = 0; i < _tags.Count; i++)
        {
            var index = i;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(new TextBlock { Text = _tags[i].Label, FontSize = 12, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new LineIcon { Kind = "close", Size = 11 });
            var b = new Button { Content = label, Style = Ui.Style("GlassButton"), Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(5), Background = Ui.Brush("AccentTintBrush") };
            b.Click += async (_, _) => await GoAsync(() => _tags.RemoveAt(index));
            QueryTags.Children.Add(b);
        }
    }

    /// <summary>Lets Discover jump straight to a search box ready for typing.</summary>
    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    public async void ImageSearch_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".avif", ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mp3", ".wav", ".m4a", ".flac", ".ogg" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await GoAsync(() => { _imagePath = file.Path; _tags.Clear(); _similarTo = 0; _title = ""; });
    }


    // ------------------------------------------------------------------ voice, and dropping files in
    private VoiceRecorder? _voice;
    private readonly DispatcherTimer _voiceStop = new() { Interval = TimeSpan.FromSeconds(12) };

    /// <summary>Tap to talk, tap again (or wait twelve seconds) to search. The sound itself becomes the query.</summary>
    private async void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is { IsRecording: true }) { await FinishVoiceAsync(); return; }
        try
        {
            _voice = new VoiceRecorder();
            await _voice.StartAsync();
            VoiceBtn.Background = Ui.Brush("AccentFillBrush");
            ToolTipService.SetToolTip(VoiceBtn, "Listening… tap to search");
            _listening = true; UpdateHint();
            _voiceStop.Tick -= VoiceStop_Tick; _voiceStop.Tick += VoiceStop_Tick; _voiceStop.Start();
        }
        catch (Exception ex)
        {
            App.Log("voice", ex, ex.Message);
            _voice?.Dispose(); _voice = null;
            ShowToast("Couldn't reach a microphone. Check Settings > Privacy > Microphone, and let desktop apps use it.");
        }
    }

    private async void VoiceStop_Tick(object? sender, object e) { _voiceStop.Stop(); await FinishVoiceAsync(); }

    private async Task FinishVoiceAsync()
    {
        _voiceStop.Stop();
        VoiceBtn.ClearValue(Control.BackgroundProperty);
        ToolTipService.SetToolTip(VoiceBtn, "Search by voice");
        _listening = false; UpdateHint();
        string? path = null;
        try { path = _voice is null ? null : await _voice.StopAsync(); } catch (Exception ex) { App.Log("voice-stop", ex, ex.Message); }
        _voice?.Dispose(); _voice = null;
        if (path is null) return;
        try
        {
            Busy.IsActive = true;
            var v = await Api.VoiceAsync(path);
            var label = v.Hint.Count > 0 ? "🎙 " + string.Join(", ", v.Hint.Take(2)) : "🎙 voice search";
            await GoAsync(() => { _tags.Add((label, "voice:" + v.Id)); _similarTo = 0; _imagePath = null; _title = ""; });
        }
        catch (Exception ex) { App.Log("voice-search", ex, ex.Message); ShowToast(ex.Message.Length is > 0 and < 140 ? ex.Message : "Couldn't search with that recording."); }
        finally { Busy.IsActive = false; try { File.Delete(path); } catch (Exception) { } }
    }

    private void Host_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Find things like this";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    /// <summary>Drop a picture, clip or sound to find what looks or sounds like it; a text file or snippet becomes a search; a folder is added.</summary>
    private async void Host_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.OfType<StorageFolder>().FirstOrDefault() is { } folder)
                {
                    await Api.AddRootAsync(folder.Path);
                    ShowToast($"Added “{folder.Name}” — reading it now");
                    await RefreshFacetsAsync();
                    return;
                }
                if (items.OfType<StorageFile>().FirstOrDefault() is not { } file) return;
                var ext = file.FileType.ToLowerInvariant();
                string[] text = [".txt", ".md", ".csv", ".log", ".srt", ".vtt", ".tsv"];
                if (text.Contains(ext))
                {
                    var words = (await FileIO.ReadTextAsync(file)).Trim();
                    words = words.Length > 280 ? words[..280] : words;
                    if (words.Length == 0) { ShowToast("That file is empty."); return; }
                    await GoAsync(() => { _tags.Add((file.DisplayName, words)); _similarTo = 0; _imagePath = null; _title = ""; });
                    return;
                }
                string[] media = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".avif", ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mpeg", ".mpg", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".opus"];
                if (!media.Contains(ext)) { ShowToast("File Mind can search by pictures, clips, sounds and text files."); return; }
                await GoAsync(() => { _imagePath = file.Path; _tags.Clear(); _similarTo = 0; _title = ""; });
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var words = (await e.DataView.GetTextAsync()).Trim();
                if (words.Length == 0) return;
                words = words.Length > 280 ? words[..280] : words;
                await GoAsync(() => { _tags.Add((words.Length > 28 ? words[..28] + "…" : words, words)); _similarTo = 0; _imagePath = null; _title = ""; });
            }
        }
        catch (Exception ex) { App.Log("drop", ex, ex.Message); ShowToast("Couldn't search with that."); }
    }
    // ------------------------------------------------------------------ toolbar
    private static readonly Dictionary<string, string> SortNames = new() { ["mtime"] = "Newest", ["name"] = "Name", ["size"] = "Largest", ["color"] = "Rainbow" };

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        var sort = (string)((MenuFlyoutItem)sender).Tag;
        await GoAsync(() => _q.Sort = sort);
    }

    private bool _allSources;

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((MenuFlyoutItem)sender).Tag;
        Settings.Layout = mode;
        Settings.Save(AppRuntime.Current.Paths);
        Layout.Mode = ParseMode(mode);
        foreach (var t in _tiles) t.AlwaysName = Layout.Mode == TileMode.Story || Settings.ShowNames;
    }

    /// <summary>Re-reads layout, tile size, spacing and file-name settings after Settings changed them.</summary>
    public void ApplyLook()
    {
        Layout.Target = Settings.TileSize; Layout.Gap = Settings.TileGap;
        Layout.Mode = ParseMode(Settings.Layout);
        foreach (var t in _tiles) t.AlwaysName = Layout.Mode == TileMode.Story || Settings.ShowNames;
    }

    private void SizeDown_Click(object sender, RoutedEventArgs e) => SetSize(Settings.TileSize - 40);
    private void SizeUp_Click(object sender, RoutedEventArgs e) => SetSize(Settings.TileSize + 40);

    private void SetSize(double size)
    {
        Settings.TileSize = Math.Clamp(size, 140, 560);
        Settings.Save(AppRuntime.Current.Paths);
        Layout.Target = Settings.TileSize; Layout.Gap = Settings.TileGap;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            await Api.AddRootAsync(folder.Path);
            _wasIndexing = true;
            await RefreshFacetsAsync();
            await ReloadAsync();
            ShowToast("Reading that folder now.");
        }
        catch (Exception ex) { ShowEmpty("Couldn't add that folder", ex.Message, false); }
    }

    // ------------------------------------------------------------------ filter panel
    private FrameworkElement Chip(string text, bool on, Func<Task> click, string? dot = null, int? count = null, string? tooltip = null, bool compact = false)
    {
        // A cell of a two-column grid: dot and name on the left, the count on the right, all inside one equal-width chip.
        tooltip ??= text;
        text = Short(text);
        var row = new Grid { ColumnSpacing = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = dot is null ? new GridLength(0) : GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (dot is not null) row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Brush(dot), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        if (count is { } n) { var ct = new TextBlock { Text = n.ToString("N0"), FontSize = 11, Foreground = Ui.Brush("GlassTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(ct, 2); row.Children.Add(ct); }
        var b = new ToggleButton { Content = row, IsChecked = on, Style = Ui.Style("GlassChip"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 0, 8, 0), MinHeight = 28, Height = 28, CornerRadius = new CornerRadius(6) };
        b.Click += async (_, _) => await click();
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    // Long names get a shorter form so a chip in the two-column grid never has to cut a word; the tooltip keeps the full name.
    private static readonly Dictionary<string, string> ShortNames = new()
    {
        ["HD or better"] = "HD+", ["Widescreen"] = "Wide", ["Under 30 s"] = "< 30 s", ["Over 1 min"] = "> 1 min", ["Over 20 MB"] = "> 20 MB", ["Over 500 MB"] = "> 500 MB",
        ["This week"] = "Week", ["This month"] = "Month", ["This year"] = "Year", ["Nano Banana / Gemini"] = "Nano Banana", ["Google Flow / Veo"] = "Flow / Veo",
        ["Hailuo / MiniMax"] = "Hailuo", ["Camera & phone"] = "Camera", ["creation_… exports"] = "creation_…", ["Midjourney"] = "Midjourney",
    };
    private static string Short(string t) => ShortNames.GetValueOrDefault(t, t);

    private static Windows.UI.Color Hex(string h)
    {
        h = h.TrimStart('#');
        return Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    private static LinearGradientBrush Gradient(double angle, params string[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.2), EndPoint = new Windows.Foundation.Point(1, 0.8) };
        for (var i = 0; i < stops.Length; i++) g.GradientStops.Add(new GradientStop { Color = Hex(stops[i]), Offset = stops.Length == 1 ? 0 : i / (double)(stops.Length - 1) });
        return g;
    }

    /// <summary>A look or mood as a chip tinted with the look itself; the text is dark or light so it stays readable.</summary>
    private FrameworkElement MoodChip(MoodEntry m, bool on)
    {
        var (brush, text) = m.Key switch
        {
            "pastel" => (Gradient(0, "#FFD6E8", "#D6E6FF", "#D9F5E4"), "#2A2A33"),
            "neon" => (Gradient(0, "#FF2BD6", "#7A3CFF", "#19E6FF"), "#FFFFFF"),
            "golden" => (Gradient(0, "#FFE29A", "#FFB347", "#F27D2B"), "#3A2108"),
            "moody" => (Gradient(0, "#1B2A44", "#10131C", "#262A33"), "#F2F3F7"),
            "bw" => (Gradient(0, "#8E8E8E", "#5A5A5A", "#2B2B2B"), "#FFFFFF"),
            "vivid" => (Gradient(0, "#FF3B6B", "#FFB000", "#2CD36F", "#2D7BFF"), "#FFFFFF"),
            "minimal" => (Gradient(0, "#FFFFFF", "#F1F1EE"), "#2A2A2E"),
            "earthy" => (Gradient(0, "#8A8F4B", "#B88A5A", "#B5573A"), "#FFFFFF"),
            "teal_orange" => (Gradient(0, "#0E7C86", "#2B8E92", "#F08A3C"), "#FFFFFF"),
            "dreamy" => (Gradient(0, "#CDB8F5", "#F3C4E4", "#FBE3F1"), "#2E2540"),
            _ => (Gradient(0, "#DDDDDD", "#BBBBBB"), "#222222"),
        };
        var label = new TextBlock { Text = m.Label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Hex(text)), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        var b = new ToggleButton
        {
            Content = label, IsChecked = on, Style = Ui.Style("GlassChip"), Background = brush, BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 0, 10, 0), MinHeight = 30, Height = 30, CornerRadius = new CornerRadius(7),
        };
        b.Click += async (_, _) => await ToggleMood(m);
        ToolTipService.SetToolTip(b, m.Label);
        return b;
    }

    /// <summary>A file-type filter as an icon tile: icon above, name and count below.</summary>
    private FrameworkElement TypeTile(string label, string icon, bool on, string kind, int count)
    {
        var col = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(new LineIcon { Kind = icon, Size = 18, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(new TextBlock { Text = count.ToString("N0"), FontSize = 10.5, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center });
        var b = new ToggleButton { Content = col, IsChecked = on, Style = Ui.Style("GlassChip"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 8, 4, 7), CornerRadius = new CornerRadius(7) };
        b.Click += async (_, _) => await SetKind(kind);
        return b;
    }

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8) return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return new SolidColorBrush(Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16)));
    }

    private bool _spacesAll;

    /// <summary>The Sources chips: the sources in view, the active one first, six at first.</summary>
    private void BuildSourceChips()
    {
        var srcAll = _sources.Where(x => x.Count > 0 || _q.Source == x.Key).OrderByDescending(x => x.Key == _q.Source).ThenByDescending(x => x.Count).ToList();
        var srcShown = _allSources ? srcAll : srcAll.Take(6).ToList();
        var srcChips = srcShown.Select(x =>
            Chip(x.Label, _q.Source == x.Key, () => GoAsync(() => { _q.Source = _q.Source == x.Key ? null : x.Key; _title = ""; }), x.Color, x.Count, x.Label)).ToList();
        if (srcAll.Count > 6)
        {
            var more = new HyperlinkButton { Content = _allSources ? "Show fewer" : $"+{srcAll.Count - 6} more", FontSize = 12, Padding = new Thickness(6, 4, 6, 4) };
            more.Click += (_, _) => { _allSources = !_allSources; RebuildPanels(); };
            srcChips.Add(more);
        }
        var byName = new HyperlinkButton { FontSize = 12, Padding = new Thickness(6, 4, 6, 4), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new LineIcon { Kind = "text", Size = 12 }, new TextBlock { Text = "By file name" } } } };
        byName.Click += NameGroups_Click;
        srcChips.Add(byName);
        SourceChips.ItemsSource = srcChips;
    }

    private void RebuildPanels()
    {
        _syncing = true;
        try
        {
            RebuildTags();
            SortText.Text = SortNames.GetValueOrDefault(_q.Sort, "Newest");
            
            // types
            int Kind(string k) => _stats.Kinds.GetValueOrDefault(k);
            var types = new List<FrameworkElement>
            {
                TypeTile("All", "f-all", _q.Kind == "all", "all", _stats.Total),
                TypeTile("Photos", "f-photo", _q.Kind == "image", "image", Kind("image")),
                TypeTile("Videos", "f-video", _q.Kind == "video", "video", Kind("video")),
                TypeTile("Sounds", "f-music", _q.Kind == "audio", "audio", Kind("audio")),
            };
            foreach (var (key, label, icon) in new[] { ("doc", "Docs", "f-file"), ("text", "Text", "f-text"), ("code", "Code", "f-text") })
                if (Kind(key) > 0 || _q.Kind == key) types.Add(TypeTile(label, icon, _q.Kind == key, key, Kind(key)));
            TypeChips.ItemsSource = types;

            // colours: three rows of swatches (the last is a free colour picker). Hover changes nothing in size or layout.
            var shown = _colors.Where(c => c.Count > 0 || _q.Color == c.Key).ToList();
            var swatches = shown.Select(c =>
            {
                var selected = _q.Color == c.Key;
                var dot = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = Brush(c.Swatch), HorizontalAlignment = HorizontalAlignment.Center,
                    BorderThickness = new Thickness(2), BorderBrush = selected ? Ui.Brush("AccentBrush") : Ui.Brush("GlassStrokeBrush"),
                };
                ToolTipService.SetToolTip(dot, $"{c.Label} · {c.Count:N0}");
                dot.PointerEntered += (_, _) => { if (_q.Color != c.Key) dot.BorderBrush = Ui.Brush("GlassTextSecondaryBrush"); };
                dot.PointerExited += (_, _) => { if (_q.Color != c.Key) dot.BorderBrush = Ui.Brush("GlassStrokeBrush"); };
                dot.Tapped += async (_, _) => await GoAsync(() => { _q.Color = _q.Color == c.Key ? null : c.Key; _q.Near = null; });
                return (FrameworkElement)dot;
            }).ToList();
            swatches.Add(CustomColourSwatch());
            ColorDots.ItemsSource = swatches;

            MoodChips.ItemsSource = _moods.Select(m => MoodChip(m, _tags.Any(t => t.Query == m.Query))).ToList();

            BuildSourceChips();

            SpaceList.Children.Clear();
            var hiddenSpaces = 0;
            var shownSpaces = _projects.Select((p, i) => (p, i)).Where(x => _spacesAll || x.i < 5 || x.p.Id == _q.Project).Select(x => x.p).ToList();
            hiddenSpaces = _projects.Count - shownSpaces.Count;
            foreach (var p in shownSpaces)
            {
                var sp = p;
                SpaceList.Children.Add(SpaceRow(sp.Name, SpaceRing(sp.Id), sp.Count.ToString("N0"), _q.Project == sp.Id, () => GoAsync(() => { _q.Project = _q.Project == sp.Id ? null : sp.Id; _title = ""; })));
            }
            if (hiddenSpaces > 0 || (_spacesAll && _projects.Count > 5))
            {
                var more = new HyperlinkButton { Padding = new Thickness(8, 2, 8, 2), FontSize = 12, Content = _spacesAll ? "Show fewer" : $"Show {hiddenSpaces:N0} more" };
                more.Click += (_, _) => { _spacesAll = !_spacesAll; RebuildPanels(); };
                SpaceList.Children.Add(more);
            }
            SpaceList.Children.Add(SpaceRow("Unsorted", null, null, _q.Unassigned, () => GoAsync(() => { _q.Unassigned = !_q.Unassigned; _title = ""; })));

            // spaces picker in the action bar
            var picked = (SpacePick.SelectedItem as ComboBoxItem)?.Tag;
            SpacePick.Items.Clear();
            foreach (var p in _projects.Where(p => !p.Smart))
            {
                var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
                SpacePick.Items.Add(item);
                if (picked is int id && id == p.Id) SpacePick.SelectedItem = item;
            }

            // folders
            FolderList.Children.Clear();
            foreach (var r in _roots)
            {
                var root = r;
                var name = Path.GetFileName(r.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = r.Path;
                var chip = Chip(name, _q.Root == r.Id, () => GoAsync(() => { _q.Root = _q.Root == root.Id ? null : root.Id; _title = ""; }), null, r.Files, r.Path, true);
                var reindex = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(4), Content = new LineIcon { Kind = "refresh", Size = 12 } };
                ToolTipService.SetToolTip(reindex, "Read this folder again");
                reindex.Click += async (_, _) => { try { await Api.ReindexAsync(root.Id); _wasIndexing = true; ShowToast("Reading again…"); } catch (Exception ex) { ShowToast(ex.Message); } };
                var forget = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(4), Content = new LineIcon { Kind = "close", Size = 12 } };
                ToolTipService.SetToolTip(forget, "Forget this folder (your files are not touched)");
                forget.Click += async (_, _) => await ForgetFolderAsync(root);
                var line = new Grid { ColumnSpacing = 4 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                chip.HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetColumn(reindex, 1); Grid.SetColumn(forget, 2);
                line.Children.Add(chip); line.Children.Add(reindex); line.Children.Add(forget);
                FolderList.Children.Add(line);
            }

            BuildLabelChips();
            _ = FillRefineAsync();

            RebuildActiveChips();
        }
        finally { _syncing = false; }
    }

    /// <summary>The last swatch: a rainbow that opens a colour picker; the pick searches by "near this colour" like clicking the strip in the viewer.</summary>
    private FrameworkElement CustomColourSwatch()
    {
        var rainbow = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        var cols = new[] { "#FF3B30", "#FFB000", "#34C759", "#00C7BE", "#2D7BFF", "#AF52DE" };
        for (var i = 0; i < cols.Length; i++) rainbow.GradientStops.Add(new GradientStop { Color = Hex(cols[i]), Offset = i / (double)(cols.Length - 1) });
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring };
        var apply = new Button { Content = "Find this colour", Style = Ui.Style("GlassAccentButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Children = { picker, apply } };
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right };
        apply.Click += async (_, _) =>
        {
            flyout.Hide();
            var c = picker.Color;
            await GoAsync(() => { _q.Near = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; _q.Color = null; });
        };
        var dot = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = rainbow, HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(2), BorderBrush = _q.Near is not null ? Ui.Brush("AccentBrush") : Ui.Brush("GlassStrokeBrush"),
            Child = new LineIcon { Kind = "plus", Size = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(dot, "Pick any colour");
        dot.Tapped += (_, _) => flyout.ShowAt(dot);
        return dot;
    }

    private static readonly string[] RingColours = ["#F2552C", "#3B82F6", "#22A559", "#8B5CF6", "#F59E0B", "#EC4899", "#14B8A6", "#64748B"];
    internal static string SpaceRing(int id) => RingColours[Math.Abs(id) % RingColours.Length];

    /// <summary>One space in the sidebar: a small coloured ring, the name, a quiet count. Clicking opens it in the Library.</summary>
    private ToggleButton SpaceRow(string name, string? ring, string? count, bool on, Func<Task> click)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
        if (ring is null) { dot.BorderBrush = Ui.Brush("GlassTextTertiaryBrush"); dot.BorderThickness = new Thickness(1.5); }
        else dot.BorderBrush = Brush(ring);
        row.Children.Add(dot);
        var label = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        if (count is not null)
        {
            var ct = new TextBlock { Text = count, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextTertiaryBrush") };
            Grid.SetColumn(ct, 2); row.Children.Add(ct);
        }
        var b = new ToggleButton { Style = Ui.Style("RailItem"), Content = row, IsChecked = on, MinHeight = 30, Height = 30 };
        b.Click += async (_, _) => await click();
        return b;
    }

    private void RebuildActiveChips()
    {
        var chips = new List<FrameworkElement>();
        void Active(string text, Action remove)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new LineIcon { Kind = "close", Size = 9, VerticalAlignment = VerticalAlignment.Center  });
            var b = new Button { Content = row, Style = Ui.Style("GlassButton"), Padding = new Thickness(8, 0, 8, 0), Height = 22, CornerRadius = new CornerRadius(5), Background = Ui.Brush("AccentTintBrush") };
            b.Click += async (_, _) => await GoAsync(remove);
            chips.Add(b);
        }
        if (_q.Kind != "all") Active(_q.Kind switch { "image" => "Photos", "video" => "Videos", "audio" => "Sounds", "doc" => "Docs", "text" => "Text", _ => _q.Kind }, () => _q.Kind = "all");
        if (_q.Color is { } c) Active("Colour: " + (_colors.FirstOrDefault(x => x.Key == c)?.Label ?? c), () => _q.Color = null);
        if (_q.Near is { } n) Active("Near " + n, () => _q.Near = null);
        if (_q.Source is { } s) Active(_sources.FirstOrDefault(x => x.Key == s)?.Label ?? s, () => _q.Source = null);
        if (_q.Root is { } r) Active("Folder: " + (Path.GetFileName(_roots.FirstOrDefault(x => x.Id == r)?.Path.TrimEnd('\\', '/') ?? "") is { Length: > 0 } nm ? nm : "folder"), () => _q.Root = null);
        if (_q.Project is { } p) Active("Space: " + (_projects.FirstOrDefault(x => x.Id == p)?.Name ?? "space"), () => _q.Project = null);
        if (_q.Unassigned) Active("Unsorted", () => _q.Unassigned = false);
        if (_q.Pattern is { } pat) Active("Names like “" + pat + "”", () => _q.Pattern = null);
        if (_q.Discover is not null) Active(_title.Length > 0 ? _title : "Discover group", () => { _q.Discover = null; _title = ""; });
        foreach (var (k, v) in _q.DetailFilters()) Active(DetailLabel(k, v), () => ClearDetail(k));
        if (string.Equals(_q.Tag, TagInfo.Favourite, StringComparison.OrdinalIgnoreCase)) Active("\u2605 Favourites", () => _q.Tag = null);
        foreach (var label in ActiveLabels())
        {
            var l = label;
            Active("Label: " + (LabelInfo.All.FirstOrDefault(x => x.Name == l).Title ?? l), () => { var set = ActiveLabels(); set.Remove(l); _q.Label = set.Count == 0 ? null : string.Join(',', set); });
        }
        if (chips.Count >= 2)
        {
            var clear = new HyperlinkButton { Content = "Clear all", FontSize = 11.5, Padding = new Thickness(6, 0, 6, 0), Height = 22 };
            clear.Click += (_, _) => ClearFilters_Click(this, new RoutedEventArgs());
            chips.Add(clear);
        }
        ActiveChips.ItemsSource = chips;
        ClearFiltersBtn.Visibility = _q.IsNarrowed ? Visibility.Visible : Visibility.Collapsed;
        var count = FilterCount();
        FilterBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterBadgeText.Text = count.ToString();
    }

    private static string DetailLabel(string key, object v) => key switch
    {
        "after" => "From " + DateTimeOffset.FromUnixTimeSeconds((long)(double)v).LocalDateTime.ToString("d MMM yyyy"),
        "before" => "Until " + DateTimeOffset.FromUnixTimeSeconds((long)(double)v).LocalDateTime.ToString("d MMM yyyy"),
        "min_size" => $"Over {(long)v / 1e6:0.#} MB",
        "max_size" => $"Under {(long)v / 1e6:0.#} MB",
        "min_width" => $"Width ≥ {v}px",
        "min_height" => $"Height ≥ {v}px",
        "min_pixels" => (long)v >= 3840L * 2160 ? "4K or higher" : (long)v >= 1920L * 1080 ? "Full HD or higher" : $"≥ {(long)v / 1e6:0.#} MP",
        "min_duration" => $"Longer than {v} s",
        "max_duration" => $"Shorter than {v} s",
        "min_fps" => $"{v} fps or higher",
        "aspect" => char.ToUpper(((string)v)[0]) + ((string)v)[1..],
        "codec" => ((string)v).ToUpperInvariant(),
        _ => key,
    };

    private void ClearDetail(string key)
    {
        switch (key)
        {
            case "after": _q.After = null; break;
            case "before": _q.Before = null; break;
            case "min_size": _q.MinSize = null; break;
            case "max_size": _q.MaxSize = null; break;
            case "min_width": _q.MinWidth = null; break;
            case "min_height": _q.MinHeight = null; break;
            case "min_pixels": _q.MinPixels = null; break;
            case "min_duration": _q.MinDuration = null; break;
            case "max_duration": _q.MaxDuration = null; break;
            case "min_fps": _q.MinFps = null; break;
            case "aspect": _q.Aspect = null; break;
            case "codec": _q.Codec = null; break;
        }
        SyncDetailFields();
    }

    private Task SetKind(string kind) => GoAsync(() => _q.Kind = _q.Kind == kind ? "all" : kind);

    private Task ToggleMood(MoodEntry m) => GoAsync(() =>
    {
        var i = _tags.FindIndex(t => t.Query == m.Query);
        if (i >= 0) _tags.RemoveAt(i); else _tags.Add((m.Label, m.Query));
        _similarTo = 0; _imagePath = null; _title = "";
    });

    private async void ClearFilters_Click(object sender, RoutedEventArgs e) => await ClearAllAsync();

    private void Exact_Click(object sender, RoutedEventArgs e) => ExactPanel.Visibility = ExactToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async Task ClearAllAsync() => await GoAsync(() =>
    {
        _q = new FileQuery { Sort = _q.Sort };
        _tags.Clear(); _similarTo = 0; _imagePath = null; _title = "";
        SyncDetailFields();
    });

    private async Task ForgetFolderAsync(RootFolder root)
    {
        var name = Path.GetFileName(root.Path.TrimEnd('\\', '/'));
        if (!await AskAsync("Forget this folder?", $"“{name}” will leave File Mind. Your files are not touched or deleted.", "Forget")) return;
        try
        {
            await Api.RemoveRootAsync(root.Id);
            if (_q.Root == root.Id) _q.Root = null;
            await RefreshFacetsAsync();
            await ReloadAsync();
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void NameGroups_Click(object sender, RoutedEventArgs e)
    {
        List<NameGroup> groups;
        try { groups = await Api.GetNameGroupsAsync(); }
        catch (Exception ex) { ShowToast(ex.Message); return; }
        var list = new StackPanel { Spacing = 8 };
        Action? closeDialog = null;
        if (groups.Count == 0) list.Children.Add(new TextBlock { Text = "No shared name patterns found. Files from known tools are already under Sources.", TextWrapping = TextWrapping.Wrap });
        foreach (var g in groups)
        {
            var pattern = g.Pattern;
            var view = new Button { Content = "View", Style = Ui.Style("GlassAccentButton"), Padding = new Thickness(14, 5, 14, 5) };
            view.Click += async (_, _) => { closeDialog?.Invoke(); await GoAsync(() => { _q.Pattern = pattern; _title = "Names like “" + pattern + "”"; }); };
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{pattern}  ·  {g.Count:N0} files", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = string.Join(", ", g.Names), FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
            line.Children.Add(text);
            Grid.SetColumn(view, 1); line.Children.Add(view);
            list.Children.Add(line);
        }
        await Dialogs.ShowAsync(XamlRoot, "Group by file name", new ScrollViewer { MaxHeight = 460, Content = list, Padding = new Thickness(0, 0, 14, 0) }, null, "Close", width: 520, gotClose: h => closeDialog = h);
    }

    // ------------------------------------------------------------------ refine
    // The hard facts about files (quality, shape, length, size, date), grouped and counted over what is on screen.
    // An option that would show nothing, or would not narrow anything, is not offered; a chosen one always stays.
    private int _refineGen;

    private bool FacetOn(string key)
    {
        var now = DateTimeOffset.UtcNow;
        bool Ago(int d) => _q.After is { } a && Math.Abs(a - now.AddDays(-d).ToUnixTimeSeconds()) < 86400;
        return key switch
        {
            "4k" => _q.MinPixels == 3840L * 2160, "hd" => _q.MinPixels == 1920L * 1080,
            "portrait" or "square" or "wide" => _q.Aspect == key,
            "short" => _q.MaxDuration == 30, "long" => _q.MinDuration == 60, "60fps" => _q.MinFps == 50,
            "big" => _q.MinSize == 20_000_000, "huge" => _q.MinSize == 500_000_000,
            "today" => Ago(1), "week" => Ago(7), "month" => Ago(30), "year" => Ago(365), _ => false,
        };
    }

    private void ToggleFacet(string key)
    {
        var on = FacetOn(key);
        double Since(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds();
        switch (key)
        {
            case "4k": _q.MinPixels = on ? null : 3840L * 2160; break;
            case "hd": _q.MinPixels = on ? null : 1920L * 1080; break;
            case "portrait" or "square" or "wide": _q.Aspect = on ? null : key; break;
            case "short": _q.MaxDuration = on ? null : 30; break;
            case "long": _q.MinDuration = on ? null : 60; break;
            case "60fps": _q.MinFps = on ? null : 50; break;
            case "big": _q.MinSize = on ? null : 20_000_000; break;
            case "huge": _q.MinSize = on ? null : 500_000_000; break;
            case "today": _q.After = on ? null : Since(1); break;
            case "week": _q.After = on ? null : Since(7); break;
            case "month": _q.After = on ? null : Since(30); break;
            case "year": _q.After = on ? null : Since(365); break;
        }
    }

    private async Task RefreshSourcesAsync(Dictionary<string, object?> scope, int gen)
    {
        try
        {
            var found = await Api.GetSourcesAsync(scope.Where(kv => kv.Value is not null && kv.Key != "source").ToDictionary(kv => kv.Key, kv => kv.Value));
            if (gen != _refineGen) return;
            _sources = found;
            BuildSourceChips();
        }
        catch (Exception ex) { App.Log("sources", ex, null); }
    }

    private async Task FillRefineAsync()
    {
        var gen = ++_refineGen;
        var scope = new Dictionary<string, object?>
        {
            ["kind"] = _q.Kind == "all" ? null : _q.Kind, ["root"] = _q.Root, ["project"] = _q.Project,
            ["unassigned"] = _q.Unassigned ? true : null, ["color"] = _q.Color, ["near"] = _q.Near,
            ["source"] = _q.Source, ["pattern"] = _q.Pattern, ["discover"] = _q.Discover, ["tag"] = _q.Tag, ["label"] = _q.Label,
        };
        _ = RefreshSourcesAsync(scope, gen);
        IEnumerable<int>? ids = IsPlainBrowse ? null : _tiles.Select(t => t.Item.Id).ToList();
        FacetResult r;
        try { r = await Api.GetFacetsAsync(scope.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), ids); }
        catch (Exception ex) { App.Log("facets", ex, null); return; }
        if (gen != _refineGen) return;

        RefineGroups.Children.Clear();
        foreach (var group in r.Facets.GroupBy(f => f.Group).OrderBy(g => g.Key == "Date" ? 0 : 1))
        {
            var shown = group.Where(f => FacetOn(f.Key) || (f.Count > 0 && f.Count < r.Total)).ToList();
            if (shown.Count == 0) continue;
            // Each group: a label line (coloured dot and name) above its chips in two equal columns.
            var hint = group.Key switch { "Quality" => "#3B82F6", "Shape" => "#8B5CF6", "Length" => "#22A559", "Motion" => "#EC4899", "File size" => "#F59E0B", _ => "#14B8A6" };
            var wrap = new WrapPanel { Spacing = 6, Columns = 2 };
            foreach (var f in shown)
            {
                var key = f.Key;
                wrap.Children.Add(Chip(f.Label, FacetOn(key), () => GoAsync(() => { ToggleFacet(key); _title = ""; SyncDetailFields(); }),
                    hint, f.Count, $"{f.Hint} · {f.Count:N0} of {r.Total:N0}"));
            }
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(2, 0, 0, 0) };
            head.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Brush(hint), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = group.Key, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Brush("GlassTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            var block = new StackPanel { Spacing = 8 };
            head.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); Foldable(head, "refine:" + group.Key, f => wrap.Visibility = f ? Visibility.Collapsed : Visibility.Visible); block.Children.Add(head); block.Children.Add(wrap);
            RefineGroups.Children.Add(block);
        }
        RefineEmpty.Visibility = RefineGroups.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ colour labels and favourites
    private List<TagInfo> _tagList = [];
    private List<LabelInfo> _labelList = [];

    private HashSet<string> ActiveLabels() => (_q.Label ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private void BuildLabelChips()
    {
        LabelChips.Children.Clear();
        var favCount = _tagList.FirstOrDefault(t => t.Name == TagInfo.Favourite)?.Count ?? 0;
        var favOn = string.Equals(_q.Tag, TagInfo.Favourite, StringComparison.OrdinalIgnoreCase);
        LabelChips.Children.Add(Chip("\u2605  Favourites", favOn,
            () => GoAsync(() => { _q.Tag = favOn ? null : TagInfo.Favourite; _title = ""; }), null, favCount));
        var active = ActiveLabels();
        foreach (var (name, title, hex) in LabelInfo.All)
        {
            var count = _labelList.FirstOrDefault(l => l.Name == name)?.Count ?? 0;
            if (count == 0 && !active.Contains(name)) continue;
            var n = name;
            LabelChips.Children.Add(Chip(title, active.Contains(name), () => GoAsync(() =>
            {
                var set = ActiveLabels(); if (!set.Remove(n)) set.Add(n);
                _q.Label = set.Count == 0 ? null : string.Join(',', set); _title = "";
            }), hex, count));
        }
    }

    /// <summary>The colour choices for these files: a dot per colour, ticked when they all carry it, and "No label".</summary>
    private void AddLabelItems(IList<MenuFlyoutItemBase> into, IReadOnlyList<FileItem> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        foreach (var (name, title, hex) in LabelInfo.All)
        {
            var on = items.All(i => i.Label == name);
            var m = new ToggleMenuFlyoutItem { Text = title, IsChecked = on, Icon = new FontIcon { Glyph = "\u25CF", Foreground = Brush(hex) } };
            m.Click += async (_, _) => await ApplyLabelAsync(ids, on ? null : name);
            into.Add(m);
        }
        var none = new MenuFlyoutItem { Text = "No label", Icon = new FontIcon { Glyph = "\uE711" } };
        none.Click += async (_, _) => await ApplyLabelAsync(ids, null);
        into.Add(none);
    }

    private async Task ApplyLabelAsync(IReadOnlyCollection<int> ids, string? label)
    {
        try
        {
            _labelList = (await Api.SetLabelAsync(ids, label)).Labels;
            foreach (var t in _tiles.Where(t => ids.Contains(t.Item.Id))) t.SetLabel(label);
            BuildLabelChips();
            if (_q.Label is not null || _q.Sort == "label") await ReloadAsync();
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private void LabelSel_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        var menu = new MenuFlyout();
        AddLabelItems(menu.Items, items);
        menu.ShowAt((FrameworkElement)sender);
    }

    private List<FileItem> SelectedItems() => _tiles.Where(t => _sel.Contains(t.Item.Id)).Select(t => t.Item).ToList();

    private async Task ApplyTagsAsync(IReadOnlyCollection<int> ids, string? add, string? remove)
    {
        try
        {
            _tagList = (await Api.TagFilesAsync(ids, add is null ? null : [add], remove is null ? null : [remove])).Tags;
            foreach (var t in _tiles.Where(t => ids.Contains(t.Item.Id)))
            {
                if (add is not null && !t.Item.Tags.Contains(add, StringComparer.OrdinalIgnoreCase)) t.Item.Tags.Add(add);
                if (remove is not null) t.Item.Tags.RemoveAll(x => string.Equals(x, remove, StringComparison.OrdinalIgnoreCase));
            }
            BuildLabelChips();
            SyncFavButton();
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private void SyncFavButton()
    {
        var items = SelectedItems();
        var all = items.Count > 0 && items.All(i => i.IsFavourite);
        FavText.Text = all ? "Unfavourite" : "Favourite";
        if (all) FavIcon.Foreground = Ui.Brush("AccentBrush"); else FavIcon.ResetColor();
    }

    private async void Favourite_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        var ids = items.Select(i => i.Id).ToList();
        if (items.All(i => i.IsFavourite)) { await ApplyTagsAsync(ids, null, TagInfo.Favourite); ShowToast($"Removed {ids.Count:N0} from Favourites."); }
        else { await ApplyTagsAsync(ids, TagInfo.Favourite, null); ShowToast($"Added {ids.Count:N0} to Favourites."); }
    }

    private async Task ApplySmartAsync(string input)
    {
        var r = SmartFilter.Parse(input);
        await GoAsync(() =>
        {
            var s = r.Query;
            if (s.MinPixels is not null) _q.MinPixels = s.MinPixels;
            if (s.MinFps is not null) _q.MinFps = s.MinFps;
            if (s.Aspect is not null) _q.Aspect = s.Aspect;
            if (s.MinDuration is not null) _q.MinDuration = s.MinDuration;
            if (s.MaxDuration is not null) _q.MaxDuration = s.MaxDuration;
            if (s.MinSize is not null) _q.MinSize = s.MinSize;
            if (s.MaxSize is not null) _q.MaxSize = s.MaxSize;
            if (r.Kind is not null) _q.Kind = r.Kind;
            // With a reference picture on the pill the words refine it, so the reference stays; otherwise they start a text search.
            if (r.Text.Length > 0) _tags.Add((r.Text, r.Text));
            _title = "";
            SyncDetailFields();
        });
    }

    private void SyncDetailFields()
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            FFrom.Date = _q.After is { } a ? DateTimeOffset.FromUnixTimeSeconds((long)a).ToLocalTime() : null;
            FThrough.Date = _q.Before is { } b ? DateTimeOffset.FromUnixTimeSeconds((long)b - 1).ToLocalTime() : null;
            FMinMb.Value = _q.MinSize is { } mn ? mn / 1e6 : double.NaN;
            FMaxMb.Value = _q.MaxSize is { } mx ? mx / 1e6 : double.NaN;
            FMinW.Value = _q.MinWidth ?? double.NaN;
            FMinH.Value = _q.MinHeight ?? double.NaN;
            FMinLen.Value = _q.MinDuration ?? double.NaN;
            FMaxLen.Value = _q.MaxDuration ?? double.NaN;
            FMinFps.Value = _q.MinFps ?? double.NaN;
            FAspect.SelectedIndex = Math.Max(0, new[] { "", "landscape", "portrait", "square", "wide" }.ToList().IndexOf(_q.Aspect ?? ""));
            FCodec.SelectedIndex = Math.Max(0, new[] { "", "h264", "hevc", "av1", "vp9", "prores" }.ToList().IndexOf(_q.Codec ?? ""));
        }
        finally { _syncing = was; }
    }

    private void Detail_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!_syncing) { _detailDebounce.Stop(); _detailDebounce.Start(); } }
    private void Detail_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (!_syncing) { _detailDebounce.Stop(); _detailDebounce.Start(); } }
    private void Detail_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) { _detailDebounce.Stop(); _detailDebounce.Start(); } }

    private async Task ApplyDetailFieldsAsync()
    {
        static double? N(NumberBox b) => double.IsNaN(b.Value) ? null : b.Value;
        static string? Tag(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } t ? t : null;
        void Fill(FileQuery q)
        {
            q.After = FFrom.Date is { } f ? f.LocalDateTime.Date.Subtract(DateTime.UnixEpoch.ToLocalTime()).TotalSeconds : null;
            q.Before = FThrough.Date is { } t ? t.LocalDateTime.Date.AddDays(1).Subtract(DateTime.UnixEpoch.ToLocalTime()).TotalSeconds : null;
            q.MinSize = N(FMinMb) is { } a ? (long)(a * 1e6) : null;
            q.MaxSize = N(FMaxMb) is { } b ? (long)(b * 1e6) : null;
            q.MinWidth = N(FMinW) is { } w ? (int)w : null;
            q.MinHeight = N(FMinH) is { } h ? (int)h : null;
            q.MinDuration = N(FMinLen);
            q.MaxDuration = N(FMaxLen);
            q.MinFps = N(FMinFps);
            q.Aspect = Tag(FAspect);
            q.Codec = Tag(FCodec);
        }
        // Controls announce themselves when the sidebar first shows them; nothing changed, so nothing to reload.
        var probe = _q.Clone(); Fill(probe);
        if (probe.DetailFilters().Count == _q.DetailFilters().Count && probe.DetailFilters().All(kv => _q.DetailFilters().TryGetValue(kv.Key, out var v) && Equals(v, kv.Value))) return;
        await GoAsync(() => Fill(_q));
    }

    // ------------------------------------------------------------------ tiles: click, check, hover, open, drag
    private static bool Held(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static TileVm? Vm(object sender)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { Tag: TileVm vm }) return vm;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) { if (Vm(sender) is { } t) { t.Hover = true; if (sender is FrameworkElement fe) Glow.On(fe); } }
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) { if (Vm(sender) is { } t) { t.Hover = false; if (sender is FrameworkElement fe) Glow.Off(fe); } }

    /// <summary>Right-click a tile: the usual things to do with it. Over a ticked tile the menu acts on every ticked file.</summary>
    private void Tile_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Vm(sender) is not { } tile) return;
        e.Handled = true;
        var many = tile.Checked && _sel.Count > 1;
        var items = many ? SelectedItems() : [tile.Item];
        var ids = items.Select(i => i.Id).ToList();
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action run, string? keys = null)
        {
            var m = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys is not null) m.KeyboardAcceleratorTextOverride = keys;
            m.Click += (_, _) => run();
            menu.Items.Add(m);
            return m;
        }
        if (!many)
        {
            Item("Open", "", () => Frame.Navigate(typeof(ViewerPage), new ViewerRequest(tile.Item, _tiles.Select(t => t.Item).ToList())), "Enter");
            Item("Show in folder", "", () =>
            {
                if (!File.Exists(tile.Item.Path)) { ShowToast("That file has moved."); return; }
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{tile.Item.Path}\"", UseShellExecute = false });
            });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        Item(many ? $"Copy {items.Count:N0} files" : "Copy file", "", () => _ = CopyItemsAsync(items, false), "Ctrl+C");
        Item(many ? "Copy paths" : "Copy path", "", () => _ = CopyItemsAsync(items, true), "Ctrl+Shift+C");
        menu.Items.Add(new MenuFlyoutSeparator());
        var fav = items.All(i => i.IsFavourite);
        Item(fav ? "Remove from Favourites" : "Add to Favourites", fav ? "" : "", () => _ = fav ? ApplyTagsAsync(ids, null, TagInfo.Favourite) : ApplyTagsAsync(ids, TagInfo.Favourite, null));
        if (!many) Item("Find similar", "", () => _ = GoAsync(() => { _similarTo = tile.Item.Id; _similarName = tile.Name; _tags.Clear(); _imagePath = null; _title = ""; }));
        if (_projects.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = "Add to space", Icon = new FontIcon { Glyph = "" } };
            foreach (var p in _projects)
            {
                var pid = p.Id;
                var s = new MenuFlyoutItem { Text = p.Name };
                s.Click += async (_, _) =>
                {
                    try { await Api.AddToProjectAsync(pid, ids); ShowToast($"Added to “{p.Name}”."); await RefreshFacetsAsync(); }
                    catch (Exception ex) { ShowToast(ex.Message); }
                };
                sub.Items.Add(s);
            }
            menu.Items.Add(sub);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(many ? $"Delete {items.Count:N0} files" : "Delete", "", () => _ = DeleteIdsAsync(ids), "Del");
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    private void Tile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Vm(sender) is not { } tile) return;
        // Two quick taps on the same tile open the original (DoubleTapped is swallowed by drag-out on some inputs).
        var now = Environment.TickCount64;
        var again = ReferenceEquals(tile, _lastTapTile) && now - _lastTapAt < 450 && !Held(VirtualKey.Shift) && !Held(VirtualKey.Control);
        _lastTapTile = tile; _lastTapAt = now;
        var index = _tiles.IndexOf(tile);
        if (Held(VirtualKey.Shift) && _lastChecked >= 0)
        {
            var (lo, hi) = (Math.Min(_lastChecked, index), Math.Max(_lastChecked, index));
            for (var i = lo; i <= hi; i++) SetChecked(_tiles[i], true);
            AfterSelectionChange();
        }
        else if (Held(VirtualKey.Control) || _sel.Count > 0) ToggleCheck(tile);
        else Frame.Navigate(typeof(ViewerPage), new ViewerRequest(tile.Item, _tiles.Select(t => t.Item).ToList()));
    }

    private void Check_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Vm(sender) is { } tile)
        {
            if (Held(VirtualKey.Shift) && _lastChecked >= 0)
            {
                var index = _tiles.IndexOf(tile);
                var (lo, hi) = (Math.Min(_lastChecked, index), Math.Max(_lastChecked, index));
                for (var i = lo; i <= hi; i++) SetChecked(_tiles[i], true);
                AfterSelectionChange();
            }
            else ToggleCheck(tile);
        }
    }

    private void ToggleCheck(TileVm tile)
    {
        SetChecked(tile, !tile.Checked);
        _lastChecked = _tiles.IndexOf(tile);
        AfterSelectionChange();
    }

    private void SetChecked(TileVm tile, bool on)
    {
        tile.Checked = on;
        if (on) _sel.Add(tile.Item.Id); else _sel.Remove(tile.Item.Id);
    }

    private void AfterSelectionChange()
    {
        var any = _sel.Count > 0;
        foreach (var t in _tiles) t.AnyChecked = any;
        var barWasHidden = ActionBar.Visibility != Visibility.Visible;
        ActionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any && barWasHidden) Anim.In(ActionBar, 180, 18);
        if (any) SyncFavButton();
        SelCount.Text = $"{_sel.Count:N0} selected";
        RemoveFromSpaceBtn.Visibility = _q.Project is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearSelection()
    {
        if (_sel.Count == 0) { SetFocus(null); return; }
        _sel.Clear();
        foreach (var t in _tiles) t.Checked = false;
        _lastChecked = -1;
        AfterSelectionChange();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e) => ClearSelection();
    private async void SelectAll_Click(object sender, RoutedEventArgs e) => await SelectAllAsync();

    private async Task SelectAllAsync()
    {
        try
        {
            if (IsPlainBrowse)
                foreach (var id in await Api.GetFileIdsAsync(_q)) _sel.Add(id);
            else
                foreach (var t in _tiles) _sel.Add(t.Item.Id);
        }
        catch (Exception ex) { ShowToast(ex.Message); }
        foreach (var t in _tiles) t.Checked = _sel.Contains(t.Item.Id);
        AfterSelectionChange();
    }

    // ------------------------------------------------------------------ the inspector
    private void SetFocus(TileVm? tile)
    {
        if (_focused is not null) _focused.Focused = false;
        _focused = tile;
        if (tile is null)
        {
            DetailColumn.Width = new GridLength(0);
            Detail.Visibility = Visibility.Collapsed;
            return;
        }
        tile.Focused = true;
        var f = tile.Item;
        DetailColumn.Width = new GridLength(350);
        Detail.Visibility = Visibility.Visible;
        DName.Text = f.Name;
        DPath.Text = f.Path;
        DMeta.Text = string.Join(" · ", new[] { f.Kind switch { "image" => "Photo", "video" => "Video", "audio" => "Sound", _ => f.Kind },
                                                f.DimensionsText, f.DurationText, f.Fps is > 0 ? $"{f.Fps:0.#} fps" : "", f.Codec?.ToUpperInvariant() ?? "", f.SizeText, f.Modified.ToString("d MMM yyyy") }
                                       .Where(s => !string.IsNullOrEmpty(s)));
        PreviewGlyph.Kind = tile.IconKind;
        PreviewGlyph.Visibility = f.HasThumb ? Visibility.Collapsed : Visibility.Visible;
        PlayBtn.Visibility = f.Kind is "video" or "audio" ? Visibility.Visible : Visibility.Collapsed;
        TranscriptBtn.Visibility = f.Kind is "video" or "audio" ? Visibility.Visible : Visibility.Collapsed;
        DColorsBox.Visibility = Visibility.Collapsed;
        DSpacesBox.Visibility = Visibility.Collapsed;
        if (f.Kind == "image")
        {
            var full = new BitmapImage(Api.FileUri(f.Id)) { DecodePixelWidth = 900 };
            full.ImageFailed += (_, _) => Preview.Source = tile.Thumb;   // formats the system cannot decode fall back to the thumbnail
            Preview.Source = full;
        }
        else Preview.Source = tile.Thumb;
        _ = LoadInfoAsync(tile);
    }

    private async Task LoadInfoAsync(TileVm tile)
    {
        try
        {
            var info = await Api.GetInfoAsync(tile.Item.Id);
            if (_focused != tile) return;
            DColors.Children.Clear();
            foreach (var hex in info.Colors.Take(8))
            {
                var h = hex;
                var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = Brush(hex), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1) };
                ToolTipService.SetToolTip(dot, "Find files near " + hex);
                dot.Tapped += async (_, _) => await GoAsync(() => { _q.Near = h.StartsWith('#') ? h : "#" + h; _q.Color = null; });
                DColors.Children.Add(dot);
            }
            DColorsBox.Visibility = info.Colors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DSpaces.ItemsSource = info.Projects.Select(p =>
            {
                var b = new Button { Content = p.Name, Style = Ui.Style("GlassButton"), Padding = new Thickness(11, 4, 11, 4), FontSize = 12, CornerRadius = new CornerRadius(5) };
                b.Click += async (_, _) => await GoAsync(() => { _q = new FileQuery { Project = p.Id }; _tags.Clear(); _similarTo = 0; _title = ""; });
                return (FrameworkElement)b;
            }).ToList();
            DSpacesBox.Visibility = info.Projects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (info.Dimensions is { Count: 2 } d && tile.Item.DimensionsText.Length == 0) DMeta.Text = $"{d[0]} × {d[1]} · " + DMeta.Text;
        }
        catch (Exception ex) { FileMind.Core.Diag.Log("Library.details", ex); }
    }

    // ------------------------------------------------------------------ working with the original file
    private static async Task<StorageFile?> FileAsync(string path)
    {
        try { return await StorageFile.GetFileFromPathAsync(path); }
        catch (Exception) { return null; }
    }

    /// <summary>Opens the original in whatever the user's default app for it is.</summary>
    private bool OpenOriginal(string path)
    {
        if (!File.Exists(path)) { ShowToast("That file has moved. It is no longer where File Mind found it."); return false; }
        try
        {
            var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            App.Trace($"open ok: {path} (pid {started?.Id.ToString() ?? "reused app"})");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Open", ex, path);
            ShowToast("Couldn't open it: " + ex.Message);
            return false;
        }
    }

    /// <summary>Dragging a tile hands the real file(s) to whatever it is dropped on: Explorer, Photoshop, After Effects, CapCut…</summary>
    private async void Tile_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var tile = Vm(sender);
            if (tile is null) { args.Cancel = true; return; }
            var paths = tile.Checked && _sel.Count > 1
                ? _tiles.Where(t => t.Checked).Select(t => t.Item.Path).Take(60).ToList()
                : [tile.Item.Path];
            var files = new List<IStorageItem>();
            foreach (var p in paths) if (await FileAsync(p) is { } f) files.Add(f);
            if (files.Count == 0) { args.Cancel = true; return; }
            args.Data.SetStorageItems(files);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        finally { deferral.Complete(); }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_focused is not null) OpenOriginal(_focused.Item.Path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_focused is null) return;
        if (!File.Exists(_focused.Item.Path)) { ShowToast("That file has moved."); return; }
        // Explorer wants the whole thing as one quoted argument: /select,"C:\path with spaces\file.png"
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_focused.Item.Path}\"", UseShellExecute = false });
    }

    /// <summary>Ctrl+C puts the selected files on the clipboard (paste them into any folder or app); with Shift, their paths.</summary>
    private async Task CopySelectedAsync(bool paths)
    {
        var items = _tiles.Where(t => t.Checked).Select(t => t.Item).ToList();
        if (items.Count == 0 && _focused is not null) items.Add(_focused.Item);
        await CopyItemsAsync(items, paths);
    }

    private async Task CopyItemsAsync(List<FileItem> items, bool paths)
    {
        if (items.Count == 0) return;
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            if (paths) data.SetText(string.Join(Environment.NewLine, items.Select(i => i.Path)));
            else
            {
                var files = new List<IStorageItem>();
                foreach (var i in items.Where(i => File.Exists(i.Path))) files.Add(await StorageFile.GetFileFromPathAsync(i.Path));
                if (files.Count == 0) { ShowToast("Those files have moved."); return; }
                data.SetStorageItems(files);
            }
            Clipboard.SetContent(data);
            try { Clipboard.Flush(); } catch (Exception) { }
            ShowToast(paths ? (items.Count == 1 ? "Path copied." : $"Copied {items.Count:N0} paths.") : (items.Count == 1 ? "Copied. Paste it anywhere." : $"Copied {items.Count:N0} files. Paste them anywhere."));
        }
        catch (Exception ex) { App.Log("copy", ex, ex.Message); ShowToast("Couldn't copy that."); }
    }

    /// <summary>Enter opens the one selected file in the viewer.</summary>
    private void OpenSelected()
    {
        var picked = _tiles.Where(t => t.Checked).ToList();
        if (picked.Count != 1) return;
        Frame.Navigate(typeof(ViewerPage), new ViewerRequest(picked[0].Item, _tiles.Select(t => t.Item).ToList()));
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_focused is null) return;
        var data = new DataPackage();
        data.SetText(_focused.Item.Path);
        Clipboard.SetContent(data);
        ShowToast("Path copied.");
    }

    private async void Similar_Click(object sender, RoutedEventArgs e)
    {
        if (_focused is null) return;
        var id = _focused.Item.Id; var name = _focused.Name;
        await GoAsync(() => { _similarTo = id; _similarName = name; _tags.Clear(); _imagePath = null; _title = ""; });
    }

    private async void Transcript_Click(object sender, RoutedEventArgs e)
    {
        if (_focused is null) return;
        var id = _focused.Item.Id;
        var body = new TextBlock { Text = "Reading what is said…", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var open = true;
        _ = Dialogs.ShowAsync(XamlRoot, "What is said", new ScrollViewer { MaxHeight = 420, Content = body }, null, "Close", width: 520).ContinueWith(_ => open = false);
        try
        {
            var state = await Api.GetTranscriptAsync(id);
            if (string.IsNullOrEmpty(state.Text)) state = await Api.StartTranscriptAsync(id);
            while (open && string.IsNullOrEmpty(state.Text) && state.Error is null && state.Status is not ("done" or "failed" or "none"))
            {
                await Task.Delay(1500);
                state = await Api.GetTranscriptAsync(id);
            }
            if (open) body.Text = state.Text is { Length: > 0 } ? state.Text : state.Error ?? "Nothing was said, or it could not be read.";
        }
        catch (Exception ex) { if (open) body.Text = ex.Message; }
    }

    // ------------------------------------------------------------------ actions on the selection
    private async Task<bool> AskAsync(string title, string body, string primary)
    {
        return await Dialogs.ShowAsync(XamlRoot, title, Dialogs.Text(body), primary, "Cancel", enterConfirms: false);
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string initial = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await Dialogs.ShowAsync(XamlRoot, title, box, "Save", width: 400) && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void AddToSpace_Click(object sender, RoutedEventArgs e)
    {
        if (SpacePick.SelectedItem is not ComboBoxItem { Tag: int pid }) { ShowToast("Choose a space first."); return; }
        try { await Api.AddToProjectAsync(pid, _sel); ShowToast($"Added {_sel.Count:N0} to the space."); ClearSelection(); await RefreshFacetsAsync(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void NewSpaceFromSel_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New space", "Name this space");
        if (name is null) return;
        try { await Api.CreateProjectAsync(name, _sel); ShowToast($"Made “{name}”."); ClearSelection(); await RefreshFacetsAsync(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New space", "Name this space");
        if (name is null) return;
        try { await Api.CreateProjectAsync(name, _sel); await RefreshFacetsAsync(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void RemoveFromSpace_Click(object sender, RoutedEventArgs e)
    {
        if (_q.Project is not { } pid) return;
        try { await Api.RemoveFromProjectAsync(pid, _sel); ClearSelection(); await RefreshFacetsAsync(); await ReloadAsync(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The dialog needs to know what each file is, which only loaded tiles tell it.
            var items = SelectedItems();
            if (items.Count < _sel.Count) { ShowToast($"{_sel.Count - items.Count:N0} of the selected files are not loaded yet. Scroll down to load them, then export."); return; }
            var queued = await ExportDialog.ShowAsync(XamlRoot, items);
            if (queued > 0) ClearSelection();
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void CopyTo_Click(object sender, RoutedEventArgs e) => await CopyMoveAsync("copy");
    private async void MoveTo_Click(object sender, RoutedEventArgs e) => await CopyMoveAsync("move");

    private async Task CopyMoveAsync(string action)
    {
        var dest = await PickFolderAsync();
        if (dest is null) return;
        try
        {
            await Api.FileActionAsync(action, _sel, dest);
            ShowToast(action == "copy" ? "Copied." : "Moved.");
            ClearSelection();
            if (action == "move") await ReloadAsync();
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private Task DeleteSelectedAsync() => DeleteIdsAsync(_sel.ToList());

    private async Task DeleteIdsAsync(List<int> ids)
    {
        if (ids.Count == 0) return;
        if (!await AskAsync($"Delete {ids.Count:N0} file{(ids.Count == 1 ? "" : "s")}?", "They go to the Recycle Bin, so you can still get them back.", "Delete")) return;
        try
        {
            await Api.FileActionAsync("delete", ids);
            ClearSelection();
            await RefreshFacetsAsync();
            await ReloadAsync();
            ShowToast("Moved to the Recycle Bin.");
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }
}
