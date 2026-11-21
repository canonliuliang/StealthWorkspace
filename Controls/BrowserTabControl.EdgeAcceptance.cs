using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Services;
using Windows.ApplicationModel.DataTransfer;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private static bool IsEdgeAcceptance => SmokeTest.UiSmoke && SmokeTest.BrowserAudit && AppPaths.HasDataRootOverride &&
        Environment.GetCommandLineArgs().Contains("--verify-edge-inprivate");

    private static void ConfigureEdgeAcceptanceProfile(ProcessStartInfo launch)
    {
        if (!IsEdgeAcceptance) return;
        var profile = Path.Combine(AppPaths.DataRoot, "isolated-edge-profile");
        Directory.CreateDirectory(profile);
        launch.ArgumentList.Add("--user-data-dir=" + profile);
        launch.ArgumentList.Add("--no-first-run");
        launch.ArgumentList.Add("--no-default-browser-check");
    }

    private async Task VerifyEdgeMenuAsync(Button action, string? expectedPage)
    {
        if (!IsEdgeAcceptance || (expectedPage != null && (!Uri.TryCreate(expectedPage, UriKind.Absolute, out var uri) ||
            !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp)))
            throw new InvalidOperationException("Edge verification requires the isolated menu audit and its local test page.");

        async Task OpenMenuAsync()
        {
            if (!MoreActionsFlyout.IsOpen) MediaAcceptance.Invoke(BrowserMenuBtn);
            for (int i = 0; i < 100 && !action.IsLoaded; i++) await Task.Delay(25);
            if (!action.IsLoaded || !action.IsEnabled || action.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Edge menu action unavailable.");
        }

        bool restoreMenu = MoreActionsFlyout.IsOpen;
        await OpenMenuAsync();
        string kind = expectedPage == null ? "blank" : "page";
        string marker = Path.Combine(AppPaths.DataRoot, "edge-window-" + kind);
        string nonce = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(marker + ".request.json", JsonSerializer.Serialize(new { nonce, expectedPage, processId = Environment.ProcessId }));
        if (CleanerAccess.IsRestricted)
        {
            DataPackage? copied = null;
            int writes = 0;
            bool privateOptions = false;
            _shareLinkWriter = (data, options) =>
            {
                writes++;
                copied = data;
                privateOptions = !options.IsAllowedInHistory && !options.IsRoamable;
                return true;
            };
            try
            {
                MediaAcceptance.Invoke(action);
                await ChooseEdgeGuidanceAsync("CloseButton", expectedPage == null ? "知道了" : "取消");
                if (writes != 0) throw new InvalidOperationException("An unconfirmed action copied the URL.");
                if (expectedPage != null)
                {
                    await OpenMenuAsync();
                    MediaAcceptance.Invoke(action);
                    await ChooseEdgeGuidanceAsync("PrimaryButton", "复制当前网页地址");
                    for (int i = 0; i < 100 && (writes == 0 || BrowserErrorBar.Title != "已复制网页地址"); i++) await Task.Delay(25);
                    if (writes != 1 || !privateOptions || copied == null || await copied.GetView().GetTextAsync() != expectedPage ||
                        BrowserErrorBar.Title != "已复制网页地址" || GetCurrentDocumentUrl() != expectedPage)
                        throw new InvalidOperationException("Private handoff did not copy the exact URL with the confirmed options.");
                }
                SmokeTest.Report("menu UI: restricted Edge InPrivate guidance verified for " + kind +
                    (expectedPage == null ? "; close copied nothing" : "; cancel copied nothing; confirmed the exact URL without clipboard history or roaming"));
            }
            finally
            {
                // Delayed test UI must never write to the user's clipboard.
                _shareLinkWriter = static (_, _) => false;
            }
        }
        else
        {
            MediaAcceptance.Invoke(action);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker + ".reply") && timer.Elapsed < TimeSpan.FromSeconds(65)) await Task.Delay(100);
            if (!File.Exists(marker + ".reply") || (await File.ReadAllTextAsync(marker + ".reply")).Trim() != nonce)
                throw new InvalidOperationException("An independent observer did not confirm the external Edge InPrivate " + kind + " window.");
            SmokeTest.Report("menu UI: external Edge InPrivate " + kind + " window verified");
        }
        if (restoreMenu) await OpenMenuAsync();
    }

    private async Task ChooseEdgeGuidanceAsync(string name, string expectedLabel)
    {
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(25);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (FindFixtureButton(popup.Child, name) is not { } answer || answer.Content?.ToString() != expectedLabel) continue;
                if (!ContainsDirections(popup.Child)) throw new InvalidOperationException("The Edge handoff steps were not visible.");
                MediaAcceptance.Invoke(answer);
                for (int j = 0; j < 120 && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => ContainsDirections(p.Child)); j++) await Task.Delay(25);
                return;
            }
        }
        throw new InvalidOperationException("The handoff button was not available: " + expectedLabel);
    }

    private static bool ContainsDirections(DependencyObject? node)
    {
        if (node == null) return false;
        if (node is TextBlock text && text.Text.Contains("Ctrl+Shift+N") && text.Text.Contains("InPrivate")) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (ContainsDirections(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
}
