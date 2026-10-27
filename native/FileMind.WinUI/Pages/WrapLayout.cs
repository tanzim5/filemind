using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FileMind.Pages;

/// <summary>Flows small chips left to right and wraps them onto new lines.</summary>
public sealed class WrapLayout : NonVirtualizingLayout
{
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size available)
    {
        double x = 0, y = 0, rowH = 0, maxW = 0;
        var width = double.IsInfinity(available.Width) ? 300 : available.Width;
        foreach (var child in context.Children)
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

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size final)
    {
        double x = 0, y = 0, rowH = 0;
        foreach (var child in context.Children)
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
