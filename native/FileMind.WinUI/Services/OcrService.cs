using FileMind.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace FileMind;

/// <summary>Reads the text in pictures with Windows' own OCR (on this PC, nothing is uploaded) and hands it to the engine,
/// so a search for words that appear inside a screenshot, a slide or a render finds it. It works in small batches, one
/// picture at a time, and waits while folders are being read.</summary>
internal static class OcrService
{
    private static CancellationTokenSource? _cts;

    public static void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        OcrEngine? engine;
        try { engine = OcrEngine.TryCreateFromUserProfileLanguages(); } catch (Exception) { engine = null; }
        if (engine is null) return;                          // no language pack for OCR on this PC
        await Task.Delay(TimeSpan.FromSeconds(45), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rt = AppRuntime.Current;
                if (rt.Api is not { } api || rt.Settings.OcrMode == "off") { await Task.Delay(15000, ct); continue; }
                if ((await api.GetStatusAsync(ct)).Running) { await Task.Delay(20000, ct); continue; }
                var batch = await api.GetOcrPendingAsync(rt.Settings.OcrMode, 10, ct);
                if (batch.Count == 0) { await Task.Delay(60000, ct); continue; }
                foreach (var (id, path) in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var text = "";
                    try { text = await ReadAsync(engine, path); } catch (Exception ex) { Diag.Log("ocr", ex); }
                    await api.StoreOcrAsync(id, text, ct);     // even an empty result is kept, so it is not asked for again
                    await Task.Delay(120, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Diag.Log("ocr.loop", ex); try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { return; } }
        }
    }

    private static async Task<string> ReadAsync(OcrEngine engine, string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double k = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * k)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * k)) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                                                               ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (await engine.RecognizeAsync(bitmap)).Text ?? "";
    }
}
