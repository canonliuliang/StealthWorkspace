using Microsoft.UI.Xaml.Media.Imaging;
using StealthWorkspace.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StealthWorkspace.Services;

internal static class VaultImageDecoder
{
    internal readonly record struct DecodedImage(SoftwareBitmapSource Source, int Width, int Height);

    internal static async Task<DecodedImage> DecodeAsync(VaultItem item, uint maxEdge, CancellationToken cancellationToken,
        ulong maxPixels = 16 * 1024 * 1024)
    {
        SoftwareBitmap bitmap = await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream decrypted = VaultService.Instance.OpenDecryptedStream(item);
            using IRandomAccessStream random = decrypted.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
            var size = GetDecodeSize(decoder.PixelWidth, decoder.PixelHeight, maxEdge, maxPixels);
            var transform = new BitmapTransform
            {
                ScaledWidth = size.Width,
                ScaledHeight = size.Height,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            cancellationToken.ThrowIfCancellationRequested();
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken);
        }, cancellationToken);

        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        return new(await CreateSourceAsync(bitmap, cancellationToken), width, height);
    }

    // WinUI retains this exact SoftwareBitmap so it can later render into a
    // software surface (for example, after a GPU reset or a RenderTargetBitmap).
    // Accept ownership: once handed to XAML, only the source may close its pixels.
    internal static async Task<SoftwareBitmapSource> CreateSourceAsync(SoftwareBitmap bitmap, CancellationToken token)
    {
        SoftwareBitmapSource? source = null;
        bool handedToXaml = false;
        try
        {
            token.ThrowIfCancellationRequested();
            source = new SoftwareBitmapSource();
            var upload = source.SetBitmapAsync(bitmap);
            handedToXaml = true;
            await upload;
            token.ThrowIfCancellationRequested();
            return source;
        }
        catch
        {
            source?.Dispose();
            if (!handedToXaml) bitmap.Dispose();
            throw;
        }
    }

    internal static (uint Width, uint Height) GetDecodeSize(uint width, uint height, uint maxEdge, ulong maxPixels)
    {
        if (width == 0 || height == 0 || maxEdge == 0 || maxPixels == 0)
            throw new InvalidDataException("图片尺寸无效。");
        // Preserve narrow long images instead of reducing all images to a 4096px
        // long edge. A separate pixel budget bounds the uncompressed allocation.
        double scale = Math.Min(1.0, Math.Min(maxEdge / (double)Math.Max(width, height),
            Math.Sqrt(maxPixels / ((double)width * height))));
        return (Math.Max(1u, (uint)(width * scale)), Math.Max(1u, (uint)(height * scale)));
    }
}
