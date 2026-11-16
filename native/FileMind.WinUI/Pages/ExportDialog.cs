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
        var ranged = start is not null || end is not null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var format = new ChipGroup(ExportSpec.FormatsFor(kinds));
        var size = new ChipGroup([("", "Original"), ("1080", "1080p"), ("720", "720p"), ("480", "480p")]);
        var limit = new ChipGroup([("", "No limit"), ("8", "8 MB"), ("25", "25 MB"), ("50", "50 MB"), ("custom", "Custom")]);
        var custom = new TextBox { PlaceholderText = "MB", Width = 90, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var rotate = new ChipGroup([("0", "None"), ("90", "90° right"), ("180", "180°"), ("270", "90° left")]);
        var speed = new ChipGroup([("0.5", "0.5×"), ("1", "1×"), ("1.5", "1.5×"), ("2", "2×"), ("4", "4×")], "1");
        var copy = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
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
            ToolTipService.SetToolTip(where, picked);
        };

        StackPanel Section(string label, UIElement content, out StackPanel box)
        {
            box = new StackPanel { Spacing = 7 };
            box.Children.Add(new TextBlock { Text = label, Style = Ui.Style("SectionLabel") });
            box.Children.Add(content);
            return box;
        }

        var body = new StackPanel { Spacing = 16 };
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock
        {
            Text = items.Count == 1 ? items[0].Name : $"{items.Count:N0} files", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("GlassTextPrimaryBrush"),
        });
        if (ranged)
            head.Children.Add(new TextBlock { Text = $"Only the part from {Clock(start ?? 0)} to {(end is { } e ? Clock(e) : "the end")}", Style = Ui.Style("SecondaryText"), FontSize = 12.5 });
        body.Children.Add(head);

        Section("Format", format.Panel, out var formatBox);
        body.Children.Add(formatBox);
        Section("Size", size.Panel, out var sizeBox);
        if (visual) body.Children.Add(sizeBox);
        var limitWrap = new Grid { ColumnSpacing = 8 };
        limitWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); limitWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        limitWrap.Children.Add(limit.Panel); Grid.SetColumn(custom, 1); limitWrap.Children.Add(custom);
        Section("Fit under", limitWrap, out var limitBox);
        if (visual) body.Children.Add(limitBox);
        limit.Changed += () => custom.Visibility = limit.Value == "custom" ? Visibility.Visible : Visibility.Collapsed;

        var fast = new Grid { ColumnSpacing = 12 };
        fast.ColumnDefinitions.Add(new ColumnDefinition()); fast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fast.Children.Add(new TextBlock { Text = "Fast cut (no quality loss)", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("GlassTextPrimaryBrush") });
        copy.HorizontalAlignment = HorizontalAlignment.Right; copy.Margin = new Thickness(0);
        Grid.SetColumn(copy, 1); fast.Children.Add(copy);
        if (ranged) body.Children.Add(fast);

        var more = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed };
        if (visual) { Section("Rotate", rotate.Panel, out var rb); more.Children.Add(rb); }
        if (timed) { Section("Speed", speed.Panel, out var sb); more.Children.Add(sb); }
        var checks = new StackPanel { Spacing = 2 };
        if (visual) checks.Children.Add(vertical);
        if (timed) { checks.Children.Add(normalize); checks.Children.Add(silence); }
        if (single == "video") checks.Children.Add(mute);
        if (checks.Children.Count > 0) more.Children.Add(checks);
        if (more.Children.Count > 0)
        {
            var chev = new LineIcon { Kind = "chevdown", Size = 12 };
            var toggle = new Button { Style = Ui.Style("GlassButton"), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new TextBlock { Text = "More options", FontSize = 12.5, Foreground = Ui.Brush("GlassTextSecondaryBrush") }); row.Children.Add(chev);
            toggle.Content = row;
            toggle.Click += (_, _) =>
            {
                var open = more.Visibility == Visibility.Collapsed;
                more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chev.Kind = open ? "chevup" : "chevdown";
            };
            body.Children.Add(toggle); body.Children.Add(more);
        }

        body.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        var dest = new Grid { ColumnSpacing = 10 };
        dest.ColumnDefinitions.Add(new ColumnDefinition()); dest.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(choose, 1); dest.Children.Add(where); dest.Children.Add(choose);
        body.Children.Add(dest);

        // Options that cannot apply to the chosen format are hidden or switched off instead of being silently ignored.
        bool rotOk = false, speedOk = true;
        void Sync()
        {
            var f = format.Value;
            var soundOnly = f is "mp3" or "wav" or "m4a" or "flac";
            var sheet = f == "sheet";
            var moving = single == "video" && !soundOnly && !sheet && f != "gif";
            var sizeOk = !soundOnly && !sheet;
            var limitOk = single == "image" ? f is "jpg" or "same" : moving;
            sizeBox.Visibility = visual && sizeOk ? Visibility.Visible : Visibility.Collapsed;
            limitBox.Visibility = visual && limitOk ? Visibility.Visible : Visibility.Collapsed;
            size.IsEnabled = sizeOk; limit.IsEnabled = limitOk;
            rotate.IsEnabled = vertical.IsEnabled = sizeOk;
            speed.IsEnabled = !sheet; rotOk = visual && sizeOk; speedOk = timed && !sheet;
            mute.IsEnabled = moving;
            normalize.IsEnabled = silence.IsEnabled = timed && !sheet && f != "gif";
            copy.IsEnabled = f == "same";
            if (!copy.IsEnabled) copy.IsOn = false;
        }
        format.Changed += Sync;
        Sync();

        var queued = 0;
        var done = await Dialogs.ShowAsync(root, ranged ? "Export clip" : "Export", body, "Export", "Cancel", async () =>
        {
            // The dialog stays open if the engine refuses, so the reason can be read and the choice changed.
            try
            {
                double? mb = null;
                if (limitBox.Visibility == Visibility.Visible)
                {
                    if (limit.Value == "custom")
                    {
                        if (!double.TryParse(custom.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out var m) || m <= 0) return "Type a size in MB, or pick another limit.";
                        mb = m;
                    }
                    else if (double.TryParse(limit.Value, System.Globalization.NumberStyles.Float, inv, out var m2)) mb = m2;
                }
                var spec = new ExportSpec
                {
                    Format = format.Value, Start = start, End = end,
                    Height = sizeBox.Visibility == Visibility.Visible && int.TryParse(size.Value, out var h) ? h : null,
                    TargetMb = mb,
                    Rotate = rotOk && int.TryParse(rotate.Value, out var rot) ? rot : 0,
                    Speed = speedOk && double.TryParse(speed.Value, System.Globalization.NumberStyles.Float, inv, out var sp) ? sp : 1,
                    Vertical = vertical.IsEnabled && vertical.IsChecked == true,
                    Normalize = normalize.IsEnabled && normalize.IsChecked == true,
                    TrimSilence = silence.IsEnabled && silence.IsChecked == true,
                    Mute = mute.IsEnabled && mute.IsChecked == true,
                    Copy = copy.IsOn,
                };
                queued = (await AppRuntime.Current.Api!.StartExportAsync(items.Select(i => i.Id), spec, folder)).Count;
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }, width: 480);
        if (done && queued > 0) App.Window?.WatchExports();
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
