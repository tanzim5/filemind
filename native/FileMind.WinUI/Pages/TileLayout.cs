using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FileMind.Pages;

public enum TileMode { Masonry, Rows, Contact, Story }

/// <summary>Lays tiles out as a masonry, justified rows, a contact sheet or a storyboard, and only builds the ones
/// near the screen so thousands of files scroll smoothly.</summary>
public sealed class TileLayout : VirtualizingLayout
{
    private TileMode _mode = TileMode.Masonry;
    private double _target = 260, _gap = 12;
    private Rect[] _rects = [];
    private double _width, _height;
    private int _count = -1;
    private bool _dirty = true;
    private readonly HashSet<int> _realized = [];

    public TileMode Mode { get => _mode; set { if (_mode != value) { _mode = value; _dirty = true; InvalidateMeasure(); } } }
    public double Target { get => _target; set { if (Math.Abs(_target - value) > 0.5) { _target = value; _dirty = true; InvalidateMeasure(); } } }
    public double Gap { get => _gap; set { if (Math.Abs(_gap - value) > 0.5) { _gap = value; _dirty = true; InvalidateMeasure(); } } }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        _dirty = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size available)
    {
        var width = double.IsInfinity(available.Width) || available.Width < 50 ? 900 : available.Width;
        if (_dirty || Math.Abs(width - _width) > 0.5 || context.ItemCount != _count)
        {
            Compute(context, width);
            _dirty = false;
        }

        var view = context.RealizationRect;
        var pad = Math.Max(view.Height, 600) * 0.6;
        var top = view.Y - pad;
        var bottom = view.Y + view.Height + pad;

        var wanted = new HashSet<int>();
        for (var i = 0; i < _rects.Length; i++)
        {
            var r = _rects[i];
            if (r.Bottom < top) continue;
            if (r.Y > bottom && _mode != TileMode.Masonry) break;   // rows and grids are ordered top to bottom
            if (r.Y > bottom) continue;
            wanted.Add(i);
        }
        foreach (var i in _realized.Where(i => !wanted.Contains(i)).ToList())
        {
            if (i < context.ItemCount) context.RecycleElement(context.GetOrCreateElementAt(i));
            _realized.Remove(i);
        }
        foreach (var i in wanted)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(_rects[i].Width, _rects[i].Height));
            _realized.Add(i);
        }
        return new Size(width, _height);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size final)
    {
        foreach (var i in _realized)
        {
            if (i >= _rects.Length || i >= context.ItemCount) continue;
            context.GetOrCreateElementAt(i).Arrange(_rects[i]);
        }
        return final;
    }

    private void Compute(VirtualizingLayoutContext context, double width)
    {
        _width = width;
        _count = context.ItemCount;
        _rects = new Rect[_count];
        var gap = _gap;

        double AspectOf(int i) => context.GetItemAt(i) is TileVm t ? t.Aspect : 1.35;

        switch (_mode)
        {
            case TileMode.Masonry:
            {
                var cn = Math.Max(2, (int)Math.Round((width + gap) / (_target + gap)));
                var w = Math.Floor((width - gap * (cn - 1)) / cn);
                var heights = new double[cn];
                for (var i = 0; i < _count; i++)
                {
                    var h = Math.Round(Math.Clamp(w / AspectOf(i), w * 0.42, w * 1.9));
                    var c = 0;
                    for (var k = 1; k < cn; k++) if (heights[k] < heights[c]) c = k;
                    _rects[i] = new Rect(c * (w + gap), heights[c], w, h);
                    heights[c] += h + gap;
                }
                _height = heights.Max();
                break;
            }
            case TileMode.Rows:
            {
                var target = Math.Round(_target * 0.78);
                double y = 0;
                var start = 0;
                double sum = 0;
                for (var i = 0; i < _count; i++)
                {
                    sum += Math.Clamp(AspectOf(i), 0.55, 2.4);
                    var n = i - start + 1;
                    if (sum * target + gap * (n - 1) >= width)
                    {
                        var h = (width - gap * (n - 1)) / sum;
                        y += PlaceRow(context, start, i, y, h, gap, justify: true) + gap;
                        start = i + 1;
                        sum = 0;
                    }
                }
                if (start < _count)
                {
                    var h = Math.Min(target, sum > 0 ? width / sum : target);
                    y += PlaceRow(context, start, _count - 1, y, h, gap, justify: false) + gap;
                }
                _height = Math.Max(0, y - gap);
                break;
            }
            default:
            {
                var contact = _mode == TileMode.Contact;
                var target = contact ? Math.Min(_target * 0.68, 245) : Math.Min(_target * 1.32, 430);
                var cn = Math.Max(2, (int)Math.Round((width + gap) / (target + gap)));
                var w = Math.Floor((width - gap * (cn - 1)) / cn);
                var h = Math.Round(contact ? w : w * 9 / 16);
                for (var i = 0; i < _count; i++)
                    _rects[i] = new Rect(i % cn * (w + gap), i / cn * (h + gap), w, h);
                _height = _count == 0 ? 0 : Math.Ceiling(_count / (double)cn) * (h + gap) - gap;
                break;
            }
        }
    }

    private double PlaceRow(VirtualizingLayoutContext context, int first, int last, double y, double h, double gap, bool justify)
    {
        h = Math.Floor(h);
        double x = 0;
        for (var i = first; i <= last; i++)
        {
            var a = Math.Clamp(context.GetItemAt(i) is TileVm t ? t.Aspect : 1.35, 0.55, 2.4);
            var w = Math.Floor(a * h);
            _rects[i] = new Rect(x, y, w, h);
            x += w + gap;
        }
        return h;
    }
}
