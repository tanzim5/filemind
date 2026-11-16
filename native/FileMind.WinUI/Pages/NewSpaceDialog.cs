using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileMind.Pages;

/// <summary>The New space dialog: a name, short descriptions of what belongs ("cats", "dogs at the beach") shown as chips, and a live
/// preview of the files that match. "Create with N files" makes the space and fills it; "Create empty" makes only the space.</summary>
internal static class NewSpaceDialog
{
    /// <summary>True when a space was created.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, ApiClient api)
    {
        var name = new TextBox { PlaceholderText = "Name this space (optional when you describe it)" };
        var chips = new WrapPanel { Spacing = 6 };
        var entry = new TextBox { PlaceholderText = "cats, dogs at the beach, a question…" };
        var status = new TextBlock { Style = Ui.Style("SecondaryText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var thumbs = new WrapPanel { Spacing = 6, Columns = 6 };
        var descriptions = new List<string>();
        Button? primary = null;
        MatchState state = new([], 0, false);
        var cts = new CancellationTokenSource();

        List<string> Current()
        {
            var all = new List<string>(descriptions);
            if (entry.Text.Trim().Length > 0) all.Add(entry.Text);
            return SpaceDescriptions.Clean(all);
        }

        void Paint()
        {
            if (primary is null) return;
            var n = state.Count;
            primary.Content = n > 0 ? $"Create with {n:N0} {(n == 1 ? "file" : "files")}" : "Create";
            primary.IsEnabled = n > 0;
        }

        async void Refresh()
        {
            cts.Cancel(); cts = new CancellationTokenSource();
            var token = cts.Token;
            var found = Current();
            if (found.Count == 0)
            {
                state = new([], 0, false); thumbs.Children.Clear();
                status.Text = "Describe what belongs and the matching files appear here.";
                Paint(); return;
            }
            status.Text = "Looking…";
            try
            {
                await Task.Delay(380, token);
                var r = await api.MatchSpacesAsync(found, token);
                state = new(r.FileIds, r.Count, true);
                thumbs.Children.Clear();
                foreach (var f in r.Preview.Take(12)) thumbs.Children.Add(Thumb(api, f));
                status.Text = r.Count == 0 ? "Nothing in your library fits that yet. Try other words, or create it empty."
                    : r.Count >= 300 ? "300 files match (the most a new space starts with)" : $"{r.Count:N0} {(r.Count == 1 ? "file matches" : "files match")}";
                Paint();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Log("space-match", ex, ex.Message); status.Text = "Couldn't look just now."; }
        }

        void Rebuild()
        {
            chips.Children.Clear();
            foreach (var d in descriptions)
            {
                var text = d;
                var x = new Button { Style = Ui.Style("GlassButton"), Padding = new Thickness(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, Content = new LineIcon { Kind = "close", Size = 9 } };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(x, "Remove " + text);
                x.Click += (_, _) => { descriptions.Remove(text); Rebuild(); Refresh(); };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(x);
                chips.Children.Add(new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 4, 3), Background = Ui.Brush("AccentTintBrush"), Child = row });
            }
        }

        void Commit()
        {
            var t = entry.Text.Trim().TrimEnd(',').Trim();
            entry.Text = "";
            if (t.Length == 0) return;
            descriptions = SpaceDescriptions.Clean([.. descriptions, t]);
            Rebuild(); Refresh();
        }

        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Commit(); }
        };
        entry.TextChanged += (_, _) =>
        {
            if (entry.Text.EndsWith(',')) Commit(); else Refresh();
        };
        name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; entry.Focus(FocusState.Programmatic); } };

        // Development aid for screenshots: FILEMIND_DEV_DESC="cats,dogs" starts with those descriptions.
        if (Environment.GetEnvironmentVariable("FILEMIND_DEV_DESC") is { Length: > 0 } dev)
        { descriptions = SpaceDescriptions.Clean(dev.Split(',')); Rebuild(); }

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Labelled("Name", name));
        var what = new StackPanel { Spacing = 8 };
        what.Children.Add(Label("What belongs here?"));
        what.Children.Add(chips);
        what.Children.Add(entry);
        body.Children.Add(what);
        var preview = new StackPanel { Spacing = 8 };
        preview.Children.Add(status);
        preview.Children.Add(thumbs);
        body.Children.Add(preview);

        async Task<string?> Create(bool withFiles)
        {
            var found = Current();
            var title = SpaceDescriptions.NameFor(name.Text, found);
            if (title.Length == 0) return "Give the space a name or a description.";
            var ids = new List<int>();
            if (withFiles)
            {
                var r = await api.MatchSpacesAsync(found);
                ids = r.FileIds;
                if (ids.Count == 0) return "Nothing fits those descriptions yet.";
            }
            await api.CreateProjectAsync(title, ids);
            return null;
        }

        if (descriptions.Count == 0) status.Text = "Describe what belongs and the matching files appear here.";
        var ok = await Dialogs.ShowAsync(root, "New space", body, "Create", "Cancel", () => Create(true), width: 480, enterConfirms: false,
            extra: "Create empty", onExtra: () => Create(false),
            gotPrimary: b => { primary = b; Paint(); if (descriptions.Count > 0) Refresh(); });
        cts.Cancel();
        return ok;
    }

    private sealed record MatchState(List<int> Ids, int Count, bool Known);

    private static TextBlock Label(string t) => new() { Text = t, FontSize = 12.5, Style = Ui.Style("SecondaryText") };

    private static StackPanel Labelled(string label, UIElement c)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(Label(label)); s.Children.Add(c);
        return s;
    }

    private static UIElement Thumb(ApiClient api, FileItem f)
    {
        var cell = new Grid { Height = 56, CornerRadius = new CornerRadius(6), Background = Ui.Brush("GlassFillStrongBrush") };
        if (f.HasThumb) cell.Children.Add(new Image { Source = new BitmapImage(api.ThumbUri(f.Id)) { DecodePixelWidth = 140 }, Stretch = Stretch.UniformToFill });
        else cell.Children.Add(new LineIcon { Kind = f.Kind == "audio" ? "f-music" : "f-file", Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        ToolTipService.SetToolTip(cell, f.Name);
        return cell;
    }
}
