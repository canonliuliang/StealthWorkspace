using System.Security.Cryptography;
using System.Text.Json;
using StealthWorkspace.Pages;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace StealthWorkspace.Services;

internal static class MediaAcceptance
{
    internal static async Task RunAsync(VaultPage page, Action<Models.VaultItem> open, Func<PlayerPage?> player)
    {
        string root = SmokeTest.MediaFixtureRoot!;
        Directory.CreateDirectory(root);
        string manifestPath = Path.Combine(root, "expected.json");
        Dictionary<string, string> expected;
        if (!SmokeTest.ReopenMedia)
        {
            await CreateImageAsync(Path.Combine(root, "横图 中文.jpg"), 1200, 800, BitmapEncoder.JpegEncoderId);
            await CreateImageAsync(Path.Combine(root, "长图 透明.png"), 240, 4096, BitmapEncoder.PngEncoderId);
            await CreateImageAsync(Path.Combine(root, "大图 6000x4000.jpg"), 6000, 4000, BitmapEncoder.JpegEncoderId);
            await CreateImageAsync(Path.Combine(root, "方向6.jpg"), 320, 180, BitmapEncoder.JpegEncoderId, true);
            string video = Path.Combine(root, "测试视频.mp4");
            if (!File.Exists(video))
            {
                var composition = new MediaComposition();
                composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 45, 120, 210), TimeSpan.FromSeconds(4)));
                composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 220, 140, 40), TimeSpan.FromSeconds(4)));
                using var output = File.Create(video + ".part");
                using var random = output.AsRandomAccessStream();
                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
                var prepared = await new MediaTranscoder().PrepareMediaStreamSourceTranscodeAsync(composition.GenerateMediaStreamSource(), random, profile);
                if (!prepared.CanTranscode) throw new InvalidOperationException("MP4 fixture encoding failed: " + prepared.FailureReason);
                await prepared.TranscodeAsync();
                random.Dispose();
                output.Dispose();
                File.Move(video + ".part", video);
            }
            var paths = Directory.GetFiles(root).Where(p => Path.GetExtension(p) is ".jpg" or ".png" or ".mp4").ToList();
            if (SmokeTest.SharedFixtureRoot is { } shared)
            {
                Directory.CreateDirectory(shared);
                string shareFile = Path.Combine(shared, "共享目录 中文.png");
                File.Copy(Path.Combine(root, "长图 透明.png"), shareFile, true);
                paths.Add(shareFile);
            }
            expected = paths.ToDictionary(p => p, HashFile);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(expected));
            var result = await page.ImportFilesAsync(paths);
            Require(result.Imported == paths.Count && result.Failed == 0, "JPG/PNG/MP4 import including configured shared source");
            Require(await page.WaitForSmokeThumbnailAsync(), "decoded gallery thumbnail");
            int beforeRetry = VaultService.Instance.VisibleItems.Count;
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string valid = Path.Combine(root, "batch-success-" + suffix + ".png");
            string missing = Path.Combine(root, "batch-retry-" + suffix + ".png");
            File.Copy(Path.Combine(root, "长图 透明.png"), valid);
            var partial = await page.ImportFilesAsync(new[] { valid, missing });
            Require(partial.Imported == 1 && partial.Failed == 1, "batch continues and reports missing source");
            File.Copy(valid, missing);
            var retry = await page.RetryFailedImportsAsync();
            Require(retry.Imported == 1 && retry.Failed == 0 && VaultService.Instance.VisibleItems.Count == beforeRetry + 2,
                "retry imports only failed item, no duplicate success");
        }
        else expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))!;

        foreach (var source in expected)
        {
            var item = VaultService.Instance.VisibleItems.Single(i => i.OriginalFileName == Path.GetFileName(source.Key));
            using (Stream decrypted = VaultService.Instance.OpenDecryptedStream(item))
                Require(Convert.ToHexString(SHA256.HashData(decrypted)) == source.Value, "decrypted bytes: " + item.OriginalFileName);
            Require(File.Exists(source.Key) && HashFile(source.Key) == source.Value, "original retained: " + item.OriginalFileName);
            open(item);
            for (int i = 0; i < 200; i++)
            {
                await Task.Delay(100);
                if (player()?.MediaError != null) throw new InvalidOperationException(player()!.MediaError);
                if (item.MediaType == "Image" ? player()?.IsImageReady == true : player()?.IsMediaReady == true && player()?.PlaybackSeconds > 0.5) break;
            }
            Require(item.MediaType == "Image" ? player()?.IsImageReady == true : player()?.IsMediaReady == true && player()?.PlaybackSeconds > 0.5,
                (SmokeTest.ReopenMedia ? "after process restart " : "") + "viewer/playback: " + item.OriginalFileName);
        }

        string backupPath = Path.Combine(root, "acceptance.swbackup");
        await VaultService.Instance.CreateBackupAsync(backupPath);
        int count = await VaultService.Instance.VerifyBackupAsync(backupPath);
        string restoredRoot = Path.Combine(root, "restored-" + Guid.NewGuid().ToString("N"));
        var restored = new VaultService(restoredRoot);
        Require(await restored.RestoreBackupAsync(backupPath) == count, "backup restored into isolated empty vault");
        Require(restored.ApplyUnlock(await Task.Run(() => restored.VerifyPassword("SmokeTest#123"))), "restored vault password accepted");
        foreach (var source in expected)
        {
            using Stream stream = restored.OpenDecryptedStream(restored.VisibleItems.Single(i => i.OriginalFileName == Path.GetFileName(source.Key)));
            Require(Convert.ToHexString(SHA256.HashData(stream)) == source.Value, "restored media hash: " + Path.GetFileName(source.Key));
        }
        restored.Lock();
    }

    private static void Require(bool condition, string message)
    {
        SmokeTest.Report("acceptance: " + message + "=" + condition);
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static async Task CreateImageAsync(string path, uint width, uint height, Guid encoderId, bool orientation = false)
    {
        if (File.Exists(path)) return;
        byte[] pixels = await Task.Run(() =>
        {
            var bytes = new byte[checked(width * height * 4)];
            for (uint y = 0; y < height; y++)
            for (uint x = 0; x < width; x++)
            {
                int offset = checked((int)((y * width + x) * 4));
                bytes[offset] = (byte)(x < width / 2 ? 210 : 40);
                bytes[offset + 1] = (byte)(y < height / 2 ? 150 : 70);
                bytes[offset + 2] = (byte)(x < width / 2 ? 40 : 230);
                bytes[offset + 3] = (byte)(encoderId == BitmapEncoder.PngEncoderId && x > width / 2 ? 120 : 255);
            }
            return bytes;
        });
        using var stream = File.Create(path);
        using var random = stream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, random);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        if (orientation)
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            { ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue((ushort)6, Windows.Foundation.PropertyType.UInt16) });
        await encoder.FlushAsync();
        SmokeTest.Report($"fixture: {Path.GetFileName(path)} {width}x{height}");
    }
}
