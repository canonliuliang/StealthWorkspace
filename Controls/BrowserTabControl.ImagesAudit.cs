using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckPageImagesAuditAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        const string root = "document.getElementById('stealth-image-viewer-7b54c4')";
        const string shadow = root + ".firstElementChild.shadowRoot";
        async Task RequireAsync(string expression, string description)
        {
            bool passed = await EvaluateAuditAsync(expression) == "true";
            SmokeTest.Report("image reader audit: " + description + "=" + passed);
            if (!passed)
            {
                SmokeTest.Report("image reader state: " + await EvaluateAuditAsync("(()=>{const s=" + shadow + ";const i=s.querySelector('img');return JSON.stringify({counter:s.querySelector('[aria-live]').textContent,width:i.naturalWidth,height:i.naturalHeight,source:i.src,images:Array.from(document.images).map(n=>({src:n.currentSrc,w:n.naturalWidth,h:n.naturalHeight}))});})()"));
                throw new InvalidOperationException(description);
            }
        }
        foreach (string page in new[] { "/gallery", "/gallery-csp" })
        {
            await NavigateAuditAsync(fixture.Address + page);
            if (page == "/gallery")
            {
                await CheckImageContextAuditAsync();
                var vault = VaultService.Instance;
                var existing = vault.VisibleItems.Select(i => i.Id).ToHashSet();
                await SavePageImageAsync(fixture.Address + "/image-tall", _hookedCore!.Source, _imageDocumentId, confirm: false);
                var saved = vault.VisibleItems.Single(i => !existing.Contains(i.Id));
                try
                {
                    using var image = vault.OpenDecryptedStream(saved);
                    using var reader = new StreamReader(image);
                    if (saved.FileExtension != ".svg" || !reader.ReadToEnd().Contains("height='3200'"))
                        throw new InvalidOperationException("Wrong cached image bytes in the vault");
                    SmokeTest.Report("image reader audit: selected cached image saved directly to vault with original bytes");
                }
                finally { await vault.DeleteItemAsync(saved); }
            }
            await EvaluateAuditAsync("document.documentElement.style.setProperty('overflow-x','auto','important');document.documentElement.style.setProperty('overflow-y','scroll');");
            const string pageState = "JSON.stringify({x:scrollX,y:scrollY,overflow:document.documentElement.style.cssText})";
            string beforeReader = await EvaluateAuditAsync(pageState);
            string status = await OpenPageImagesAsync(fixture.Address + "/image-tall");
            if (status != "opened") throw new InvalidOperationException("Image reader failed to open: " + status);
            for (int i = 0; i < 80; i++)
            {
                if (await EvaluateAuditAsync(shadow + ".querySelector('img').naturalHeight===3200") == "true") break;
                await Task.Delay(25);
            }
            await RequireAsync(shadow + ".querySelector('[aria-live]').textContent==='2 / 3' && " + shadow + ".querySelector('img').naturalHeight===3200",
                page + " starts at requested image and deduplicates sources");
            await RequireAsync("!" + shadow + ".querySelector('[aria-label=加密保存到媒体库]').disabled",
                page + " loaded image can be saved from reader");
            if (page == "/gallery-csp") await CheckReaderSaveGestureAsync(shadow);
            await RequireAsync(shadow + ".querySelector('[aria-label^=按窗口宽度]').getAttribute('aria-pressed')==='true'",
                page + " long image opens at readable width automatically");
            await EvaluateAuditAsync(shadow + ".querySelector('[aria-label^=按窗口宽度]').click()");
            await RequireAsync("(()=>{const s=" + shadow + ";const img=s.querySelector('img');const viewport=s.querySelector('[aria-label^=图片内容]');return img.height>viewport.clientHeight && img.width<=viewport.clientWidth;})()",
                page + " width mode supports scrolling long images");
            await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'PageDown',bubbles:true}))");
            await RequireAsync(shadow + ".querySelector('[aria-label^=图片内容]').scrollTop>0", page + " page-down scrolls the reading viewport");
            await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}))");
            await Task.Delay(100);
            await RequireAsync(shadow + ".querySelector('[aria-live]').textContent==='3 / 3' && " + shadow + ".querySelector('[aria-label^=下一张]').disabled",
                page + " arrow navigation reaches last image without wrapping");
            await RequireAsync(shadow + ".querySelector('[role=status]').textContent.includes('加载失败')", page + " failed image has a recoverable message");
            await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true}))");
            await RequireAsync(shadow + ".querySelector('[aria-live]').textContent==='1 / 3'", page + " home returns to first image");
            await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await RequireAsync(root + "===null && document.getElementById('draft').value==='未提交的输入' && document.images.length===4",
                page + " escape removes reader and preserves page images and draft");
            bool restored = beforeReader == await EvaluateAuditAsync(pageState);
            SmokeTest.Report("image reader audit: " + page + " restores exact scroll and inline overflow=" + restored);
            if (!restored) throw new InvalidOperationException("Reader changed original page scrolling");
        }
        await NavigateAuditAsync(fixture.Address + "/gallery-lazy");
        bool untouchedBefore = fixture.CountRequests("/image-lazy-one") == 0 && fixture.CountRequests("/image-lazy-two") == 0;
        await OpenPageImagesAsync(fixture.Address + "/image-lazy-one");
        for (int i = 0; i < 100 && await EvaluateAuditAsync(shadow + ".querySelector('img')?.naturalHeight===3200") != "true"; i++) await Task.Delay(25);
        await RequireAsync(shadow + ".querySelector('[aria-live]').textContent==='1 / 2' && " + shadow + ".querySelector('img').naturalHeight===3200",
            "lazy images resolve declared data sources and keep separate placeholders");
        bool currentOnly = fixture.CountRequests("/image-lazy-one") == 1 && fixture.CountRequests("/image-lazy-two") == 0;
        SmokeTest.Report($"image reader audit: lazy originals unrequested before open={untouchedBefore}; only selected image fetched={currentOnly}");
        if (!untouchedBefore || !currentOnly) throw new InvalidOperationException("Reader eagerly requested lazy originals");
        await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}))");
        for (int i = 0; i < 100 && fixture.CountRequests("/image-lazy-two") == 0; i++) await Task.Delay(25);
        await RequireAsync("Array.from(document.images).every(i=>i.getAttribute('src')==='/image-placeholder')", "lazy page elements are not rewritten by reader");
        await EvaluateAuditAsync(root + ".dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
    }

    private async Task CheckReaderSaveGestureAsync(string shadow)
    {
        if (_hookedCore is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Reader save gesture restricted to fixture");
        var vault = VaultService.Instance;
        var existing = vault.VisibleItems.Select(i => i.Id).ToHashSet();
        // Keyboard activation targets the same button at every OS scale and page zoom.
        await core.ExecuteScriptAsync(shadow + ".querySelector('[aria-label=加密保存到媒体库]').focus()");
        foreach (string type in new[] { "keyDown", "keyUp" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", System.Text.Json.JsonSerializer.Serialize(new
            {
                type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13,
                text = type == "keyDown" ? "\r" : ""
            }));
        Microsoft.UI.Xaml.Controls.Button? confirm = null;
        for (int i = 0; i < 100 && confirm == null; i++)
        {
            await Task.Delay(50);
            confirm = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                .Select(popup => ImageExperienceAcceptance.FindConfirm(popup.Child, "加密保存")).FirstOrDefault(b => b != null);
        }
        if (confirm == null || vault.VisibleItems.Count != existing.Count)
            throw new InvalidOperationException($"Reader save must await native confirmation; pending={_imageSavePending}; ticket={_readerTicket != null}; messaging={core.Settings.IsWebMessageEnabled}");
        MediaAcceptance.Invoke(confirm);
        for (int i = 0; i < 100 && (_imageSavePending || vault.VisibleItems.Count == existing.Count); i++) await Task.Delay(50);
        var item = vault.VisibleItems.Single(i => !existing.Contains(i.Id));
        using (var reader = new StreamReader(vault.OpenDecryptedStream(item)))
            if (!reader.ReadToEnd().Contains("height='3200'")) throw new InvalidOperationException("Reader saved a different image");
        await vault.DeleteItemAsync(item);
        SmokeTest.Report("image reader audit: trusted reader toolbar click, native confirmation, decrypted original on CSP page");
    }

    private async Task CheckImageContextAuditAsync()
    {
        var core = _hookedCore ?? throw new InvalidOperationException("Image context core missing");
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var site) || !site.IsLoopback)
            throw new InvalidOperationException("Context gesture restricted to fixture");
        var ready = new TaskCompletionSource<bool>();
        void Requested(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuRequestedEventArgs args)
        {
            SmokeTest.Report("image native menu labels: " + string.Join(" | ", args.MenuItems.Select(item => item.Label)));
            args.Handled = true;
            ready.TrySetResult(args.MenuItems.Any(item => item.Label == "在图片阅读器中查看") &&
                args.MenuItems.Any(item => item.Label == "加密保存到媒体库"));
        }
        core.ContextMenuRequested += Requested;
        try
        {
            var pointJson = await core.ExecuteScriptAsync("(()=>{const i=document.querySelector('[src=\"/image-tall\"]');i.scrollIntoView({block:'start'});const r=i.getBoundingClientRect();return {x:r.x+r.width/2,y:Math.max(0,r.y)+40};})()");
            using var point = System.Text.Json.JsonDocument.Parse(pointJson);
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", System.Text.Json.JsonSerializer.Serialize(new
                {
                    type, x = point.RootElement.GetProperty("x").GetDouble(), y = point.RootElement.GetProperty("y").GetDouble(),
                    button = "right", buttons = type == "mousePressed" ? 2 : 0, clickCount = 1
                }));
            bool present = await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
            SmokeTest.Report("image reader audit: native image context menu contains reader=" + present);
            if (!present) throw new InvalidOperationException("Native image reader menu missing");
        }
        finally { core.ContextMenuRequested -= Requested; }
    }
}
