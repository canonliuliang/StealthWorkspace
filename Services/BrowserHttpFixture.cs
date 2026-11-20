using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StealthWorkspace.Services;

internal sealed class BrowserHttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serving;
    private readonly List<Task> _requests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _requestCounts = new();
    internal string Address { get; }
    internal string LastRequest { get; private set; } = "";
    internal string? LastPopupMethod { get; private set; }
    internal int CountRequests(string path) => _requestCounts.GetValueOrDefault(path);

    internal BrowserHttpFixture()
    {
        _listener.Start();
        Address = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _requests.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        try
        {
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string request = await reader.ReadLineAsync(_lifetime.Token) ?? "";
                string? header;
                long rangeStart = 0;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_lifetime.Token)))
                    if (header.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(header[13..].Split('-')[0], out rangeStart);
                string path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                path = path.Split('?')[0];
                LastRequest = path;
                _requestCounts.AddOrUpdate(path, 1, (_, count) => count + 1);
                if (path == "/child") LastPopupMethod = request.Split(' ')[0];
                if (path == "/download-slow")
                {
                    const int chunks = 64, chunkSize = 65536;
                    long total = chunks * chunkSize;
                    rangeStart = Math.Clamp(rangeStart, 0, total - 1);
                    string rangeHeader = rangeStart > 0 ? $"Content-Range: bytes {rangeStart}-{total - 1}/{total}\r\n" : "";
                    byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(rangeStart > 0 ? "206 Partial Content" : "200 OK")}\r\nContent-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nETag: \"fixture-v1\"\r\n{rangeHeader}Content-Disposition: attachment; filename=slow-fixture.bin\r\nContent-Length: {total - rangeStart}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _lifetime.Token);
                    byte[] chunk = Enumerable.Repeat((byte)0x5A, chunkSize).ToArray();
                    for (long offset = rangeStart; offset < total; offset += chunkSize)
                    {
                        await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunkSize, total - offset)), _lifetime.Token);
                        await Task.Delay(70, _lifetime.Token);
                    }
                    return;
                }
                if (path == "/slow-resource") await Task.Delay(1500, _lifetime.Token);
                if (path == "/stalled-image") await Task.Delay(30000, _lifetime.Token);
                string status = path == "/challenge" ? "403 Forbidden" : path == "/missing" ? "404 Not Found" : path == "/redirect" ? "302 Found" : "200 OK";
                string extra = path == "/redirect" ? "Location: /page\r\n" : path == "/download" ? "Content-Disposition: attachment; filename=browser-check.txt\r\n" : "";
                if (path == "/gallery-csp") extra += "Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'none'; script-src 'nonce-fixture'\r\n";
                if (path == "/cached-script") extra += "Cache-Control: public, max-age=3600\r\n";
                string html = path switch
                {
                    "/cache-page" => "<!doctype html><title>Cache fixture</title><script src='/cached-script'></script><h1>Cache reload fixture</h1>",
                    "/partition-host" => $"<!doctype html><title>Partition fixture</title><script>onmessage = e => {{if (e.origin === '{Address}') window.embeddedValue = e.data;}}</script><iframe id='child'></iframe><script>child.src = '{Address}/partition-child' + location.search;</script>",
                    "/partition-child" => "<!doctype html><script>if (location.search === '?seed') localStorage.setItem('r161-partition','embedded'); parent.postMessage(localStorage.getItem('r161-partition'), new URL(document.referrer).origin);</script>",
                    "/cached-script" => "window.fixtureVersion=" + CountRequests(path) + ";",
                    "/pending-page" => "<!doctype html><title>Pending resource fixture</title><input id=check value='unsaved value'><img src=/stalled-image><script>window.ticks=0;setInterval(()=>window.ticks++,20)</script>",
                    "/gallery-lazy" => "<!doctype html><meta charset=utf-8><title>Lazy image fixture</title><img src=/image-placeholder data-src=/image-lazy-one alt='延迟长图一' width=200 height=800><img src=/image-placeholder data-original=/image-lazy-two alt='延迟长图二' width=200 height=800>",
                    "/image-placeholder" => "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'></svg>",
                    "/gallery" or "/gallery-csp" => "<!doctype html><meta charset=utf-8><title>Image reader fixture</title><h1>图片阅读测试</h1><input id=draft value='未提交的输入'><p>使用图片右键菜单打开阅读器</p><img src=/image-wide alt='横向图片' width=360 height=240><img src=/image-tall alt='长图阅读' width=200 height=800><img src=/image-wide alt='重复图片' width=120 height=80><img src=/image-broken alt='加载失败的图片' width=200 height=200>",
                    "/image-wide" => "<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='800'><rect width='1200' height='800' fill='#1c5c94'/><circle cx='600' cy='400' r='250' fill='#83c5ff'/><text x='600' y='430' font-family='sans-serif' font-size='96' text-anchor='middle'>1</text></svg>",
                    "/image-tall" or "/image-lazy-one" or "/image-lazy-two" => "<svg xmlns='http://www.w3.org/2000/svg' width='800' height='3200'><rect width='800' height='3200' fill='#2d6b50'/>" + string.Concat(Enumerable.Range(0, 8).Select(i => $"<text x='400' y='{200 + 400 * i}' fill='white' font-family='sans-serif' font-size='96' text-anchor='middle'>{i + 1}</text>")) + "</svg>",
                    "/image-broken" => "intentionally not an image",
                    "/fullscreen" => "<!doctype html><title>Fullscreen fixture</title><button id=fullscreen onclick='document.documentElement.requestFullscreen().catch(e=>window.fullscreenError=e.name+e.message)'>Enter fullscreen</button><input id=draft value='keep my draft'>",
                    "/reading" => "<!doctype html><meta charset=utf-8><title>Reading fixture</title><link rel=icon href=/tile><style>body{font:16px system-ui;background:#f3f4f6}main{display:grid;grid-template-columns:repeat(4,1fr);gap:16px}article{background:white;padding:12px}img{width:128px;height:128px}</style><h1>Image reading workload</h1><main>" +
                        string.Concat(Enumerable.Range(0, 48).Select(i => $"<article><img src='/tile?{i}'><p>Chapter {i}: synthetic reading content</p></article>")) + "</main>",
                    "/popup-frame" => $"<!doctype html><title>Embedded popup fixture</title><iframe src='{Address.Replace("127.0.0.1", "localhost")}/popup'></iframe>",
                    "/popup" => """
                        <!doctype html><title>Popup fixture</title><h1>Popup and reading workflow</h1>
                        <button id="open" onclick="window.child=window.open('/child','fixture-child')">Open child</button>
                        <button id="blank" onclick="window.child=window.open('about:blank');child.document.write('<title>Written child</title><h1 id=written>Written content</h1>')">Write child</button>
                        <form action="/child" method="post" target="_blank"><input name="value" value="fixture"><button id="post">Submit to new tab</button></form>
                        <a id="background" href="/child" target="_blank">Open background</a>
                        <script>sessionStorage.setItem('popup-context','kept');window.received=0;addEventListener('message',e=>{if(e.origin===location.origin&&e.data==='child-ready')window.received++});setTimeout(()=>window.open('/child','automatic'),300);</script>
                        """,
                    "/child" => "<!doctype html><title>Child fixture</title><h1>Child</h1><input id=check><script>if(window.opener)window.opener.postMessage('child-ready',location.origin);</script>",
                    "/download" => "synthetic download check",
                    _ => "<!doctype html><title>HTTP fixture</title><h1>" + status + "</h1><a id='download' href='/download'>Download</a>" + (path == "/slowpage" ? "<img src='/slow-resource'>" : "")
                };
                byte[] body = path == "/tile" ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"), _lifetime.Token) : Encoding.UTF8.GetBytes(html);
                if (path == "/tile") { await Task.Delay(40, _lifetime.Token); extra += "Cache-Control: max-age=600\r\n"; }
                string type = path.StartsWith("/image-") ? "image/svg+xml" : path == "/tile" ? "image/png" : path == "/download" ? "application/octet-stream" : path == "/cached-script" ? "text/javascript" : "text/html; charset=utf-8";
                byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\n{extra}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _lifetime.Token);
                await stream.WriteAsync(body, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { } // Browser may cancel a request by navigating away.
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        await _serving;
        await Task.WhenAll(_requests);
        _lifetime.Dispose();
    }
}
