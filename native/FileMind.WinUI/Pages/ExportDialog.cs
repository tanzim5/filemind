using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileMind.Pages;

/// <summary>The one place exports are set up: from the viewer (one file, perhaps a marked part of it) or from a
/// selection in the Library. It only offers what makes sense for the files chosen, then queues the work and returns;
/// progress shows in the window's export tray.</summary>
internal static class ExportDialog
{
    private static string Clock(double s) => s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(s).ToString(@"m\:ss");

    /// <summary>Shows the dialog and starts the export. Returns how many files were queued (0 when cancelled).</summary>
    public static async Task<int> ShowAsync(XamlRoot root, IReadOnlyList<FileItem> items, double? start = null, double? end = null)
    {
        items = [.. items.Where(i => ExportSpec.CanExport(i.Kind))];
        if (items.Count == 0) throw new InvalidOperationException("Pictures, videos and sounds can be exported. None of these files are.");
        var kinds = items.Select(i => i.Kind).Distinct().ToList();
        var single = kinds.Count == 1 ? kinds[0] : null;
        var timed = single is "video" or "audio";
        var visual = kinds.All(k => k is "video" or "image");
        string? folder = null;

        ComboBox Combo(string header, IEnumerable<(string Key, string Label)> options)
        {
            var c = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (key, label) in options) c.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            c.SelectedIndex = 0;
            return c;
        }
        static string Picked(ComboBox c) => (string)((ComboBoxItem)c.SelectedItem).Tag;

        var format = Combo("Format", ExportSpec.FormatsFor(kinds));
        var size = Combo("Size", [("", "Original size"), ("2160", "4K (2160p)"), ("1080", "1080p"), ("720", "720p"), ("480", "480p")]);
        var limit = Combo("Fit under", [("", "No limit"), ("10", "10 MB"), ("25", "25 MB"), ("50", "50 MB"), ("100", "100 MB"), ("500", "500 MB")]);
        var rotate = Combo("Rotate", [("0", "No"), ("90", "90° right"), ("180", "180°"), ("270", "90° left")]);
        var speed = Combo("Speed", [("1", "Normal"), ("0.5", "Half speed"), ("1.5", "1.5×"), ("2", "2×"), ("4", "4×")]);
        var copy = new CheckBox { Content = "Fast cut: no quality loss, may start a moment early" };
        var vertical = new CheckBox { Content = "Crop to vertical (9:16)" };
        var normalize = new CheckBox { Content = "Even out the loudness" };
        var silence = new CheckBox { Content = "Trim silence from both ends" };
        var mute = new CheckBox { Content = "Remove the sound" };
        var where = new TextBlock { Text = "Saved next to the original", Style = Ui.Style("SecondaryText"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var choose = new Button { Content = "Choose folder…", Style = Ui.Style("GlassButton"), Padding = new Thickness(12, 6, 12, 6) };
        choose.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync();
            if (picked is null) return;
            folder = picked; where.Text = picked;
        };

        var body = new StackPanel { Spacing = 12, MinWidth = 380 };
        body.Children.Add(new TextBlock
        {
            Text = items.Count == 1 ? items[0].Name : $"{items.Count:N0} files",
            Style = Ui.Style("SecondaryText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (start is not null || end is not null)
            body.Children.Add(new TextBlock { Text = $"Only the part from {Clock(start ?? 0)} to {(end is { } e ? Clock(e) : "the end")}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(format);
        var pair = new Grid { ColumnSpacing = 10 };
        pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(limit, 1); pair.Children.Add(size); pair.Children.Add(limit);
        if (visual) body.Children.Add(pair);
        if (start is not null || end is not null) body.Children.Add(copy);

        var more = new StackPanel { Spacing = 8 };
        if (visual)
        {
            var turn = new Grid { ColumnSpacing = 10 };
            turn.ColumnDefinitions.Add(new ColumnDefinition()); turn.ColumnDefinitions.Add(new ColumnDefinition());
            turn.Children.Add(rotate);
            if (single == "video") { Grid.SetColumn(speed, 1); turn.Children.Add(speed); }
            more.Children.Add(turn);
            more.Children.Add(vertical);
        }
        else if (timed) more.Children.Add(speed);
        if (timed) { more.Children.Add(normalize); more.Children.Add(silence); }
        if (single == "video") more.Children.Add(mute);
        if (more.Children.Count > 0)
            body.Children.Add(new Expander { Header = "More options", Content = more, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });

        var dest = new Grid { ColumnSpacing = 10 };
        dest.ColumnDefinitions.Add(new ColumnDefinition()); dest.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(choose, 1); dest.Children.Add(where); dest.Children.Add(choose);
        body.Children.Add(dest);
        var problem = new TextBlock { Foreground = Ui.Hex("#FF7A7A"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        body.Children.Add(problem);

        // Options that cannot apply to the chosen format are switched off instead of being silently ignored.
        void Sync()
        {
            var f = Picked(format);
            var soundOnly = f is "mp3" or "wav" or "m4a" or "flac";
            var sheet = f == "sheet";
            var moving = single == "video" && !soundOnly && !sheet && f != "gif";
            size.IsEnabled = !soundOnly && !sheet;
            limit.IsEnabled = single == "image" ? f is "jpg" or "same" : moving;
            rotate.IsEnabled = vertical.IsEnabled = !soundOnly && !sheet;
            speed.IsEnabled = !sheet;
            mute.IsEnabled = moving;
            normalize.IsEnabled = silence.IsEnabled = timed && !sheet && f != "gif";
            copy.IsEnabled = f == "same";
            if (!copy.IsEnabled) copy.IsChecked = false;
        }
        format.SelectionChanged += (_, _) => Sync();
        Sync();

        var dialog = new ContentDialog
        {
            Title = start is not null || end is not null ? "Export clip" : "Export", Content = body, PrimaryButtonText = "Export",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = root, RequestedTheme = ThemeEngine.Element(),
        };
        var queued = 0;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // The dialog stays open if the engine refuses, so the reason can be read and the choice changed.
            var wait = args.GetDeferral();
            try
            {
                var spec = new ExportSpec
                {
                    Format = Picked(format), Start = start, End = end,
                    Height = size.IsEnabled && int.TryParse(Picked(size), out var h) ? h : null,
                    TargetMb = limit.IsEnabled && double.TryParse(Picked(limit), System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : null,
                    Rotate = rotate.IsEnabled ? int.Parse(Picked(rotate)) : 0,
                    Speed = speed.IsEnabled ? double.Parse(Picked(speed), System.Globalization.CultureInfo.InvariantCulture) : 1,
                    Vertical = vertical.IsEnabled && vertical.IsChecked == true,
                    Normalize = normalize.IsEnabled && normalize.IsChecked == true,
                    TrimSilence = silence.IsEnabled && silence.IsChecked == true,
                    Mute = mute.IsEnabled && mute.IsChecked == true,
                    Copy = copy.IsChecked == true,
                };
                queued = (await AppRuntime.Current.Api!.StartExportAsync(items.Select(i => i.Id), spec, folder)).Count;
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                problem.Text = ex.Message; problem.Visibility = Visibility.Visible;
            }
            finally { wait.Complete(); }
        };
        await dialog.ShowAsync();
        if (queued > 0) App.Window?.WatchExports();
        return queued;
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
