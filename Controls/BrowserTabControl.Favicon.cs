using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _faviconTimer;
    private XamlRoot? _faviconRoot;
    private double _faviconScale;
    private bool _faviconLoading, _faviconPending;
    private int _faviconSourcePixels;
    private string? _faviconOrigin;
    private string? _faviconSourceUri;

    private static string? IconOrigin(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;

    private void BeginFaviconNavigation(string address)
    {
        // Keep a same-site icon while the next page loads. Clear it immediately
        // for a different origin so the old site's identity is never displayed.
        if (_faviconOrigin == null || !string.Equals(_faviconOrigin, IconOrigin(address), StringComparison.OrdinalIgnoreCase))
        {
            _favicon = null;
            _faviconSourcePixels = 0;
            _faviconOrigin = null;
            _faviconSourceUri = null;
        }
        _faviconVersion++;
    }

    private int FaviconPixels => Math.Clamp((int)Math.Ceiling(16 * (XamlRoot?.RasterizationScale ?? 1)), 16, 96);

    private void AttachFaviconScale()
    {
        if (!ReferenceEquals(_faviconRoot, XamlRoot))
        {
            DetachFaviconScale();
            _faviconRoot = XamlRoot;
            if (_faviconRoot != null) _faviconRoot.Changed += FaviconScaleChanged;
        }
        FaviconScaleChanged(_faviconRoot!, null!);
    }

    private void DetachFaviconScale()
    {
        if (_faviconRoot != null) _faviconRoot.Changed -= FaviconScaleChanged;
        _faviconRoot = null;
    }

    private void FaviconScaleChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (Math.Abs(scale - _faviconScale) < 0.01) return;
        _faviconScale = scale;
        if (_hookedCore != null) CoreWebView2_FaviconChanged(_hookedCore, EventArgs.Empty);
    }

    private void CoreWebView2_FaviconChanged(CoreWebView2 sender, object args)
    {
        if (_isDisposed) return;
        _faviconVersion++;
        _faviconPending = true;
        if (_faviconTimer == null)
        {
            _faviconTimer = DispatcherQueue.CreateTimer();
            _faviconTimer.IsRepeating = false;
            _faviconTimer.Interval = TimeSpan.FromMilliseconds(150);
            _faviconTimer.Tick += FaviconTimer_Tick;
        }
        _faviconTimer.Stop();
        _faviconTimer.Start();
    }

    private void FaviconTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => _ = RefreshFaviconAsync();

    private void CancelFaviconRefresh()
    {
        if (_faviconTimer != null)
        {
            _faviconTimer.Stop();
            _faviconTimer.Tick -= FaviconTimer_Tick;
            _faviconTimer = null;
        }
        _faviconPending = false;
        _faviconVersion++;
    }

    private async Task RefreshFaviconAsync()
    {
        if (_isDisposed || _faviconLoading || !_faviconPending || _hookedCore is not { } core) return;
        // Do not queue a script or an icon fetch in a sleeping page. Resume will
        // service the one pending update when the user returns to this tab.
        if (core.IsSuspended) return;
        _faviconPending = false;
        _faviconLoading = true;
        int version = _faviconVersion;
        bool Current() => !_isDisposed && version == _faviconVersion && ReferenceEquals(core, _hookedCore);
        try
        {
            string sourceUri = core.FaviconUri;
            // Immediate native fallback. Some runtimes return only 16px even when
            // FaviconUri points to a larger image; keep this until the original is ready.
            // With no site icon, the runtime can supply its black stock globe.
            // Keep our theme-aware vector globe instead of a blurry dark bitmap.
            using var native = string.IsNullOrWhiteSpace(sourceUri) ? null :
                await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            if (!Current()) return;
            if (native is { Size: > 0 })
            {
                var fallback = new BitmapImage();
                await fallback.SetSourceAsync(native);
                if (!Current()) return;
                if (_favicon == null || _faviconSourceUri != sourceUri || fallback.PixelWidth >= FaviconPixels)
                {
                    _favicon = fallback;
                    _faviconSourcePixels = fallback.PixelWidth;
                    _faviconOrigin = IconOrigin(core.Source);
                    _faviconSourceUri = sourceUri;
                    NavigationVisualChanged?.Invoke();
                }
                // The native icon is already cached by WebView2. At 100% scale,
                // its 16px image is sufficient; larger native icons can satisfy
                // higher DPI too. Only run the extra page fetch/decoder when the
                // actual display needs more pixels. Dynamic FaviconChanged events
                // still fetch a fresh native image, including same-URL updates.
                if (fallback.PixelWidth >= FaviconPixels) return;
            }

            if (core.IsSuspended) { _faviconPending = true; return; }

            // Fetch only declared image candidates through the existing WebView's
            // own cache/cookies/CORS rules. No separate HTTP client, icon service,
            // disk cache, request interception, or page DOM modification.
            string expression = FaviconScript.Replace("__PIXELS__", FaviconPixels.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__FAVICON_URL__", JsonSerializer.Serialize(core.FaviconUri));
            string response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (!Current()) return;
            using var json = JsonDocument.Parse(response);
            if (!json.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return;
            string? encoded = value.GetProperty("png").GetString();
            int originalPixels = value.GetProperty("sourcePixels").GetInt32();
            if (encoded == null || encoded.Length > 65536 || originalPixels < _faviconSourcePixels) return;
            byte[] bytes = Convert.FromBase64String(encoded);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!Current()) return;
            _favicon = bitmap;
            _faviconSourcePixels = originalPixels;
            _faviconOrigin = IconOrigin(core.Source);
            _faviconSourceUri = sourceUri;
            NavigationVisualChanged?.Invoke();
        }
        catch (Exception) { /* Missing, blocked, or invalid icons retain the native fallback. */ }
        finally
        {
            _faviconLoading = false;
            if (!_isDisposed && _faviconPending) _faviconTimer?.Start();
        }
    }

    internal async Task<bool> CheckFaviconFormatsAsync()
    {
        if (_hookedCore is not { } core) return false;
        var fixtures = new[] {
            ("ICO", "image/x-icon", Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")))),
            ("SVG", "image/svg+xml", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='128' height='128' viewBox='0 0 128 128'><rect width='128' height='128' fill='#14304f'/><path d='M16 96L64 16L112 96Z' fill='#4ddbc4'/></svg>")))
        };
        bool all = true;
        foreach (var (name, type, encoded) in fixtures)
        {
            _faviconSourcePixels = 0;
            string url = "data:" + type + ";base64," + encoded;
            await core.ExecuteScriptAsync("document.querySelectorAll('link[rel~=icon]').forEach(e=>e.remove());var f=document.createElement('link');f.rel='icon';f.type=" + JsonSerializer.Serialize(type) + ";f.href=" + JsonSerializer.Serialize(url) + ";document.head.appendChild(f);");
            for (int i = 0; i < 70 && (_faviconSourceUri != url || (_favicon?.PixelWidth ?? 0) < FaviconPixels); i++) await Task.Delay(50);
            bool sharp = _faviconSourceUri == url && _faviconSourcePixels >= FaviconPixels && (_favicon?.PixelWidth ?? 0) >= FaviconPixels;
            StealthWorkspace.Services.SmokeTest.Report($"browser: {name} favicon source={_faviconSourcePixels}, decoded={_favicon?.PixelWidth}, sharp={sharp}");
            all &= sharp;
        }
        return all;
    }

    private const string FaviconScript = """
    (async()=>{
      const size=__PIXELS__, deadline=new AbortController(), timer=setTimeout(()=>deadline.abort(),1800);
      try {
        const entries=Array.from(document.querySelectorAll('link[rel~="icon"],link[rel="apple-touch-icon"]')).slice(0,12)
          .map(link=>({url:link.href,score:link.type==='image/svg+xml'?1024:Math.max(0,...(link.sizes?.value.match(/\d+/g)||[]).map(Number))}));
        entries.push({url:__FAVICON_URL__,score:0});
        const seen=new Set(); let best=null;
        for(const entry of entries.sort((a,b)=>b.score-a.score).filter(e=>e.url&&!seen.has(e.url)&&seen.add(e.url)).slice(0,3)) {
          try {
            const url=new URL(entry.url,location.href);
            if(!['http:','https:'].includes(url.protocol)&&!(url.protocol==='data:'&&url.href.startsWith('data:image/')&&url.href.length<524288))continue;
            const response=await fetch(url.href,{credentials:'same-origin',cache:'force-cache',signal:deadline.signal});
            if(!response.ok||Number(response.headers.get('content-length'))>524288)continue;
            const reader=response.body.getReader(), chunks=[];let length=0;
            for(;;){const part=await reader.read();if(part.done)break;length+=part.value.length;if(length>524288){await reader.cancel();break;}chunks.push(part.value);}
            if(length>524288||!length)continue;
            const blob=new Blob(chunks,{type:response.headers.get('content-type')||'image/png'});
            let source, objectUrl;
            try {
              // Read dimensions before asking for a decoded bitmap. Highly
              // compressed oversized favicons must not allocate huge surfaces.
              objectUrl=URL.createObjectURL(blob);source=new Image();source.decoding='async';
              await new Promise((resolve,reject)=>{
                const aborted=()=>{cleanup();reject(new Error('icon timeout'));};
                const cleanup=()=>{source.onload=source.onerror=null;deadline.signal.removeEventListener('abort',aborted);};
                source.onload=()=>{cleanup();resolve();};source.onerror=()=>{cleanup();reject(new Error('invalid icon'));};
                deadline.signal.addEventListener('abort',aborted,{once:true});
                if(deadline.signal.aborted){aborted();return;}
                source.src=objectUrl;
              });
              const pixels=Math.max(source.naturalWidth,source.naturalHeight);
              if(pixels>4096||source.naturalWidth*source.naturalHeight>4194304||source.naturalWidth<1||source.naturalHeight<1)continue;
              const canvas=new OffscreenCanvas(size,size),ctx=canvas.getContext('2d');
              ctx.imageSmoothingEnabled=true;ctx.imageSmoothingQuality='high';
              const ratio=Math.min(size/source.naturalWidth,size/source.naturalHeight),w=source.naturalWidth*ratio,h=source.naturalHeight*ratio;
              ctx.drawImage(source,(size-w)/2,(size-h)/2,w,h);
              const png=await canvas.convertToBlob({type:'image/png'}),bytes=new Uint8Array(await png.arrayBuffer());
              const item={sourcePixels:pixels,png:btoa(String.fromCharCode(...bytes))};
              if(!best||pixels>best.sourcePixels)best=item;
              if(pixels>=size)return best;
            } finally {if(source){source.onload=source.onerror=null;source.removeAttribute('src');}if(objectUrl)URL.revokeObjectURL(objectUrl);}
          }catch{}
          if(deadline.signal.aborted)break;
        }
        return best;
      }finally{clearTimeout(timer);}
    })()
    """;
}
