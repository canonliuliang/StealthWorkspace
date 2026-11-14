using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task<bool> PrepareEndurancePageAsync()
    {
        if (!await RunSmokeNavigationAsync()) return false;
        if (_hookedCore is not { } core) return false;
        await core.ExecuteScriptAsync("document.body.innerHTML='<h1>Browser endurance fixture</h1><input id=check>';document.getElementById('check').value='unsaved value';");
        return await VerifySmokeResumeAsync();
    }

    internal (long Memory, TimeSpan Cpu) GetEnduranceRendererMetrics()
    {
        long memory = 0;
        TimeSpan cpu = TimeSpan.Zero;
        if (_hookedCore is { } core)
        foreach (var info in core.Environment.GetProcessInfos())
        {
            if (info.Kind != CoreWebView2ProcessKind.Renderer) continue;
            try
            {
                using var process = Process.GetProcessById((int)info.ProcessId);
                memory += process.PrivateMemorySize64;
                cpu += process.TotalProcessorTime;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return (memory, cpu);
    }
}
