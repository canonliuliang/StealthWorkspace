using StealthWorkspace.Controls;
using StealthWorkspace.Pages;
using StealthWorkspace.Services;
using Microsoft.UI.Xaml.Controls;

namespace StealthWorkspace;

public sealed partial class MainWindow
{
    private async Task RunLoginRetentionAcceptanceAsync()
    {
        try
        {
            string phase = LoginRetentionAcceptance.Phase;
            if (!AppPaths.HasDataRootOverride || !LoginRetentionAcceptance.Requested || SmokeTest.OutputPath == null ||
                File.ReadAllText(Path.Combine(AppPaths.DataRoot, "login-fixture.id")) != LoginRetentionAcceptance.RunId)
                throw new InvalidOperationException("A marked synthetic vault is required.");
            bool decoy = phase == "decoy";
            async Task Unlock()
            {
                if (!VaultService.Instance.HasVault)
                {
                    if (phase is not ("seed" or "other")) throw new InvalidOperationException("Previous fixture vault missing.");
                    await VaultService.Instance.InitializeVaultAsync("LoginFixture#Main123", "LoginFixture#Decoy123");
                }
                if (!VaultService.Instance.IsUnlocked)
                {
                    using var credentials = await VaultService.Instance.VerifyPasswordAsync(decoy ? "LoginFixture#Decoy123" : "LoginFixture#Main123");
                    if (!VaultService.Instance.ApplyUnlock(credentials)) throw new InvalidOperationException("Fixture could not unlock.");
                }
                if (VaultService.Instance.IsDecoyMode != decoy) throw new InvalidOperationException("Unexpected fixture workspace.");
                OnUnlocked();
            }
            async Task<BrowserTabControl> OpenBrowser()
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                for (int i = 0; i < 120; i++)
                {
                    await Task.Delay(50);
                    if (NavFrame.Content is BrowserPage page && page.SmokeSelectedTab is { IsLoaded: true } tab) return tab;
                }
                throw new InvalidOperationException("Fixture browser missing.");
            }
            void CheckEmptyHistory()
            {
                if (BrowserSessionHistory.Items.Count != 0 || BrowserSessionHistory.RecentlyClosed.Count != 0)
                    throw new InvalidOperationException("An earlier session's history is still visible.");
            }
            async Task WaitForHistoryCheckAsync(string marker)
            {
                string id = LoginRetentionAcceptance.RunId;
                File.WriteAllText(SmokeTest.OutputPath + marker, id);
                for (int i = 0; i < 1800; i++)
                {
                    string path = SmokeTest.OutputPath + ".checked";
                    if (File.Exists(path) && File.ReadAllText(path) == id) return;
                    await Task.Delay(50);
                }
                throw new TimeoutException("The synthetic browser history was not checked.");
            }

            await Unlock();
            CheckEmptyHistory();
            var tab = await OpenBrowser();
            switch (phase)
            {
                case "seed":
                    await tab.VerifyLoginFixtureAsync(false, "login", true, true);
                    await tab.VerifyLoginFixtureAsync(true, "login", true, true);
                    if (BrowserSessionHistory.Items.Count < 2) throw new InvalidOperationException("Visits were absent before lock.");
                    LockWorkspace();
                    CheckEmptyHistory();
                    if (VaultService.Instance.IsUnlocked || NavFrame.Content is not LockPage) throw new InvalidOperationException("Lock did not isolate the fixture.");
                    // The controller reads only History SQLite files under its freshly created test directory.
                    await WaitForHistoryCheckAsync(".locked");
                    await Unlock();
                    CheckEmptyHistory();
                    tab = await OpenBrowser();
                    await tab.VerifyLoginFixtureAsync(false, "read", true, true);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    break;
                case "logout":
                    await tab.VerifyLoginFixtureAsync(false, "read", true, true);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    // The server expires the HttpOnly cookie without deleting unrelated site preferences.
                    await tab.VerifyLoginFixtureAsync(false, "logout", false, true);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    break;
                case "crash":
                case "recover":
                    await tab.VerifyLoginFixtureAsync(false, "read", true, true);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    // The controller terminates only its own marked fixture process in the crash phase.
                    // In recovery it inspects the preceding phase's SQLite visits before this window closes.
                    await WaitForHistoryCheckAsync(".live");
                    break;
                case "forget":
                    await tab.VerifyLoginFixtureAsync(false, "read", false, true);
                    await tab.VerifyLoginFixtureAsync(false, "login", true, true);
                    await tab.VerifyLoginFixtureAsync(false, "read", true, true);
                    await tab.ForgetLoginFixtureSiteAsync();
                    await tab.VerifyLoginFixtureAsync(false, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    break;
                case "lasttab":
                    await tab.VerifyLoginFixtureAsync(false, "read", true, true);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    await WaitForHistoryCheckAsync(".live");
                    var closingPage = (BrowserPage)NavFrame.Content;
                    var tabs = (TabView)closingPage.FindName("BrowserTabView");
                    if (tabs.TabItems.Count != 1 || tabs.SelectedItem is not TabViewItem item ||
                        !ReferenceEquals(item.Content, tab) || BrowserTabControl.FindFixtureButton(item, "CloseButton") is not { } close)
                        throw new InvalidOperationException("No native close button on the last tab.");
                    MediaAcceptance.Invoke(close);
                    bool BlankReady() => tabs.TabItems.Count == 1 && closingPage.SmokeSelectedTab is { IsLoaded: true, HasBrowserControl: false } blank && blank != tab;
                    for (int i = 0; i < 100 && !BlankReady(); i++) await Task.Delay(60);
                    if (!BlankReady())
                        throw new InvalidOperationException("The last website did not close into a fresh native tab.");
                    // Allow the last real engine controller to finish leaving before normal program exit.
                    await Task.Delay(1000);
                    SmokeTest.Report("login fixture: closed last website using its tab close button, leaving a native blank tab");
                    break;
                case "decoy":
                case "other":
                    await tab.VerifyLoginFixtureAsync(false, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(true, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(false, "login", true, true);
                    break;
                case "verify":
                    await tab.VerifyLoginFixtureAsync(false, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(true, "read", true, true);
                    if (LoginRetentionAcceptance.ExerciseClearAll) await tab.VerifyLoginFixtureAsync(false, "login", true, true);
                    break;
                case "clearall":
                    await tab.ClearLoginFixturesFromBlankTabAsync();
                    await tab.VerifyLoginFixtureAsync(false, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(true, "read", false, false);
                    break;
                case "checkall":
                    await tab.VerifyLoginFixtureAsync(false, "read", false, false);
                    await tab.VerifyLoginFixtureAsync(true, "read", false, false);
                    break;
                default: throw new ArgumentException("Unknown login fixture phase.");
            }
            SmokeTest.Report("login retention phase passed: " + phase);
        }
        catch (Exception ex) { SmokeTest.Report("login retention failed: " + ex); }
        finally { Close(); }
    }
}
