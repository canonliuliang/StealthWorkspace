using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using StealthWorkspace.Models;
using StealthWorkspace.Pages;
using Windows.Graphics.Imaging;

namespace StealthWorkspace.Services;

internal static class ImageExperienceAcceptance
{
    internal static async Task RunAsync(VaultPage gallery, Action<VaultItem> open, Func<PlayerPage?> current, Func<VaultPage?> currentGallery, Action openFromSidebar)
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
        gallery.SetSmokeFilter("switch-", 2, sort: 1);
        var items = gallery.DisplayedItems.ToArray();
        Require(items.Length == 4 && items.All(item => item.MediaType == "Image"), "image queue follows filtered gallery");
        open(items[0]);
        var page = current() ?? throw new InvalidOperationException("Viewer not opened");
        for (int i = 0; i < 100 && !page.IsImageReady && page.MediaError == null; i++) await Task.Delay(50);
        Require(page.IsImageReady && page.ImageCount == 4 && page.NavigationEnabled == (false, true), "first image ready; previous disabled");
        Require(!page.HasPlaybackElement, "viewing an image leaves the native playback control uncreated");
        await page.SelectMediaAsync(1);
        var reference = await WaitForLongImageViewportAsync(page);
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
        page.RotateImage();
        page.UpdateLayout();
        Require(!page.IsWidthReading && page.ImageDisplayedWidth >= page.ImageViewportWidth - 32,
            "rotated long image fits horizontally without a vertical scroll extent");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task slow = page.SelectMediaAsync(2);
        Task middle = page.SelectMediaAsync(0);
        Task latest = page.SelectMediaAsync(1);
        await Task.WhenAll(slow, middle, latest);
        await WaitForLongImageViewportAsync(page, reference);
        await UiCapture.SaveAsync(page, "r11-image-rapid.png");
        Require(page.IsImageReady && page.MediaError == null && page.CurrentMediaId == items[1].Id && page.ImageZoom == 1,
            "rapid switching renders latest image pixels, no stale completion");
        SmokeTest.Report($"image switching: rapid 3 requests including large image={watch.Elapsed.TotalMilliseconds:F0}ms (includes viewport verification and screenshot)");
        await page.SelectMediaAsync(3);
        Require(!page.IsImageReady && page.MediaError != null && page.NavigationEnabled == (true, false), "corrupt image reports error; navigation remains available");
        await page.SelectMediaAsync(2);
        Require(page.IsImageReady && page.MediaError == null, "next valid image recovers after decode failure");
        page.ReturnToGallery();
        await Task.Delay(300);
        var restored = currentGallery() ?? throw new InvalidOperationException("Gallery return failed");
        var state = restored.CaptureViewState();
        Require(state.Search == "switch-" && state.FilterIndex == 2 && state.SelectedId == items[2].Id && restored.DisplayedItems.Count() == 4,
            "return restores search, filter and current selection");
        openFromSidebar();
        page = current()!;
        for (int i = 0; i < 100 && !page.IsImageReady && page.MediaError == null; i++) await Task.Delay(50);
        Require(page.IsImageReady && page.CurrentMediaId == items[2].Id, "sidebar reopens the last item in this unlock session");
        page.ReturnToGallery();
        await Task.Delay(200);
        await CheckAnimationsAsync(root, currentGallery()!, open, current);
        open(items[0]);
        page = current()!;
        for (int i = 0; i < 100 && !page.IsLoaded; i++) await Task.Delay(10);
        Task pending = page.SelectMediaAsync(2);
        VaultService.Instance.Lock();
        await pending;
        await Task.Delay(200);
        Require(!page.IsImageReady && page.CurrentMediaId == null, "lock during image load clears private content");
        using var unlock = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
        Require(VaultService.Instance.ApplyUnlock(unlock), "isolated vault unlocked again");
        openFromSidebar();
        Require(current() is { CurrentMediaId: null, IsImageReady: false, HasPlaybackElement: false }, "a new unlock session opens an empty player without a native playback control");
        current()!.ReturnToGallery();
        var deleteGallery = currentGallery()!;
        for (int i = 0; i < 100 && !deleteGallery.IsLoaded; i++) await Task.Delay(50);
        if (Environment.GetCommandLineArgs().Contains("--gallery-navigation-stress"))
        {
            for (int i = 0; i < 24; i++)
            {
                MainWindow.Current!.LockWorkspace();
                using var verified = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                if (!VaultService.Instance.ApplyUnlock(verified)) throw new InvalidOperationException("Stress fixture could not unlock");
                MainWindow.Current!.OnUnlocked();
                openFromSidebar();
                var empty = current() ?? throw new InvalidOperationException("Stress fixture player not opened");
                if (empty.HasPlaybackElement) throw new InvalidOperationException("Empty player created a native playback control");
                if (i % 2 == 0) empty.ReturnToGallery();
                else
                {
                    for (int turn = 0; turn < 100 && !empty.IsLoaded; turn++) await Task.Delay(10);
                    var button = (Button)empty.FindName("OpenLibraryButton");
                    if (!empty.IsLoaded || !button.IsLoaded || empty.HasPlaybackElement) throw new InvalidOperationException("Loaded empty player did not provide a gallery button without a native playback control");
                    MediaAcceptance.Invoke(button);
                }
                deleteGallery = currentGallery() ?? throw new InvalidOperationException("Stress fixture gallery not restored");
                for (int turn = 0; turn < 100 && !deleteGallery.IsLoaded; turn++) await Task.Delay(10);
                if (!deleteGallery.IsLoaded) throw new InvalidOperationException("Gallery did not load");
                await Task.Delay(25);
                SmokeTest.Report($"gallery navigation stress: {i+1}/24; loaded button={i%2!=0}");
            }
        }
        deleteGallery.SetSmokeFilter("switch-", 2, sort: 1);
        deleteGallery.FlushPendingSearch();
        var remaining = deleteGallery.DisplayedItems.ToList();
        Require(remaining.Count == 4, "delete fixture uses only the four filtered synthetic images");
        open(remaining[0]);
        page = current()!;
        await DeleteAndCheckAsync(page, remaining[0], remaining[1]);
        await page.SelectMediaAsync(2);
        await DeleteAndCheckAsync(page, remaining[3], remaining[2]);
        await DeleteAndCheckAsync(page, remaining[2], remaining[1]);
        await DeleteAndCheckAsync(page, remaining[1], null);
        Require(!page.IsImageReady && page.ImageCount == 0 && page.NavigationEnabled == (false, false),
            "deleting the last queued item clears the preview and navigation");
        VaultService.Instance.Lock();
    }

    private sealed record ViewportFrame(int Width, int Height, byte[] Pixels)
    {
        // The 800 × 12000 fixture fits to width. Both sampled rows should show
        // its upper half. A prior landscape image shows its lower half here.
        internal bool ShowsLongImage()
        {
            if (Width < 16 || Height < 16 || Pixels.LongLength != 4L * Width * Height) return false;
            foreach (double y in new[] { .25, .80 })
            {
                if (!Matches(.25, y, 40, 150, 210) || !Matches(.75, y, 108, 71, 19)) return false;
            }
            return true;
        }

        private bool Matches(double x, double y, int red, int green, int blue)
        {
            int offset = 4 * ((int)(Height * y) * Width + (int)(Width * x));
            // RenderTargetBitmap returns premultiplied BGRA. The fixture's
            // orange half has alpha 120; the left half is opaque blue.
            return Math.Abs(Pixels[offset] - blue) <= 10 && Math.Abs(Pixels[offset + 1] - green) <= 10 &&
                   Math.Abs(Pixels[offset + 2] - red) <= 10;
        }

        internal double MeanDifference(ViewportFrame other)
        {
            if (Width != other.Width || Height != other.Height || other.Pixels.Length != Pixels.Length || Pixels.Length == 0)
                return double.PositiveInfinity;
            long sum = 0;
            for (int i = 0; i < Pixels.Length; i++) sum += Math.Abs(Pixels[i] - other.Pixels[i]);
            return (double)sum / Pixels.Length;
        }

        internal async Task SaveAsync(string name)
        {
            if (Pixels.Length == 0) return;
            string path = Path.Combine(Path.GetDirectoryName(SmokeTest.OutputPath!)!, name);
            using var file = File.Create(path);
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)Width, (uint)Height, 96, 96, Pixels);
            await encoder.FlushAsync();
        }
    }

    private static async Task<ViewportFrame> WaitForLongImageViewportAsync(PlayerPage page, ViewportFrame? reference = null)
    {
        var viewport = (ScrollViewer)page.FindName("ImageContainer");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ViewportFrame? first = null, last = null;
        double difference = double.PositiveInfinity;
        int samples = 0;
        while (!deadline.IsCancellationRequested)
        {
            var bitmap = new RenderTargetBitmap();
            try
            {
                await bitmap.RenderAsync(viewport).AsTask(deadline.Token).WaitAsync(deadline.Token);
                var buffer = await bitmap.GetPixelsAsync().AsTask(deadline.Token).WaitAsync(deadline.Token);
                last = new(bitmap.PixelWidth, bitmap.PixelHeight, buffer.ToArray());
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            first ??= last;
            samples++;
            if (last.ShowsLongImage())
            {
                difference = reference?.MeanDifference(last) ?? 0;
                if (difference < 1)
                {
                    SmokeTest.Report($"image render diagnostic: long image painted; viewport={last.Width}x{last.Height}; samples={samples}; mean difference={difference:F4}");
                    return last;
                }
            }
            await Task.Delay(16);
        }
        var expected = reference ?? first;
        if (expected != null) await expected.SaveAsync(reference == null ? "r11-reference-first.png" : "r11-reference.png");
        if (last != null) await last.SaveAsync(reference == null ? "r11-reference-last.png" : "r11-actual.png");
        if (reference != null && last != null) difference = reference.MeanDifference(last);
        throw new InvalidOperationException($"Long image did not render within 5 s; samples={samples}; viewport={last?.Width}x{last?.Height}; expected colors={last?.ShowsLongImage()}; mean difference={difference:F4}");
    }

    private static async Task DeleteAndCheckAsync(PlayerPage page, VaultItem item, VaultItem? expected)
    {
        Require(page.CurrentMediaId == item.Id, "delete targets current item " + item.OriginalFileName);
        for (int i = 0; i < 100 && !page.IsLoaded; i++) await Task.Delay(50);
        MediaAcceptance.Invoke((Button)page.FindName("DeleteButton"));
        Button? confirm = null;
        for (int i = 0; i < 100 && confirm == null; i++)
        {
            await Task.Delay(50);
            confirm = VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
                .Select(popup => FindConfirm(popup.Child)).FirstOrDefault(button => button != null);
        }
        Require(confirm != null, "player asks before deleting");
        MediaAcceptance.Invoke(confirm!);
        for (int i = 0; i < 200 && (page.CurrentMediaId != expected?.Id || VaultWork.IsBusy); i++) await Task.Delay(50);
        Require(VaultService.Instance.VisibleItems.All(entry => entry.Id != item.Id) && page.CurrentMediaId == expected?.Id,
            "confirmed removal selects " + (expected?.OriginalFileName ?? "empty state"));
    }

    internal static Button? FindConfirm(DependencyObject? element, string label = "粉碎删除")
    {
        if (element == null) return null;
        if (element is Button { Name: "PrimaryButton", IsEnabled: true } primary && Equals(primary.Content, label)) return primary;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindConfirm(VisualTreeHelper.GetChild(element, i), label) is { } child) return child;
        return null;
    }

    private static async Task CheckAnimationsAsync(string root, VaultPage gallery, Action<VaultItem> open, Func<PlayerPage?> current)
    {
        // Synthetic 8x8 GIF; 12x12 WebP with partial blue and transparent frames,
        // Dwell for 900/1200/1500 ms so a native screenshot can sample every
        // composited frame on a busy machine. The file requests exactly two loops.
        foreach (var (name, data) in new[]
        {
            ("motion.gif", "R0lGODlhCAAIAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQAMAAAACwAAAAACAAIAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBMAABACwAAAAACAAIAIEAAP8AAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAA7"),
            ("motion.webp", "UklGRrwAAABXRUJQVlA4WAoAAAASAAAACwAACwAAQU5JTQYAAAAAAAAAAgBBTk1GKAAAAAAAAAAAAAsAAAsAAIQDAAJWUDhMDwAAAC8LwAIABxD9j/4HIqL/AQBBTk1GKAAAAAIAAAIAAAIAAAIAALAEAABWUDhMDwAAAC8CgAAABxDR//4HIqL/AQBBTk1GMAAAAAAAAAAAAAYAAAYAANwFAAJWUDhMFwAAAC8GgAEQDxDzH/MfDIUAAqBIvInof8D/AA==")
        })
        {
            string path = Path.Combine(root, name);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(data));
        }
        var imported = await gallery.ImportFilesAsync([Path.Combine(root, "motion.gif"), Path.Combine(root, "motion.webp")]);
        Require(imported.Imported == 2, "two animated fixtures imported into the encrypted vault");
        foreach (string name in new[] { "motion.gif", "motion.webp" })
        {
            open(VaultService.Instance.VisibleItems.Single(i => i.OriginalFileName == name));
            var page = current()!;
            var view = (Image)page.FindName("ImageViewer");
            for (int i = 0; i < 150 && !page.IsImageReady; i++) await Task.Delay(20);
            if (name.EndsWith(".gif", StringComparison.Ordinal))
                Require(view.Source is BitmapImage { IsAnimatedBitmap: true, IsPlaying: true }, "encrypted GIF is playing through WinUI");
            else
                await CheckWebpPixelsAsync(view, page);
            page.ReturnToGallery();
            await Task.Delay(100);
        }
    }

    private static async Task CheckWebpPixelsAsync(Image view, PlayerPage page)
    {
        var observed = new List<int>();
        var observedAt = new List<long>();
        var clock = Stopwatch.StartNew();
        var settled = TimeSpan.FromMilliseconds(2500);
        TimeSpan lastChange = TimeSpan.Zero, lastSample = TimeSpan.Zero, largestGap = TimeSpan.Zero;
        int lastFrame = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(18))
        {
            var capture = new RenderTargetBitmap();
            await capture.RenderAsync(view, 24, 24);
            byte[] pixels = (await capture.GetPixelsAsync()).ToArray();
            var elapsed = clock.Elapsed;
            if (elapsed - lastSample > largestGap) largestGap = elapsed - lastSample;
            lastSample = elapsed;
            lastFrame = 0;
            if (capture.PixelWidth > 0 && capture.PixelHeight > 0 && pixels.Length == capture.PixelWidth * capture.PixelHeight * 4)
            {
                int Offset(double position) => 4 * ((int)(position * capture.PixelHeight) * capture.PixelWidth + (int)(position * capture.PixelWidth));
                int reference = Offset(.8), center = Offset(5.5 / 12), corner = Offset(.8 / 12);
                // All three frames have an opaque red reference. The second has
                // a blue center; the third has a small transparent top-left patch.
                if (pixels[reference + 2] > 96 && pixels[reference + 2] > pixels[reference] * 2)
                {
                    int frame = pixels[center] > pixels[center + 2] ? 2 : pixels[corner + 3] < pixels[reference + 3] / 2 ? 3 : 1;
                    lastFrame = frame;
                    if (observed.Count == 0 || observed[^1] != frame)
                    {
                        observed.Add(frame);
                        observedAt.Add((long)elapsed.TotalMilliseconds);
                        lastChange = elapsed;
                    }
                }
            }
            if (observed.Count >= 6 && lastFrame == 3 && elapsed - lastChange >= settled) break;
            await Task.Delay(15);
        }
        SmokeTest.Report("animated WebP rendered pixel sequence: " + string.Join(",", observed));
        SmokeTest.Report($"animated WebP observed at ms: {string.Join(",", observedAt)}; largest sample gap={(long)largestGap.TotalMilliseconds}; final frame held={(long)(lastSample - lastChange).TotalMilliseconds}");
        Require(observed.SequenceEqual(new[] { 1, 2, 3, 1, 2, 3 }) && lastFrame == 3 && lastSample - lastChange >= settled &&
            page.MediaError == null, "encrypted WebP displays all three frames twice and stops");
    }

    private static void Require(bool condition, string message)
    {
        SmokeTest.Report("image experience: " + message + "=" + condition);
        if (!condition) throw new InvalidOperationException(message);
    }
}
