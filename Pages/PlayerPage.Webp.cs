using Microsoft.UI.Xaml.Media.Imaging;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StealthWorkspace.Pages;

public sealed partial class PlayerPage
{
    private CancellationTokenSource? _webpPlayback;

    // Windows' WebP decoder returns composited canvas frames (including blending
    // and disposal). WinUI's BitmapImage plays GIFs but displays WebP frame zero.
    private async Task PlayWebpAsync(VaultItem item, int version, uint width, uint height, SoftwareBitmapSource source)
    {
        using var lifetime = new CancellationTokenSource();
        _webpPlayback = lifetime;
        CancellationToken token = lifetime.Token;
        try
        {
            using Stream decrypted = await Task.Run(() => VaultService.Instance.OpenDecryptedStream(item), token);
            using IRandomAccessStream random = decrypted.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(random).AsTask(token);
            uint repeats = await ReadWebpPropertyAsync(decoder.BitmapContainerProperties, "/anim/LoopCount", token);
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
            uint index = 0, completed = 0;
            var frame = await decoder.GetFrameAsync(index).AsTask(token);
            while (true)
            {
                // Wait on the displayed frame, including the final frame of a finite loop.
                uint duration = await ReadWebpPropertyAsync(frame.BitmapProperties, "/anmf/FrameDuration", token);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(duration, 10u)), token);
                if (++index == decoder.FrameCount)
                {
                    if (++completed == repeats && repeats != 0) break;
                    index = 0;
                }
                frame = await decoder.GetFrameAsync(index).AsTask(token);
                var pixels = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
                // Keep one displayed source for this animation. Replacing the
                // frame does not close a source still used by the compositor.
                await VaultImageDecoder.SetPixelsAsync(source, pixels, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (!token.IsCancellationRequested && version == _loadVersion && !_cleanedUp)
        {
            AppLog.Warn("动态 WebP 播放中断：" + ex.GetType().Name);
            ShowPlaybackError("无法播放后续图像帧。");
        }
        catch (Exception) when (token.IsCancellationRequested || version != _loadVersion || _cleanedUp) { }
        finally { if (ReferenceEquals(_webpPlayback, lifetime)) _webpPlayback = null; }
    }

    private static async Task<uint> ReadWebpPropertyAsync(BitmapPropertiesView properties, string name, CancellationToken token)
    {
        var result = await properties.GetPropertiesAsync([name]).AsTask(token);
        return result.TryGetValue(name, out var value) ? Convert.ToUInt32(value.Value) : 0;
    }
}
