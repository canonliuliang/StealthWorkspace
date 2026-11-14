using System.Security.Cryptography;
using StealthWorkspace.Models;
using StealthWorkspace.Pages;
using Windows.Graphics.Imaging;

namespace StealthWorkspace.Services;

internal static class ImageExperienceAcceptance
{
    internal static async Task RunAsync(VaultPage gallery, Action<VaultItem> open, Func<PlayerPage?> current, Func<VaultPage?> currentGallery)
    {
        // Import deliberately rejects files inside the vault data root.
        string root = Path.Combine(Path.GetDirectoryName(SmokeTest.OutputPath!)!, "r11-image-fixtures");
        Directory.CreateDirectory(root);
        string[] paths = { Path.Combine(root, "switch-01.png"), Path.Combine(root, "switch-02.png"), Path.Combine(root, "switch-03.jpg"), Path.Combine(root, "switch-04-corrupt.png"), Path.Combine(root, "other-image.png") };
        await MediaAcceptance.CreateImageAsync(paths[0], 640, 480, BitmapEncoder.PngEncoderId);
        await MediaAcceptance.CreateImageAsync(paths[1], 800, 12000, BitmapEncoder.PngEncoderId);
        await MediaAcceptance.CreateImageAsync(paths[2], 3600, 2400, BitmapEncoder.JpegEncoderId);
        await File.WriteAllTextAsync(paths[3], "intentionally invalid test image");
        await MediaAcceptance.CreateImageAsync(paths[4], 80, 80, BitmapEncoder.PngEncoderId);
        var imported = await gallery.ImportFilesAsync(paths);
        Require(imported.Imported == paths.Length && imported.Failed == 0, "test images imported, originals retained");
        gallery.SetSmokeFilter("switch-", 2);
        var items = gallery.DisplayedItems.ToArray();
        Require(items.Length == 4 && items.All(item => item.MediaType == "Image"), "image queue follows filtered gallery");
        open(items[0]);
        var page = current() ?? throw new InvalidOperationException("Viewer not opened");
        for (int i = 0; i < 100 && !page.IsImageReady && page.MediaError == null; i++) await Task.Delay(50);
        Require(page.IsImageReady && page.ImageCount == 4 && page.ImageNavigationEnabled == (false, true), "first image ready; previous disabled");
        await page.SelectImageAsync(1);
        await Task.Delay(220);
        byte[] expectedPixels = await page.CaptureImageViewportPixelsAsync();
        Require(page.ImageDecodedSize == (800, 12000), "long image retains readable pixels within decode budget");
        Require(page.IsWidthReading && page.ImageZoom == 1, "long image starts in width reading without magnification limit");
        await UiCapture.SaveAsync(page, "r11-image-viewer.png");
        page.FitImageToWidth();
        await Task.Delay(600);
        Require(page.IsWidthReading && page.ImageDisplayedWidth >= page.ImageViewportWidth - 32,
            "fit width spans the viewport even for a 15:1 long image");
        await UiCapture.SaveAsync(page, "r11-image-fit-width.png");
        byte[] viewport = await page.CaptureImageViewportPixelsAsync();
        int painted = 0;
        for (int offset = 0; offset < viewport.Length; offset += 4)
            if (viewport[offset] + viewport[offset + 1] + viewport[offset + 2] > 30) painted++;
        Require(viewport.Length > 0 && painted / (viewport.Length / 4.0) > 0.7, "fit width keeps image inside visible viewport instead of zooming into empty margin");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task slow = page.SelectImageAsync(2);
        Task middle = page.SelectImageAsync(0);
        Task latest = page.SelectImageAsync(1);
        await Task.WhenAll(slow, middle, latest);
        await Task.Delay(220);
        byte[] actualPixels = await page.CaptureImageViewportPixelsAsync();
        await UiCapture.SaveAsync(page, "r11-image-rapid.png");
        double meanDifference = expectedPixels.Length == actualPixels.Length && actualPixels.Length > 0
            ? expectedPixels.Zip(actualPixels, (a, b) => Math.Abs(a - b)).Average() : double.PositiveInfinity;
        SmokeTest.Report($"image render diagnostic: ready={page.IsImageReady}; error={page.MediaError != null}; correctId={page.CurrentImageId == items[1].Id}; bytes={expectedPixels.Length}/{actualPixels.Length}; mean difference={meanDifference:F4}");
        Require(page.IsImageReady && page.MediaError == null && page.CurrentImageId == items[1].Id && page.ImageZoom == 1 &&
            meanDifference < 1, "rapid switching renders latest image pixels, no stale completion");
        SmokeTest.Report($"image switching: rapid 3 requests including large image={watch.Elapsed.TotalMilliseconds:F0}ms (includes 220ms render settle)");
        await page.SelectImageAsync(3);
        Require(!page.IsImageReady && page.MediaError != null && page.ImageNavigationEnabled == (true, false), "corrupt image reports error; navigation remains available");
        await page.SelectImageAsync(2);
        Require(page.IsImageReady && page.MediaError == null, "next valid image recovers after decode failure");
        page.ReturnToGallery();
        await Task.Delay(300);
        var restored = currentGallery() ?? throw new InvalidOperationException("Gallery return failed");
        var state = restored.CaptureViewState();
        Require(state.Search == "switch-" && state.FilterIndex == 2 && state.SelectedId == items[2].Id && restored.DisplayedItems.Count() == 4,
            "return restores search, filter and current selection");
        open(items[0]);
        page = current()!;
        Task pending = page.SelectImageAsync(2);
        VaultService.Instance.Lock();
        await pending;
        await Task.Delay(200);
        Require(!page.IsImageReady && page.CurrentImageId == null, "lock during image load clears private content");
    }

    private static void Require(bool condition, string message)
    {
        SmokeTest.Report("image experience: " + message + "=" + condition);
        if (!condition) throw new InvalidOperationException(message);
    }
}
