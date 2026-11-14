using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace StealthWorkspace.Services;

internal static class UiCapture
{
    // Render only synthetic test visuals, without driving or capturing the desktop.
    internal static async Task<byte[]> PixelsAsync(UIElement element)
    {
        if (!SmokeTest.Enabled) throw new InvalidOperationException("Test-only capture");
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        return (await bitmap.GetPixelsAsync()).ToArray();
    }

    internal static async Task SaveAsync(UIElement element, string name)
    {
        if (!SmokeTest.Enabled) throw new InvalidOperationException("Test-only capture");
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        string path = Path.Combine(Path.GetDirectoryName(SmokeTest.OutputPath!)!, name);
        using var file = File.Create(path);
        using var random = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        SmokeTest.Report("visual capture: " + name);
    }
}
