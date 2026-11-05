using System.Collections.ObjectModel;
using System.Diagnostics;
using FileMind.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
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

public sealed record ViewerRequest(FileItem Item, IReadOnlyList<FileItem> Siblings);

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
    public void ApplySections()
    {
        var all = new Dictionary<string, UIElement> { ["types"] = SecTypes, ["sources"] = SecSources, ["colours"] = SecColours, ["folders"] = SecFolders, ["moods"] = SecMoods, ["spaces"] = SecSpaces, ["details"] = SecDetails };
        if (!Settings.SectionOrder.Contains("moods")) { Settings.SectionOrder = ["types", "sources", "colours", "folders", "moods", "spaces", "details"]; Settings.SectionHidden.Remove("colours"); try { Settings.Save(AppRuntime.Current.Paths); } catch { } }
        var order = Settings.SectionOrder.Where(all.ContainsKey).Distinct().Concat(all.Keys.Except(Settings.SectionOrder)).ToList();
        SectionStack.Children.Clear();
        foreach (var key in order)
        {
            all[key].Visibility = Settings.SectionHidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
            SectionStack.Children.Add(all[key]);
        }
    }

    public LibraryPage()
    {
        InitializeComponent();
        Current = this;
        ApplySections();
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
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => { ClearSelection(); return Task.CompletedTask; }, false);
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, async () => await DeleteSelectedAsync(), false);

        // The filters live in the app's left rail, always in the same spot, while this page is showing.
        BodyGrid.Children.Remove(FilterPanel);
        App.Window?.SetSidebar(FilterPanel);

        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += async (_, _) =>
        {
            App.Window?.SetSidebar(FilterPanel);
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
        }
        catch (Exception) { }
        RebuildPanels();
    }

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
        EmptyCard.Visibility = Visibility.Collapsed;
        RebuildPanels();
        await LoadMoreAsync(_cts.Token);
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_loading || !_more) return;
        _loading = true;
        Busy.IsActive = _tiles.Count == 0;
        try
        {
            List<FileItem> items;
            if (_imagePath is not null)
            {
                items = await Api.SearchByImageAsync(_imagePath, _q, ct);
                _more = false;
            }
            else if (_similarTo > 0)
            {
                items = await Api.SimilarAsync(_similarTo, _q, 200, ct);
                _more = false;
            }
            else if (_tags.Count > 0)
            {
                items = await Api.SearchAsync(_tags.Select(t => t.Query), _q, PageSize, _offset, ct);
                _more = items.Count == PageSize;
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

    private void UpdateTitle()
    {
        BackBtn.IsEnabled = _back.Count > 0;
        FwdBtn.IsEnabled = _fwd.Count > 0;
        if (_imagePath is not null) { PageTitle.Text = "Looks (or sounds) like your file"; Summary.Text = $"{_tiles.Count:N0} closest matches"; return; }
        if (_similarTo > 0) { PageTitle.Text = "Similar files"; Summary.Text = $"More like “{_similarName}”"; return; }
        if (_tags.Count > 0) { PageTitle.Text = "Results"; Summary.Text = "Best matches for " + string.Join(" + ", _tags.Select(t => $"“{t.Label}”")); return; }
        string title = _title;
        if (title.Length == 0)
        {
            if (_q.Project is { } pid) title = _projects.FirstOrDefault(p => p.Id == pid)?.Name ?? "Space";
            else if (_q.Root is { } rid) title = Path.GetFileName(_roots.FirstOrDefault(r => r.Id == rid)?.Path.TrimEnd('\\', '/') ?? "") is { Length: > 0 } n ? n : "Folder";
            else if (_q.Unassigned) title = "Unsorted";
            else if (_q.Source is { } s) title = _sources.FirstOrDefault(x => x.Key == s)?.Label ?? "Source";
            else title = "Library";
        }
        PageTitle.Text = title;
        if (_q.IsNarrowed) Summary.Text = $"{_total:N0} files";
        else
        {
            var parts = new List<string>();
            void Add(string key, string word) { if (_stats.Kinds.TryGetValue(key, out var n) && n > 0) parts.Add($"{n:N0} {word}"); }
            Add("image", "photos"); Add("video", "videos"); Add("audio", "sounds");
            Summary.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing here yet";
        }
    }

    private void ShowEmptyIfNeeded()
    {
        if (_tiles.Count > 0) { EmptyCard.Visibility = Visibility.Collapsed; return; }
        if (_roots.Count == 0) ShowEmpty("Add a folder to begin", "Choose a folder of photos, videos or sounds. File Mind reads it on this PC and nothing is uploaded.", true);
        else if (_wasIndexing) ShowEmpty("Reading your files…", "Files appear here as soon as they have been read.", false);
        else if (_tags.Count > 0 || _similarTo > 0 || _imagePath is not null) ShowEmpty("Nothing matched", "Try describing it differently, or loosen a filter.", false);
        else ShowEmpty("No files like that yet", "Clear a filter, or add another folder.", false);
    }

    private void ShowEmpty(string title, string body, bool offerAdd)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyAction.Visibility = offerAdd ? Visibility.Visible : Visibility.Collapsed;
        EmptyCard.Visibility = Visibility.Visible;
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
        catch (Exception) { }
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

    private async void NavBack_Click(object sender, RoutedEventArgs e) => await GoBackAsync();
    private async void NavFwd_Click(object sender, RoutedEventArgs e) => await GoForwardAsync();

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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { }

    private void RebuildTags()
    {
        QueryTags.Children.Clear();
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
            SearchBox.PlaceholderText = "Listening… say what you are looking for";
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
        SearchBox.PlaceholderText = "Describe what you are looking for…";
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
    private bool _allColors;
    private void Colors_Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _allColors = true; ColorBox.MaxHeight = double.PositiveInfinity; }
    private void Colors_Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _allColors = false; ColorBox.MaxHeight = 51; }

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

    private void FilterToggle_Changed(object sender, RoutedEventArgs e)
    {
        var on = FilterToggle.IsChecked == true;
        FilterPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FilterColumn.Width = new GridLength(on ? 310 : 0);
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
        var row = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (dot is not null) row.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4), Background = Brush(dot), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        if (count is { } n) { var ct = new TextBlock { Text = n.ToString("N0"), FontSize = 11.5, Foreground = Ui.Brush("GlassTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(ct, 2); row.Children.Add(ct); }
        var b = new ToggleButton { Content = row, IsChecked = on, Style = Ui.Style("GlassChip"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 10, 6), MinHeight = 30, CornerRadius = new CornerRadius(6) };
        b.Click += async (_, _) => await click();
        if (tooltip is not null) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    /// <summary>A file-type filter as an icon tile: icon above, name and count below.</summary>
    private FrameworkElement TypeTile(string label, string icon, bool on, string kind, int count)
    {
        var col = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(new LineIcon { Kind = icon, Size = 18, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(new TextBlock { Text = count.ToString("N0"), FontSize = 10.5, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center });
        var b = new ToggleButton { Content = col, IsChecked = on, Style = Ui.Style("GlassChip"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 8, 4, 7), CornerRadius = new CornerRadius(5) };
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
                TypeTile("All", "grid", _q.Kind == "all", "all", _stats.Total),
                TypeTile("Photos", "photo", _q.Kind == "image", "image", Kind("image")),
                TypeTile("Videos", "video", _q.Kind == "video", "video", Kind("video")),
                TypeTile("Sounds", "music", _q.Kind == "audio", "audio", Kind("audio")),
            };
            foreach (var (key, label, icon) in new[] { ("doc", "Docs", "file"), ("text", "Text", "text"), ("code", "Code", "text") })
                if (Kind(key) > 0 || _q.Kind == key) types.Add(TypeTile(label, icon, _q.Kind == key, key, Kind(key)));
            TypeChips.ItemsSource = types;

            // colours: two tidy rows, the rest on request
            var shown = _colors.Where(c => c.Count > 0 || _q.Color == c.Key).ToList();
            ColorDots.ItemsSource = shown.Select(c =>
            {
                var selected = _q.Color == c.Key;
                var dot = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = Brush(c.Swatch),
                    BorderThickness = new Thickness(selected ? 3 : 1), BorderBrush = selected ? Ui.Brush("AccentOnStrokeBrush") : Ui.Brush("GlassStrokeBrush"),
                };
                ToolTipService.SetToolTip(dot, $"{c.Label} · {c.Count:N0}");
                dot.Tapped += async (_, _) => await GoAsync(() => _q.Color = _q.Color == c.Key ? null : c.Key);
                return (FrameworkElement)dot;
            }).ToList();

            MoodChips.ItemsSource = _moods.Select(m => Chip(m.Label, _tags.Any(t => t.Query == m.Query), () => ToggleMood(m), null, null, null, true)).ToList();

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

            var spaceChips = _projects.Select(p =>
                Chip(p.Name, _q.Project == p.Id, () => GoAsync(() => { _q.Project = _q.Project == p.Id ? null : p.Id; _title = ""; }), null, p.Count, null, true)).ToList();
            spaceChips.Add(Chip("Unsorted", _q.Unassigned, () => GoAsync(() => { _q.Unassigned = !_q.Unassigned; _title = ""; }), null, null, "Files that are not in any space", true));
            SpaceChips.ItemsSource = spaceChips;

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
                ((ToggleButton)chip).HorizontalContentAlignment = HorizontalAlignment.Left;
                Grid.SetColumn(reindex, 1); Grid.SetColumn(forget, 2);
                line.Children.Add(chip); line.Children.Add(reindex); line.Children.Add(forget);
                FolderList.Children.Add(line);
            }

            _ = FillRefineAsync();

            RebuildActiveChips();
        }
        finally { _syncing = false; }
    }

    private void RebuildActiveChips()
    {
        var chips = new List<FrameworkElement>();
        void Active(string text, Action remove)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new TextBlock { Text = text, FontSize = 12 });
            row.Children.Add(new LineIcon { Kind = "close", Size = 9, VerticalAlignment = VerticalAlignment.Center  });
            var b = new Button { Content = row, Style = Ui.Style("GlassButton"), Padding = new Thickness(11, 4, 11, 4), CornerRadius = new CornerRadius(5), Background = Ui.Brush("AccentTintBrush") };
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
        ActiveChips.ItemsSource = chips;
        ClearFiltersBtn.Visibility = _q.IsNarrowed ? Visibility.Visible : Visibility.Collapsed;
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
        var dialog = new ContentDialog
        { RequestedTheme = ThemeEngine.Element(),
            Title = "Group by file name",
            XamlRoot = XamlRoot,
            CloseButtonText = "Close",
            Content = new ScrollViewer { MaxHeight = 460, Content = list, Padding = new Thickness(0, 0, 14, 0) },
        };
        if (groups.Count == 0) list.Children.Add(new TextBlock { Text = "No shared name patterns found. Files from known tools are already under Sources.", TextWrapping = TextWrapping.Wrap });
        foreach (var g in groups)
        {
            var pattern = g.Pattern;
            var view = new Button { Content = "View", Style = Ui.Style("GlassAccentButton"), Padding = new Thickness(14, 5, 14, 5) };
            view.Click += async (_, _) => { dialog.Hide(); await GoAsync(() => { _q.Pattern = pattern; _title = "Names like “" + pattern + "”"; }); };
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
        await dialog.ShowAsync();
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

    private async Task FillRefineAsync()
    {
        var gen = ++_refineGen;
        var scope = new Dictionary<string, object?>
        {
            ["kind"] = _q.Kind == "all" ? null : _q.Kind, ["root"] = _q.Root, ["project"] = _q.Project,
            ["unassigned"] = _q.Unassigned ? true : null, ["color"] = _q.Color, ["near"] = _q.Near,
            ["source"] = _q.Source, ["pattern"] = _q.Pattern, ["discover"] = _q.Discover,
        };
        IEnumerable<int>? ids = IsPlainBrowse ? null : _tiles.Select(t => t.Item.Id).ToList();
        FacetResult r;
        try { r = await Api.GetFacetsAsync(scope.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), ids); }
        catch (Exception ex) { App.Log("facets", ex, null); return; }
        if (gen != _refineGen) return;

        RefineGroups.Children.Clear();
        foreach (var group in r.Facets.GroupBy(f => f.Group))
        {
            var shown = group.Where(f => FacetOn(f.Key) || (f.Count > 0 && f.Count < r.Total)).ToList();
            if (shown.Count == 0) continue;
            var box = new StackPanel { Spacing = 6 };
            box.Children.Add(new TextBlock { Text = group.Key, FontSize = 11.5, Foreground = Ui.Brush("GlassTextSecondaryBrush") });
            var wrap = new WrapPanel { Spacing = 6 };
            foreach (var f in shown)
            {
                var key = f.Key;
                wrap.Children.Add(Chip(f.Label, FacetOn(key), () => GoAsync(() => { ToggleFacet(key); _title = ""; SyncDetailFields(); }),
                    null, f.Count, $"{f.Hint} · {f.Count:N0} of {r.Total:N0}"));
            }
            box.Children.Add(wrap);
            RefineGroups.Children.Add(box);
        }
        RefineEmpty.Visibility = RefineGroups.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            if (r.Text.Length > 0) { _tags.Add((r.Text, r.Text)); _similarTo = 0; _imagePath = null; }
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
        await GoAsync(() =>
        {
            _q.After = FFrom.Date is { } f ? f.LocalDateTime.Date.Subtract(DateTime.UnixEpoch.ToLocalTime()).TotalSeconds : null;
            _q.Before = FThrough.Date is { } t ? t.LocalDateTime.Date.AddDays(1).Subtract(DateTime.UnixEpoch.ToLocalTime()).TotalSeconds : null;
            _q.MinSize = N(FMinMb) is { } a ? (long)(a * 1e6) : null;
            _q.MaxSize = N(FMaxMb) is { } b ? (long)(b * 1e6) : null;
            _q.MinWidth = N(FMinW) is { } w ? (int)w : null;
            _q.MinHeight = N(FMinH) is { } h ? (int)h : null;
            _q.MinDuration = N(FMinLen);
            _q.MaxDuration = N(FMaxLen);
            _q.MinFps = N(FMinFps);
            _q.Aspect = Tag(FAspect);
            _q.Codec = Tag(FCodec);
        });
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

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) { if (Vm(sender) is { } t) t.Hover = true; }
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) { if (Vm(sender) is { } t) t.Hover = false; }

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
        ActionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
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
        catch (Exception) { }
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
        var dialog = new ContentDialog { RequestedTheme = ThemeEngine.Element(), Title = "What is said", XamlRoot = XamlRoot, CloseButtonText = "Close", Content = new ScrollViewer { MaxHeight = 420, Content = body } };
        var open = true;
        dialog.Closed += (_, _) => open = false;
        _ = dialog.ShowAsync();
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
        var d = new ContentDialog { RequestedTheme = ThemeEngine.Element(), Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string initial = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial, MinWidth = 320 };
        var d = new ContentDialog { RequestedTheme = ThemeEngine.Element(), Title = title, Content = box, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        return await d.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
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

    private async Task DeleteSelectedAsync()
    {
        if (_sel.Count == 0) return;
        if (!await AskAsync($"Delete {_sel.Count:N0} file{(_sel.Count == 1 ? "" : "s")}?", "They go to the Recycle Bin, so you can still get them back.", "Delete")) return;
        try
        {
            await Api.FileActionAsync("delete", _sel);
            ClearSelection();
            await RefreshFacetsAsync();
            await ReloadAsync();
            ShowToast("Moved to the Recycle Bin.");
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }
}
