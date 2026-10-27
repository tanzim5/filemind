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
            child.Measure(new Size(width, double.PositiveInfinity));
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
        double x = 0, y = 0, rowH = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += rowH + Spacing; x = 0; rowH = 0; }
            child.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Spacing;
            rowH = Math.Max(rowH, s.Height);
        }
        return final;
    }
}
