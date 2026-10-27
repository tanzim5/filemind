using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FileMind.Pages;

/// <summary>Makes any element a drag source that hands the real file (and optionally text) to whatever it is dropped on.</summary>
public static class DragHelper
{
    public static void Attach(UIElement el, Func<string?> path, Func<string?>? text = null)
    {
        el.CanDrag = true;
        el.DragStarting += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var p = path();
                var t = text?.Invoke();
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    e.Data.SetStorageItems([await StorageFile.GetFileFromPathAsync(p)]);
                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                }
                else if (string.IsNullOrEmpty(t)) { e.Cancel = true; return; }
                if (!string.IsNullOrEmpty(t)) e.Data.SetText(t);
            }
            catch (Exception ex) { App.Log("drag", ex, ex.Message); e.Cancel = true; }
            finally { deferral.Complete(); }
        };
    }
}
