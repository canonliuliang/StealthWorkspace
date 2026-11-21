using Microsoft.UI.Xaml.Media.Imaging;
using StealthWorkspace.Models;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace StealthWorkspace.Services;

internal static class VaultVideoPreview
{
    internal static async Task<(SoftwareBitmapSource Source, TimeSpan Duration)> DecodeAsync(
        VaultItem item, CancellationToken token)
    {
        // Decode only the visible card. The seekable stream and frame stay in memory.
        var (pixels, duration) = await Task.Run(async () =>
        {
            using var stream = VaultService.Instance.OpenDecryptedStream(item);
            using var random = stream.AsRandomAccessStream();
            using var media = MediaSource.CreateFromStream(random, item.MimeType);
            using var player = new MediaPlayer { IsMuted = true, IsVideoFrameServerEnabled = true };
            player.CommandManager.IsEnabled = false;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.VideoFrameAvailable += (_, _) => ready.TrySetResult();
            player.MediaFailed += (_, _) => ready.TrySetException(new InvalidDataException("无法解码视频封面。"));
            player.Source = media;
            player.Play();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var session = player.PlaybackSession;
            var size = VaultImageDecoder.GetDecodeSize(session.NaturalVideoWidth, session.NaturalVideoHeight, 400, 160_000);
            using var frame = VideoFrame.CreateAsDirect3D11SurfaceBacked(
                DirectXPixelFormat.B8G8R8A8UIntNormalized, (int)size.Width, (int)size.Height);
            player.CopyFrameToVideoSurface(frame.Direct3DSurface);
            player.Pause();
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Direct3DSurface, BitmapAlphaMode.Premultiplied)
                .AsTask(token);
            return (bitmap, session.NaturalDuration);
        }, token);
        return (await VaultImageDecoder.CreateSourceAsync(pixels, token), duration);
    }
}
