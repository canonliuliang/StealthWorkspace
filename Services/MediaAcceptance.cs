using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
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
            File.Copy(video, Path.Combine(root, "第二段视频.mp4"), overwrite: true);
            WriteSilentWave(Path.Combine(root, "八秒音频.wav"));
            var paths = Directory.GetFiles(root).Where(p => Path.GetExtension(p) is ".jpg" or ".png" or ".mp4" or ".wav").ToList();
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
            Require(result.Imported == paths.Count && result.Failed == 0, "JPG/PNG/MP4/WAV import including configured shared source");
            Require(await page.WaitForSmokeThumbnailAsync(), "decoded gallery thumbnail");
            var displayTimes = page.DisplayedItems.Select(item => item.ImportedAt).ToArray();
            Require(displayTimes.SequenceEqual(displayTimes.OrderDescending()), "gallery defaults to newest imports first");
            page.SetSmokeFilter("", 1);
            var previewVideo = page.DisplayedItems.Single(item => item.OriginalFileName == Path.GetFileName(video));
            Require(await page.WaitForSmokeThumbnailAsync(previewVideo.Id) && previewVideo.DisplayDuration is "0:08" or "0:07",
                "decrypted video has a cover and duration");
            page.SetSmokeFilter("", 0, sort: 2);
            var sizes = page.DisplayedItems.Select(item => item.FileSizeBytes).ToArray();
            Require(sizes.SequenceEqual(sizes.OrderDescending()), "gallery sorts by size");
            page.SetSmokeFilter("", 0, sort: 0);
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
            if (item.MediaType != "Image")
            {
                var element = (Microsoft.UI.Xaml.Controls.MediaPlayerElement)player()!.FindName("PlayerElement");
                var media = element.MediaPlayer;
                Require(!media.CommandManager.IsEnabled && !media.SystemMediaTransportControls.IsEnabled,
                    "private playback leaves Windows media transport commands disabled");
                bool originalMute = media.IsMuted;
                try
                {
                    foreach (bool userMuted in new[] { false, true })
                    {
                        media.IsMuted = userMuted;
                        PrivacyMediaGuard.SetAppAudioMuted(true);
                        Require(media.IsMuted, "player muted for boss key");
                        PrivacyMediaGuard.SetAppAudioMuted(false);
                        Require(media.IsMuted == userMuted, "player restored previous mute=" + userMuted);
                    }
                }
                finally
                {
                    PrivacyMediaGuard.SetAppAudioMuted(false);
                    media.IsMuted = originalMute;
                }
                var videoPage = player()!;
                var session = media.PlaybackSession;
                media.Pause();
                session.Position = TimeSpan.Zero;
                await Task.Delay(200);
                Require(videoPage.HandlePlaybackKey(Windows.System.VirtualKey.Right), "right arrow handles video seek");
                for (int i = 0; i < 40 && Math.Abs(session.Position.TotalSeconds - 5) > 0.5; i++) await Task.Delay(50);
                Require(Math.Abs(session.Position.TotalSeconds - 5) <= 0.5, "right arrow seeks forward five seconds");
                videoPage.HandlePlaybackKey(Windows.System.VirtualKey.Left);
                for (int i = 0; i < 40 && session.Position.TotalSeconds > 0.5; i++) await Task.Delay(50);
                Require(session.Position.TotalSeconds <= 0.5, "left arrow seeks backward five seconds");
                media.Volume = 0.5;
                videoPage.HandlePlaybackKey(Windows.System.VirtualKey.Up);
                Require(Math.Abs(media.Volume - 0.55) < 0.001, "up arrow increases volume");
                videoPage.HandlePlaybackKey(Windows.System.VirtualKey.Down);
                Require(Math.Abs(media.Volume - 0.5) < 0.001, "down arrow decreases volume");
                if (item.MediaType == "Audio")
                {
                    Require(!videoPage.HandlePlaybackKey(Windows.System.VirtualKey.F), "audio playback leaves video full screen disabled");
                    continue;
                }
                videoPage.HandlePlaybackKey(Windows.System.VirtualKey.F);
                Require(MainWindow.Current?.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen,
                    "F enters full-screen video");
                videoPage.HandlePlaybackKey(Windows.System.VirtualKey.Escape);
                Require(MainWindow.Current?.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen &&
                    videoPage.CurrentMediaId == item.Id, "Escape exits full screen and keeps the video open");
                if (expected.Keys.Count(path => Path.GetExtension(path) == ".mp4") == 2)
                {
                    var direction = videoPage.NavigationEnabled.Previous ? "PreviousButton" : "NextButton";
                    Invoke((Button)videoPage.FindName(direction));
                    for (int i = 0; i < 100 && (videoPage.CurrentMediaId == item.Id || !videoPage.IsMediaReady); i++) await Task.Delay(50);
                    Require(videoPage.IsMediaReady && videoPage.CurrentMediaId != item.Id, "video navigation opens another playable video");
                }
            }
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

    internal static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
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

    private static void WriteSilentWave(string path)
    {
        const int sampleRate = 16000;
        const short bytesPerSample = sizeof(short);
        byte[] samples = new byte[8 * sampleRate * bytesPerSample];
        using var output = new BinaryWriter(File.Create(path));
        output.Write("RIFF"u8); output.Write(36 + samples.Length); output.Write("WAVE"u8);
        output.Write("fmt "u8); output.Write(16); output.Write((short)1); output.Write((short)1);
        output.Write(sampleRate); output.Write(sampleRate * bytesPerSample);
        output.Write(bytesPerSample); output.Write((short)(8 * bytesPerSample));
        output.Write("data"u8); output.Write(samples.Length); output.Write(samples);
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
