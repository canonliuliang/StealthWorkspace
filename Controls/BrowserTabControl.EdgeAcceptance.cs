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

        async Task OpenMenuAsync(bool reopen = false)
        {
            if (reopen)
            {
                if (MoreActionsFlyout.IsOpen) MoreActionsFlyout.Hide();
                for (int i = 0; i < 80 && action.IsLoaded; i++) await Task.Delay(20);
            }
            if (!MoreActionsFlyout.IsOpen) MediaAcceptance.Invoke(BrowserMenuBtn);
            for (int i = 0; i < 100 && (!action.IsLoaded || !MoreActionsFlyout.IsOpen); i++) await Task.Delay(25);
            await Task.Delay(130);
            if (!action.IsLoaded || action.ActualHeight <= 0 || !action.IsEnabled || action.Visibility != Visibility.Visible)
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
            bool allowWrite = false;
            _shareLinkWriter = (data, options) =>
            {
                writes++;
                copied = data;
                privateOptions = !options.IsAllowedInHistory && !options.IsRoamable;
                return allowWrite;
            };
            try
            {
                MediaAcceptance.Invoke(action);
                await ChooseEdgeGuidanceAsync("CloseButton", expectedPage == null ? "知道了" : "取消");
                if (writes != 0) throw new InvalidOperationException("An unconfirmed action copied the URL.");
                SmokeTest.Report("menu UI: Edge handoff " + kind + " closed without clipboard access");
                if (expectedPage != null)
                {
                    await OpenMenuAsync(reopen: true);
                    MediaAcceptance.Invoke(action);
                    SmokeTest.Report("menu UI: invoked Edge menu again to exercise clipboard rejection");
                    await ChooseEdgeGuidanceAsync("PrimaryButton", "复制当前网页地址", expectedPage);
                    if (writes != 1) throw new InvalidOperationException("The first clipboard attempt did not reach the writer.");
                    allowWrite = true;
                    await ChooseEdgeGuidanceAsync("PrimaryButton", "复制当前网页地址");
                    for (int i = 0; i < 100 && (writes == 0 || BrowserErrorBar.Title != "已复制网页地址"); i++) await Task.Delay(25);
                    if (writes != 2 || !privateOptions || copied == null || await copied.GetView().GetTextAsync() != expectedPage ||
                        BrowserErrorBar.Title != "已复制网页地址" || GetCurrentDocumentUrl() != expectedPage)
                        throw new InvalidOperationException("Private handoff did not copy the exact URL with the confirmed options.");
                }
                SmokeTest.Report("menu UI: restricted Edge InPrivate guidance verified for " + kind +
                    (expectedPage == null ? "; close copied nothing" : "; cancel copied nothing; rejected clipboard kept the full address selected; confirmed the exact URL without clipboard history or roaming"));
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

    private async Task ChooseEdgeGuidanceAsync(string name, string expectedLabel, string? rejectedAddress = null)
    {
        string observed = "no popup";
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(25);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (FindFixtureButton(popup.Child, name) is not { } answer) continue;
                observed = answer.Content?.ToString() ?? "no label";
                if (observed != expectedLabel) continue;
                if (!ContainsDirections(popup.Child)) throw new InvalidOperationException("The Edge handoff steps were not visible.");
                SmokeTest.Report("menu UI: invoking Edge handoff dialog button " + expectedLabel);
                MediaAcceptance.Invoke(answer);
                if (rejectedAddress != null)
                {
                    for (int j = 0; j < 100; j++)
                    {
                        await Task.Delay(25);
                        var field = FindEdgeAuditVisual<TextBox>(popup.Child, b => b.Name == "EdgeHandoffAddress");
                        var error = FindEdgeAuditVisual<TextBlock>(popup.Child, b => b.Visibility == Visibility.Visible && b.Text.Contains("Windows 未允许自动复制"));
                        if (field is { IsLoaded: true, IsReadOnly: true, SelectionStart: 0 } &&
                            field.Text == rejectedAddress && field.SelectedText == rejectedAddress && error != null && answer.IsEnabled)
                        {
                            SmokeTest.Report("menu UI: rejected clipboard preserved the open dialog, selected URL and retry button");
                            return;
                        }
                    }
                    throw new InvalidOperationException("Rejected clipboard writing must keep the complete address visible, selected and available for retry.");
                }
                for (int j = 0; j < 120 && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => ContainsDirections(p.Child)); j++) await Task.Delay(25);
                // After the popup leaves the visual tree, ShowAsync can still be
                // completing its native close transition.
                await Task.Delay(350);
                return;
            }
        }
        throw new InvalidOperationException("The handoff button was not available: " + expectedLabel + "; observed " + observed);
    }

    private static bool ContainsDirections(DependencyObject? node)
        => FindEdgeAuditVisual<TextBlock>(node, text => text.Text.Contains("Ctrl+Shift+N") && text.Text.Contains("InPrivate")) != null;

    private static T? FindEdgeAuditVisual<T>(DependencyObject? node, Func<T, bool> predicate) where T : DependencyObject
    {
        if (node == null) return null;
        if (node is T wanted && predicate(wanted)) return wanted;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindEdgeAuditVisual(VisualTreeHelper.GetChild(node, i), predicate) is { } found) return found;
        return null;
    }
}
