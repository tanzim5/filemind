using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FileMind;

/// <summary>The window's backdrop: thin, clear acrylic with only a hint of tint, so the wallpaper shows through the sidebar
/// with its colours instead of a grey wash. (The stock acrylic adds a heavy luminosity layer, which is what made the glass look muddy.)</summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private ICompositionSupportsSystemBackdrop? _target;
    private bool _dark = true;

    /// <summary>Re-tints for the current theme (dark or light).</summary>
    public void Update(bool dark)
    {
        _dark = dark;
        Apply();
    }

    private void Apply()
    {
        var c = _controller;
        if (c is null) return;
        try
        {
            c.Kind = DesktopAcrylicKind.Thin;
            c.TintColor = _dark ? Windows.UI.Color.FromArgb(255, 0x0B, 0x0C, 0x10) : Windows.UI.Color.FromArgb(255, 0xF5, 0xF5, 0xF7);
            c.TintOpacity = _dark ? 0.10f : 0.08f;
            c.LuminosityOpacity = _dark ? 0.55f : 0.72f;
            c.FallbackColor = _dark ? Windows.UI.Color.FromArgb(255, 0x10, 0x11, 0x16) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF5);
        }
        catch (Exception ex) { App.Log("backdrop", ex, ex.Message); }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _target = connectedTarget;
        _config = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_config);
        Apply();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        try { _controller?.RemoveSystemBackdropTarget(disconnectedTarget); _controller?.Dispose(); } catch (Exception) { }
        _controller = null; _config = null; _target = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_config is not null && _target == target) _config = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
    }
}
