using System.ComponentModel;
using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileMind.Pages;

/// <summary>One tile in the grid. The thumbnail is only requested when the tile is actually shown on screen.</summary>
public sealed class TileVm(FileItem item, ApiClient api) : INotifyPropertyChanged
{
    private BitmapImage? _thumb;
    private bool _checked, _hover, _focused, _anyChecked, _always;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }

    public FileItem Item { get; } = item;
    public string Name => Item.Name;
    public bool HasThumb => Item.HasThumb;
    public Visibility ThumbVisible => Item.HasThumb ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisible => Item.HasThumb || Item.Kind == "audio" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AudioVisible => Item.Kind == "audio" ? Visibility.Visible : Visibility.Collapsed;
    public string IconKind => Item.Kind switch { "audio" => "music", "video" => "video", _ => "file" };
    public Visibility BadgeVisible => Item.DurationText.Length > 0 && Item.Kind != "audio" ? Visibility.Visible : Visibility.Collapsed;
    public string Badge => Item.DurationText;

    /// <summary>Width over height, used by the masonry and row layouts.</summary>
    public double Aspect
    {
        get
        {
            if (Item.ThumbWidth is > 0 && Item.ThumbHeight is > 0) return (double)Item.ThumbWidth.Value / Item.ThumbHeight.Value;
            if (Item.Width is > 0 && Item.Height is > 0) return (double)Item.Width.Value / Item.Height.Value;
            return Item.Kind == "audio" ? 1.6 : 1.35;
        }
    }

    public bool Checked { get => _checked; set { if (_checked != value) { _checked = value; Raise(nameof(Checked), nameof(CheckVisible), nameof(CheckBrush), nameof(RingVisibleCheck), nameof(RingVisible)); } } }
    public bool Hover { get => _hover; set { if (_hover != value) { _hover = value; Raise(nameof(CheckVisible), nameof(NameVisible)); } } }
    public bool Focused { get => _focused; set { if (_focused != value) { _focused = value; Raise(nameof(RingVisible)); } } }
    public bool AlwaysName { get => _always; set { if (_always != value) { _always = value; Raise(nameof(NameVisible)); } } }
    public bool AnyChecked { get => _anyChecked; set { if (_anyChecked != value) { _anyChecked = value; Raise(nameof(CheckVisible)); } } }

    public Visibility CheckVisible => _checked || _anyChecked || (_hover && Item.Kind != "audio") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NameVisible => Item.Kind != "audio" && (_hover || _focused || _always) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RingVisible => _focused || _checked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RingVisibleCheck => _checked ? Visibility.Visible : Visibility.Collapsed;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush Accent = new(Windows.UI.Color.FromArgb(255, 255, 90, 31));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush Clear = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    public Microsoft.UI.Xaml.Media.Brush CheckBrush => _checked ? Accent : Clear;

    public BitmapImage? Thumb
    {
        get
        {
            if (!Item.HasThumb) return null;
            return _thumb ??= new BitmapImage(api.ThumbUri(Item.Id)) { DecodePixelWidth = 420 };
        }
    }
}
