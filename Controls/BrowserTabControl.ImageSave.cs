using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? _readerMessageHandler;

    private async void ImageViewerMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_readerTicket == null || _imageSavePending || !IsForegroundTab || !IsLoaded ||
            !ReferenceEquals(sender, _hookedCore) || args.Source != sender.Source) return;
        try
        {
            string ticket = _readerTicket;
            if (args.TryGetWebMessageAsString() != ticket) return;
            ulong id = _imageDocumentId;
            string document = sender.Source;
            const string selection = """
                (() => {
                    const dialog = document.getElementById('stealth-image-viewer-7b54c4');
                    const image = dialog?.open && dialog.firstElementChild?.shadowRoot?.querySelector('img');
                    return image?.complete && image.naturalWidth ? image.currentSrc : null;
                })()
                """;
            string? source = JsonSerializer.Deserialize<string>(await sender.ExecuteScriptAsync(selection));
            if (ticket == _readerTicket) await SavePageImageAsync(source, document, id, confirm: true);
        }
        catch (Exception ex) { AppLog.Warn("Image reader message: " + ex.GetType().Name); }
    }

    private async Task SavePageImageAsync(string? source, string? document, ulong documentId, bool confirm)
    {
        if (_imageSavePending || source == null || _hookedCore is not { } core) return;
        bool SameDocument() => !_isDisposed && IsLoaded && IsForegroundTab && VaultService.Instance.IsUnlocked &&
            core == _hookedCore && documentId == _imageDocumentId && core.Source == document;
        if (!SameDocument()) return;
        _imageSavePending = true;
        try
        {
            // A reader runs inside third-party page content. Only a native confirmation
            // authorizes its request. The native context-menu command is already trusted.
            if (confirm)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "将当前网页图片存入媒体库？",
                    Content = "图片会直接加密保存到当前解锁的媒体库。",
                    PrimaryButtonText = "加密保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                if (await WorkspaceDialogs.ShowAsync(dialog) != ContentDialogResult.Primary || !SameDocument()) return;
            }
            using var work = VaultWork.TryBegin("图片入库");
            if (work == null) throw new InvalidOperationException("请等待当前保险箱任务完成后重试。");
            (byte[] bytes, string name) = await ReadPageImageAsync(core, source, work.Token);
            try
            {
                if (!SameDocument()) return;
                using var input = new MemoryStream(bytes, writable: false);
                await VaultService.Instance.ImportStreamAsync(input, name, new Progress<double>(work.Report), work.Token);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (SameDocument()) ShowImageSaveNotice("图片已存入媒体库",
                name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name + " · 可从媒体库导出矢量原图。" : name,
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Save page image: " + ex.GetType().Name);
            if (SmokeTest.Enabled) SmokeTest.Report($"image save failed: {ex.GetType().Name} ({ex.HResult:X8}); {ex.StackTrace}");
            if (SameDocument()) ShowImageSaveNotice("未能保存图片", ex is InvalidDataException ? ex.Message :
                VaultWork.IsBusy ? "请等待当前保险箱任务完成后重试。" : "请等待图片加载完成后重试。", InfoBarSeverity.Warning);
        }
        finally { _imageSavePending = false; }
    }

    private void ShowImageSaveNotice(string title, string detail, InfoBarSeverity severity)
    {
        BrowserErrorBar.Severity = severity;
        BrowserErrorBar.ActionButton.Visibility = Visibility.Collapsed;
        BrowserErrorBar.Title = title;
        BrowserErrorBar.Message = detail;
        UiMotion.Open(BrowserErrorBar);
    }

    private static async Task<(byte[] Bytes, string Name)> ReadPageImageAsync(CoreWebView2 core, string source, CancellationToken token)
    {
        const int maxEncoded = (MaxImageBytes + 2) / 3 * 4;
        if (source.Length > maxEncoded + 1024) throw new InvalidDataException("直接保存支持 32 MB 以内的图片。");
        using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getResourceTree", "{}").AsTask(token));
        var frame = tree.RootElement.GetProperty("frameTree");
        string frameId = frame.GetProperty("frame").GetProperty("id").GetString()!;
        string mime, content, stem = "网页图片";
        bool encoded = true;
        bool percentEscaped = false;
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int comma = source.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("网页返回了无效的图片。");
            string header = source[5..comma];
            mime = header.Split(';')[0];
            encoded = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
            content = source[(comma + 1)..];
            percentEscaped = !encoded;
            if (encoded && content.Contains('%')) content = Uri.UnescapeDataString(content);
        }
        else if (source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            // Use an isolated JS world so a webpage cannot replace fetch/FileReader.
            // The only fetch here reads a blob URL in this document.
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName = "StealthWorkspace.ImageReader" })).AsTask(token));
            int contextId = world.RootElement.GetProperty("executionContextId").GetInt32();
            string expression = $$"""
                (async () => {
                    const blob = await (await fetch({{JsonSerializer.Serialize(source)}})).blob();
                    if (!blob.type.startsWith('image/') || !blob.size || blob.size > {{MaxImageBytes}}) return null;
                    return await new Promise((resolve,reject) => {
                        const reader = new FileReader();
                        reader.onload = () => resolve(reader.result); reader.onerror = reject;
                        reader.readAsDataURL(blob);
                    });
                })()
                """;
            using var evaluation = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, contextId, awaitPromise = true, returnByValue = true })).AsTask(token));
            var result = evaluation.RootElement.GetProperty("result");
            if (!result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("图片无法读取，或超过了 32 MB 的直接保存上限。");
            string data = value.GetString()!;
            int split = data.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (split < 5) throw new InvalidDataException("网页返回了无效的图片。");
            mime = data[5..split];
            content = data[(split + 8)..];
        }
        else
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("此图片地址不能直接保存。");
            string url = source.Split('#')[0];
            var image = frame.GetProperty("resources").EnumerateArray().FirstOrDefault(r =>
                r.GetProperty("url").GetString() == url && r.GetProperty("type").GetString() == "Image");
            if (image.ValueKind == JsonValueKind.Undefined)
            {
                var top = frame.GetProperty("frame");
                if (top.GetProperty("url").GetString()?.Split('#')[0] != url ||
                    top.GetProperty("mimeType").GetString()?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidDataException("浏览器未找到已加载的图片，请刷新图片后重试。");
                image = top;
            }
            if (image.TryGetProperty("contentSize", out var size) && size.GetDouble() > MaxImageBytes)
                throw new InvalidDataException("直接保存支持 32 MB 以内的图片。");
            mime = image.GetProperty("mimeType").GetString() ?? "";
            using var resource = await GetPageResourceContentAsync(core, frameId, url, token);
            encoded = resource.RootElement.GetProperty("base64Encoded").GetBoolean();
            content = resource.RootElement.GetProperty("content").GetString() ?? "";
            string segment = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath)).Trim('.', ' ');
            if (!string.IsNullOrWhiteSpace(segment)) stem = segment.Replace("..", "_");
        }
        return await Task.Run(() => DecodePageImage(mime, content, stem, encoded, percentEscaped), token);
    }

    private static async Task<JsonDocument> GetPageResourceContentAsync(CoreWebView2 core, string frameId, string url, CancellationToken token)
    {
        // Chromium lists cached resources without the Page domain enabled, but
        // only returns their bytes while that domain is active.
        token.ThrowIfCancellationRequested();
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").AsTask(token);
            string response = await core.CallDevToolsProtocolMethodAsync("Page.getResourceContent",
                JsonSerializer.Serialize(new { frameId, url })).AsTask(token);
            return JsonDocument.Parse(response);
        }
        finally
        {
            try { await core.CallDevToolsProtocolMethodAsync("Page.disable", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { AppLog.Warn("Finish cached image read: " + ex.GetType().Name); }
        }
    }

    private static (byte[] Bytes, string Name) DecodePageImage(string mime, string content, string stem, bool encoded, bool percentEscaped)
    {
        const int maxEncoded = (MaxImageBytes + 2) / 3 * 4;
        string extension = mime.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/png" or "image/apng" => ".png", "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp", "image/gif" => ".gif", "image/bmp" => ".bmp",
            "image/avif" => ".avif", "image/svg+xml" => ".svg", "image/heic" => ".heic",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico", "image/tiff" => ".tif",
            _ => throw new InvalidDataException("此网页图片格式暂不支持直接保存。")
        };
        string name = BrowserDownloadPath.SanitizeName(stem + extension);
        if (content.Length == 0 || (encoded ? content.Length > maxEncoded : Encoding.UTF8.GetByteCount(content) > MaxImageBytes))
            throw new InvalidDataException("图片为空，或超过了 32 MB 的直接保存上限。");
        byte[] bytes;
        if (encoded) bytes = Convert.FromBase64String(content);
        else
        {
            bytes = Encoding.UTF8.GetBytes(percentEscaped ? content.Replace("+", "%2B") : content);
            if (percentEscaped)
            {
                byte[] escaped = bytes;
                bytes = System.Net.WebUtility.UrlDecodeToBytes(escaped, 0, escaped.Length)!;
                if (!ReferenceEquals(bytes, escaped)) CryptographicOperations.ZeroMemory(escaped);
            }
        }
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("图片为空，或超过了 32 MB 的直接保存上限。");
        }
        return (bytes, name);
    }
}
