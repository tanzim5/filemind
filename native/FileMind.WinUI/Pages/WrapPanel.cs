using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FileMind.Pages;

/// <summary>Flows small chips left to right and wraps them onto new lines. A plain panel (no ItemsRepeater), so it can be
/// moved between parents, which the filter rail does.</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 6;

    /// <summary>When above zero, chips sit in equal columns that fill the width, so rows line up with no gaps.</summary>
    public int Columns { get; set; }

    /// <summary>Flowing chips keep their natural widths but each full row is stretched to the edge, so the block reads
    /// as a tidy rectangle instead of a ragged column (a short last row keeps natural widths).</summary>
    public bool Justify { get; set; }

    private IEnumerable? _items;

    /// <summary>Replaces the chips with these elements.</summary>
    public IEnumerable? ItemsSource
    {
        get => _items;
        set
        {
            _items = value;
            Children.Clear();
            if (value is null) return;
            foreach (var o in value) if (o is UIElement e) Children.Add(e);
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Columns > 0)
        {
            var w = double.IsInfinity(available.Width) ? 300 : available.Width;
            var cell = (w - Spacing * (Columns - 1)) / Columns;
            double cy = 0, ch = 0; int i = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(cell, double.PositiveInfinity));
                ch = Math.Max(ch, child.DesiredSize.Height);
                if (++i % Columns == 0) { cy += ch + Spacing; ch = 0; }
            }
            if (ch > 0) cy += ch; else if (cy > 0) cy -= Spacing;
            return new Size(w, cy);
        }
        double x = 0, y = 0, rowH = 0, maxW = 0;
        var width = double.IsInfinity(available.Width) ? 300 : available.Width;
        foreach (var child in Children)
        {
            // Natural width: a chip that stretches would otherwise claim the whole line.
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.DesiredSize.Width > width) child.Measure(new Size(width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > width) { y += rowH + Spacing; x = 0; rowH = 0; }
            x += s.Width + Spacing;
            rowH = Math.Max(rowH, s.Height);
            maxW = Math.Max(maxW, x - Spacing);
        }
        return new Size(maxW, y + rowH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Columns > 0)
        {
            var cell = (final.Width - Spacing * (Columns - 1)) / Columns;
            double cy = 0, ch = 0; int i = 0;
            foreach (var child in Children)
            {
                ch = Math.Max(ch, child.DesiredSize.Height);
                child.Arrange(new Rect((i % Columns) * (cell + Spacing), cy, cell, child.DesiredSize.Height));
                if (++i % Columns == 0) { cy += ch + Spacing; ch = 0; }
            }
            return final;
        }
        // Lay the chips out into rows first, then place each row (stretched when justified).
        var rows = new List<List<UIElement>>(); var cur = new List<UIElement>(); double rx = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            if (cur.Count > 0 && rx + w > final.Width) { rows.Add(cur); cur = []; rx = 0; }
            cur.Add(child); rx += w + Spacing;
        }
        if (cur.Count > 0) rows.Add(cur);
        double y = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var natural = row.Sum(c => c.DesiredSize.Width) + Spacing * (row.Count - 1);
            var last = r == rows.Count - 1;
            var extra = Justify && (!last || (row.Count > 1 && natural > final.Width * 0.7)) ? Math.Max(0, final.Width - natural) / row.Count : 0;
            double x = 0, h = row.Max(c => c.DesiredSize.Height);
            foreach (var c in row)
            {
                var w = c.DesiredSize.Width + extra;
                c.Arrange(new Rect(x, y, w, c.DesiredSize.Height));
                x += w + Spacing;
            }
            y += h + Spacing;
        }
        return final;
    }
}
