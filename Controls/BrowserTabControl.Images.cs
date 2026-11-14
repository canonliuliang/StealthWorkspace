using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private static readonly Lazy<string> ImageViewerScript = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StealthWorkspace.Assets.BrowserImageViewer.js")
            ?? throw new InvalidOperationException("Image reader resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    private CoreWebView2ContextMenuItem? _imageReaderMenuItem;
    private string? _contextImageSource;
    private string? _contextImageDocument;
    private bool _imagesAfterMenuClose;

    private void CoreWebView2_ContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (_isDisposed || _retainedForDownloads) return;
        try
        {
            var target = args.ContextMenuTarget;
            if (SmokeTest.Enabled) SmokeTest.Report($"image context: kind={target.Kind}; main={target.IsRequestedForMainFrame}; hasSource={target.HasSourceUri}; sourceLength={target.SourceUri?.Length ?? 0}; frameLength={target.FrameUri?.Length ?? 0}; pageLength={target.PageUri?.Length ?? 0}; sameFrame={target.FrameUri == target.PageUri}; nativePage={target.PageUri == sender.Source}; point={args.Location}; scale={XamlRoot?.RasterizationScale}");
            // This runtime reports both boolean hints as false for an SVG image
            // even though SourceUri and the top-level FrameUri are populated.
            // Validate the actual source and frame instead of hiding the command.
            bool mainFrame = target.IsRequestedForMainFrame || (!string.IsNullOrEmpty(target.PageUri) && target.FrameUri == target.PageUri);
            if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || string.IsNullOrEmpty(target.SourceUri) || !mainFrame) return;
            _contextImageSource = target.SourceUri;
            _contextImageDocument = sender.Source;
            if (_imageReaderMenuItem == null)
            {
                _imageReaderMenuItem = sender.Environment.CreateContextMenuItem("在图片阅读器中查看", null, CoreWebView2ContextMenuItemKind.Command);
                _imageReaderMenuItem.CustomItemSelected += ImageReaderMenuItem_Selected;
            }
            args.MenuItems.Insert(0, _imageReaderMenuItem);
            if (SmokeTest.Enabled) SmokeTest.Report("image context: inserted reader command; count=" + args.MenuItems.Count);
        }
        catch (Exception ex) { AppLog.Warn("Image context menu: " + ex.GetType().Name); }
    }

    private void ImageReaderMenuItem_Selected(CoreWebView2ContextMenuItem sender, object args)
    {
        string? source = _contextImageSource, document = _contextImageDocument;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isDisposed && _hookedCore?.Source == document) await OpenPageImagesAsync(source);
        });
    }

    private void PageImages_Click(object sender, RoutedEventArgs args)
    {
        _imagesAfterMenuClose = true;
        MoreActionsFlyout.Hide();
    }

    private async Task<string> OpenPageImagesAsync(string? startSource = null)
    {
        if (_isDisposed || !_isInitialized || _hookedCore is not { } core) return "unavailable";
        try
        {
            string result = await core.ExecuteScriptAsync(ImageViewerScript.Value + "(" + JsonSerializer.Serialize(startSource) + "," + (UiMotion.AnimationsEnabled ? "true" : "false") + ");");
            if (_isDisposed || !ReferenceEquals(core, _hookedCore)) return "unavailable";
            using var parsed = JsonDocument.Parse(result);
            string status = parsed.RootElement.TryGetProperty("status", out var value) ? value.GetString() ?? "unavailable" : "unavailable";
            if (status is "opened" or "already-open") _browserControl?.Focus(FocusState.Programmatic);
            else if (status == "empty") ShowBrowserError("没有找到网页图片", "此功能查看当前页面的图片，不包括视频、画布和嵌入页面。");
            else ShowBrowserError("无法打开图片阅读器", "可以使用图片右键菜单，在新标签页中打开图片。");
            return status;
        }
        catch (Exception ex)
        {
            if (!_isDisposed) { AppLog.Warn("Page image reader: " + ex.GetType().Name); ShowBrowserError("无法打开图片阅读器", "请稍后重试。"); }
            return "unavailable";
        }
    }

    private void DetachImageReader()
    {
        if (_imageReaderMenuItem != null) _imageReaderMenuItem.CustomItemSelected -= ImageReaderMenuItem_Selected;
        _imageReaderMenuItem = null;
        _contextImageSource = _contextImageDocument = null;
        _imagesAfterMenuClose = false;
    }
}
