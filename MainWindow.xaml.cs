using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using StealthWorkspace.Models;
using StealthWorkspace.Pages;
using StealthWorkspace.Services;
using Windows.Graphics;
using Windows.System;

namespace StealthWorkspace;

public sealed partial class MainWindow : Window
{
    public static new MainWindow? Current { get; private set; }
    public static IntPtr CurrentHandle { get; private set; } = IntPtr.Zero;

    private readonly DispatcherTimer _idleTimer = new();
    private bool _rootLoaded;
    private bool _isLocked;
    private bool _locking;
    private bool _suppressNavSelection;
    private bool _pageNavigationBlocked;
    private bool _camouflageVisible;
    internal bool IsPrivacyCoverVisible => _camouflageVisible;
    private WeakReference<UIElement>? _focusBeforeCamouflage;
    private bool _bossKeyMuted;
    private bool _restoreAppAudioAfterCover;
    private bool _paneVisibleBeforeFullScreen = true;
    private bool _browserNavigationLayout;
    private bool _paneOpenBeforeBrowser;
    private MotionCue _navigationCue;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private int _pendingResidueCount;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        VaultService.Instance.BindToUiContext(System.Threading.SynchronizationContext.Current
            ?? new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(DispatcherQueue));
        UpdateTitle(GetCamouflageTitle(SettingsService.Instance.Data.CamouflageIndex));
        VaultWork.Changed += RefreshStatusBadge;

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
            UpdateCaptionColors();
        }
        catch (Exception ex)
        {
            AppLog.Warn("标题栏初始化降级: " + ex.GetType().Name);
        }

        try
        {
            CurrentHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); }
            catch (Exception ex) { AppLog.Warn("窗口图标初始化失败: " + ex.GetType().Name); }
            var bossKey = BossKeyService.Instance;
            bossKey.Initialize(CurrentHandle);
            bossKey.BossKeyPressed += OnBossKeyPressed;
            bossKey.WindowMinimized += OnWindowMinimized;
            bossKey.SystemSessionLeaving += OnSystemSessionLeaving;
            bossKey.SystemResumed += RefreshIdleDeadline;
            bossKey.WindowVisibilityChanged += OnWindowVisibilityChanged;
        }
        catch (Exception ex)
        {
            AppLog.Error("窗口防护初始化失败", ex);
        }

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        SetupWindowPlacement();
        SetupIdleDetection();

        if (Content is FrameworkElement root)
        {
            root.Loaded += OnRootLoaded;
        }
        else
        {
            LockWorkspace();
        }
    }

    #region Startup

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => UpdateCaptionColors();

    private void UpdateCaptionColors()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        byte foreground = dark ? (byte)255 : (byte)0;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, foreground, foreground, foreground);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
        byte inactive = dark ? (byte)170 : (byte)100;
        bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, inactive, inactive, inactive);
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(28, foreground, foreground, foreground);
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(45, foreground, foreground, foreground);
    }

    private void SetupWindowPlacement()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (area == null) return;

            RectInt32 work = area.WorkArea;
            double dpi = NativeMethods.GetDpiForWindow(CurrentHandle) / 96.0;
            int width = Math.Min(work.Width - 48, (int)((SmokeTest.UiSmoke ? SmokeTest.UiWidth : 1100) * dpi));
            int height = Math.Min(work.Height - 48, (int)((SmokeTest.UiSmoke ? SmokeTest.UiHeight : 740) * dpi));
            int x = work.X + Math.Max(0, (work.Width - width) / 2);
            int y = work.Y + Math.Max(0, (work.Height - height) / 2);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            AppLog.Warn("窗口尺寸设置失败: " + ex.GetType().Name);
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_rootLoaded) return;
        _rootLoaded = true;

        ApplySettingsOnStartup();
        if (SmokeTest.UiSmoke && SmokeTest.UiTheme is { } theme)
            RootGrid.RequestedTheme = theme == "Light" ? ElementTheme.Light : ElementTheme.Dark;
        LockWorkspace();
        _ = StartupMaintenanceAsync();

        SmokeTest.Report($"UI ready: page={NavFrame.CurrentSourcePageType?.Name ?? "null"}, title={Title}, " +
                         $"paneVisible={NavView.IsPaneVisible}, lockPageVisible={NavView.IsPaneVisible == false}");

        if (SmokeTest.Enabled)
        {
            SmokeTest.Report($"motion: custom animations={UiMotion.AnimationsEnabled}, scale={RootGrid.XamlRoot.RasterizationScale:0.##}");
            if (SmokeTest.UiSmoke)
            {
                if (SmokeTest.InteractiveFixture) _ = OpenInteractiveFixtureAsync();
                else if (!SmokeTest.Interactive) _ = RunUiSmokeWalkAsync();
            }
            else
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(1500);
                    SmokeTest.Report("smoke: 关闭窗口");
                    try { Close(); } catch { }
                });
            }
        }
    }

    private async Task OpenInteractiveFixtureAsync()
    {
        // Only --ui-smoke with its isolated data-root override reaches this path.
        // It never accepts or prompts for the real user's vault password.
        try
        {
            if (VaultService.Instance.HasVault)
                VaultService.Instance.ApplyUnlock(await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123"));
            else await VaultService.Instance.InitializeVaultAsync("SmokeTest#123");
            OnUnlocked();
            // This explicitly requested fixture contains only synthetic test data.
            // Do not save this diagnostic capture setting to either vault.
            BossKeyService.Instance.SetDisplayAffinityProtection(false);
            SmokeTest.Report("interactive fixture ready, pid=" + Environment.ProcessId);
        }
        catch (Exception ex) { SmokeTest.Report("interactive fixture failed: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Developer-only page walk used by `--ui-smoke`: unlocks a throw-away vault in an
    /// isolated data root, navigates through every page, locks/unlocks again and closes.
    /// Any runtime XAML/resource/binding failure stops the walk and fails the result.
    /// </summary>
    private async Task RunUiSmokeWalkAsync()
    {
        try
        {
            await Task.Delay(300);

            if (!VaultService.Instance.IsUnlocked)
            {
                if (VaultService.Instance.HasVault)
                {
                    using var existing = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                    VaultService.Instance.ApplyUnlock(existing);
                }
                else
                {
                    // Exercise the real window lock entry while setup is still in flight.
                    Task setup = VaultService.Instance.InitializeVaultAsync("SmokeTest#123");
                    LockWorkspace();
                    await setup;
                    bool setupStayedLocked = VaultService.Instance.HasVault && !VaultService.Instance.IsUnlocked && _isLocked;
                    SmokeTest.Report("ui-smoke: window lock during initial setup prevents automatic unlock=" + setupStayedLocked);
                    if (!setupStayedLocked) throw new InvalidOperationException("Initial setup bypassed a window lock");
                    using var created = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                    if (!VaultService.Instance.ApplyUnlock(created)) throw new InvalidOperationException("New fixture could not be unlocked");
                }
            }
            SmokeTest.Report($"ui-smoke: unlocked={VaultService.Instance.IsUnlocked}, vault={VaultService.Instance.HasVault}");
            OnUnlocked();
            await Task.Delay(500);

            if (SmokeTest.ShutdownAudit)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(200);
                var tab = ((BrowserPage)NavFrame.Content).SmokeSelectedTab
                    ?? throw new InvalidOperationException("Missing shutdown fixture tab");
                Task<bool> navigation = tab.RunSmokeNavigationAsync();
                if (SmokeTest.ShutdownDuringInitialization)
                {
                    _ = navigation.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    SmokeTest.Report("shutdown audit: initializing WebView before direct Window.Close");
                    return;
                }
                if (!await navigation) throw new InvalidOperationException("Shutdown fixture navigation failed");
                SmokeTest.Report("shutdown audit: live WebView before direct Window.Close");
                return;
            }

            if (SmokeTest.ExperienceTest)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(400);
                if (NavFrame.Content is not BrowserPage experienceBrowser || experienceBrowser.SmokeSelectedTab is not { } experienceTab)
                    throw new InvalidOperationException("Experience browser missing");
                await experienceTab.CheckPrivacyExperienceAsync();
                NavigateToVault();
                await Task.Delay(400);
                await ImageExperienceAcceptance.RunAsync((VaultPage)NavFrame.Content, NavigateToPlayer,
                    () => NavFrame.Content as PlayerPage, () => NavFrame.Content as VaultPage,
                    () => NavigateToPage(typeof(PlayerPage), null, "player", force: true));
                SmokeTest.Report("experience complete");
                return;
            }

            if (SmokeTest.RenderAudit)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(250);
                if (NavFrame.Content is not BrowserPage renderPage || renderPage.SmokeSelectedTab is not { } renderTab)
                    throw new InvalidOperationException("Render audit page missing");
                await renderTab.RunRenderAuditAsync();
                return;
            }

            if (SmokeTest.BrowserAudit)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(250);
                if (NavFrame.Content is not BrowserPage auditPage) throw new InvalidOperationException("Audit page missing");
                await auditPage.RunBrowserAuditAsync();
                return;
            }

            if (SmokeTest.BrowserProbeUrl is { } probeUrl)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(250);
                if (NavFrame.Content is BrowserPage probePage && probePage.SmokeSelectedTab is { } probeTab)
                    foreach (string address in probeUrl.Split('|', StringSplitOptions.RemoveEmptyEntries)) await probeTab.RunNetworkProbeAsync(address);
                return;
            }

            if (SmokeTest.BrowserSoakMinutes > 0)
            {
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                await Task.Delay(250);
                if (NavFrame.Content is not BrowserPage soakPage) throw new InvalidOperationException("Soak browser missing");
                await soakPage.RunEnduranceAsync(TimeSpan.FromMinutes(SmokeTest.BrowserSoakMinutes));
                return;
            }

            NavigateToPage(typeof(VaultPage), null, "vault", force: true);
            await Task.Delay(500);

            // Use a real image and the same import entry point as picker and drag/drop.
            string fixture = Path.Combine(Path.GetDirectoryName(SmokeTest.OutputPath!)!, "vault-check-" + Guid.NewGuid().ToString("N") + ".bmp");
            // A recognizable raster with distinct pixels; use no private files.
            using (var bitmap = new BinaryWriter(File.Create(fixture)))
            {
                bitmap.Write((ushort)0x4D42);
                bitmap.Write(54 + 96 * 72 * 3);
                bitmap.Write(0);
                bitmap.Write(54);
                bitmap.Write(40);
                bitmap.Write(96);
                bitmap.Write(72);
                bitmap.Write((ushort)1);
                bitmap.Write((ushort)24);
                for (int i = 0; i < 6; i++) bitmap.Write(0);
                for (int y = 0; y < 72; y++)
                for (int x = 0; x < 96; x++)
                {
                    bitmap.Write((byte)(x < 48 ? 220 : 48));
                    bitmap.Write((byte)(y < 36 ? 200 : 70));
                    bitmap.Write((byte)(x < 48 ? 48 : 230));
                }
            }
            try
            {
                if (NavFrame.Content is not VaultPage vaultPage) throw new InvalidOperationException("媒体库未加载。");
                var import = vaultPage.ImportFilesAsync(new[] { fixture });
                NavigateToPage(typeof(SettingsPage), null, null, force: true);
                if (NavFrame.Content is not SettingsPage || !VaultWork.IsBusy || !StatusProgress.IsActive)
                    throw new InvalidOperationException("切换页面后导入任务或窗口进度不可见。");
                using (var conflict = VaultWork.TryBegin("测试冲突任务"))
                    if (conflict != null) throw new InvalidOperationException("导入期间启动了冲突任务。");
                var result = await import;
                if (!StatusBadge.IsEnabled || VaultWork.IsBusy || _pageNavigationBlocked)
                    throw new InvalidOperationException("导入结束后工作区仍显示忙碌。");
                SmokeTest.Report("vault: import survived navigation, showed window progress and blocked conflicting work");
                if (result.Imported != 1 || result.Failed != 0 || !File.Exists(fixture))
                    throw new InvalidOperationException("媒体导入回归检查未通过。");
                SmokeTest.Report("vault: image imported and original preserved");
                NavigateToVault();
                vaultPage = (VaultPage)NavFrame.Content;
                if (!await vaultPage.WaitForSmokeThumbnailAsync())
                    throw new InvalidOperationException("媒体库图片缩略图未显示。");
                SmokeTest.Report("vault: encrypted image thumbnail displayed");
                NavigateToPlayer(VaultService.Instance.VisibleItems.Last());
                for (int i = 0; i < 40 && NavFrame.Content is PlayerPage { IsImageReady: false }; i++)
                    await Task.Delay(100);
                if (NavFrame.Content is not PlayerPage { IsImageReady: true })
                    throw new InvalidOperationException("图片预览未显示。");
                SmokeTest.Report("vault: image opened in viewer");
            }
            finally
            {
                File.Delete(fixture);
            }

            if (SmokeTest.MediaFixtureRoot != null)
            {
                NavigateToVault();
                await Task.Delay(300);
                await MediaAcceptance.RunAsync((VaultPage)NavFrame.Content, NavigateToPlayer, () => NavFrame.Content as PlayerPage);
            }

            NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
            await Task.Delay(500);
            bool browserOk = NavFrame.Content is BrowserPage browser && await browser.RunSmokeTestAsync();
            if (!browserOk) throw new InvalidOperationException("Browser smoke failed");
            var cachedBrowser = (BrowserPage)NavFrame.Content;
            var retainedTab = cachedBrowser.SmokeSelectedTab;
            NavigateToVault();
            await Task.Delay(300);
            NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
            await Task.Delay(300);
            bool retained = ReferenceEquals(cachedBrowser, NavFrame.Content) && ReferenceEquals(retainedTab, cachedBrowser.SmokeSelectedTab) && retainedTab != null && await retainedTab.VerifySmokeResumeAsync();
            SmokeTest.Report("browser: switching media/browser preserves tab and unsaved DOM=" + retained);
            if (!retained) throw new InvalidOperationException("Browser page navigation lost state");
            int webViewTabs = Controls.BrowserTabControl.InitializedTabCount;
            string webViewRuntime = BrowserProfileManager.GetWebView2RuntimeVersion();
            if (browserOk)
            {
                SmokeTest.Report($"ui-smoke: webview-ready tabs={webViewTabs}, runtime={webViewRuntime}");
            }
            else
            {
                SmokeTest.Report($"ui-smoke: webview-unavailable runtime={webViewRuntime}, error={Controls.BrowserTabControl.LastInitializationError}, detail={BrowserProfileManager.LastInitializationError}");
            }
            await Task.Delay(4000);

            NavigateToPage(typeof(CleanerPage), null, "cleaner", force: true);
            await Task.Delay(3500);

            NavigateToPage(typeof(SettingsPage), null, null, force: true);
            await Task.Delay(600);

            var settingsPage = (SettingsPage)NavFrame.Content;
            var search = (TextBox)settingsPage.FindName("SettingsSearch");
            var transfer = (Border)settingsPage.FindName("BookmarkTransferSection");
            var backupCard = (Border)settingsPage.FindName("BackupSection");
            async Task EnterSettingQueryAsync(string value)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(object sender, TextChangedEventArgs args) => changed.TrySetResult();
                search.TextChanged += OnChanged;
                try
                {
                    search.Text = value;
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { search.TextChanged -= OnChanged; }
            }
            await EnterSettingQueryAsync("HTML");
            if (transfer.Visibility != Visibility.Visible || backupCard.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException($"Settings search failed to isolate matching controls: transfer={transfer.Visibility}, backup={backupCard.Visibility}");
            await EnterSettingQueryAsync("nonexistent synthetic preference");
            if (transfer.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Settings search retained a non-match");
            await EnterSettingQueryAsync("");
            if (transfer.Visibility != Visibility.Visible || backupCard.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Clearing settings search did not restore controls");
            SmokeTest.Report("settings: bookmark transfer search, empty results and clearing the filter passed");
            var titles = (ComboBox)settingsPage.FindName("CamouflageCombo");
            int originalTitleIndex = titles.SelectedIndex;
            titles.SelectedIndex = 2;
            if (!string.IsNullOrEmpty(AppTitleBar.Subtitle) || AppTitleBar.Title != Title ||
                !Title.StartsWith(GetCamouflageTitle(2), StringComparison.Ordinal))
                throw new InvalidOperationException("伪装标题与窗口显示不一致。");
            titles.SelectedIndex = originalTitleIndex;
            SmokeTest.Report("privacy: office title applied to the window and title bar with an empty subtitle");

            NavigateToPage(typeof(PlayerPage), null, "player", force: true);
            await Task.Delay(600);

            var flyout = new Flyout { Content = new TextBlock { Text = "测试" } };
            var flyoutOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            flyout.Opened += (_, _) => flyoutOpened.TrySetResult();
            flyout.ShowAt(StatusBadge);
            await flyoutOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ShowCamouflage();
            await Task.Delay(300);
            if (flyout.IsOpen || WorkspaceContent.Visibility != Visibility.Collapsed ||
                !ReferenceEquals(FocusManager.GetFocusedElement(RootGrid.XamlRoot), CamouflageOverlay))
                throw new InvalidOperationException("Privacy cover retained an open flyout or underlying focus");
            HideCamouflage();
            SmokeTest.Report("motion: camouflage closed the flyout and removed workspace controls from focus");

            // The popup layer sits above page transitions. Lock while a real modal
            // is open and ensure its awaiting action receives cancellation.
            await WorkspaceNativeDialogsAudit.CheckRevocationAsync();
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Content = "测试", CloseButtonText = "关闭" };
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Opened += (_, _) => opened.TrySetResult();
            var dismissed = WorkspaceDialogs.ShowAsync(dialog);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            LockWorkspace();
            if (NavFrame.Content is not LockPage || await dismissed.WaitAsync(TimeSpan.FromSeconds(5)) != ContentDialogResult.None)
                throw new InvalidOperationException("Lock did not dismiss the modal and replace the private page");
            SmokeTest.Report("motion: locking with an open dialog returned cancellation and displayed the lock page");
            await Task.Delay(600);
            SmokeTest.Report($"ui-smoke: locked={!VaultService.Instance.IsUnlocked}");

            using (var pending = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123"))
            {
                if (pending.Status != VaultUnlockStatus.Success) throw new InvalidOperationException("Repeated-lock fixture failed");
                LockWorkspace();
                bool revoked = !VaultService.Instance.ApplyUnlock(pending) && !VaultService.Instance.IsUnlocked && _isLocked;
                SmokeTest.Report("ui-smoke: repeated window lock revokes pending credentials=" + revoked);
                if (!revoked) throw new InvalidOperationException("Repeated window lock did not revoke pending credentials");
            }
            bool originalMinimizeSetting = SettingsService.Instance.Data.LockOnMinimize;
            try
            {
                SettingsService.Instance.Data.LockOnMinimize = true;
                using var pending = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                if (pending.Status != VaultUnlockStatus.Success) throw new InvalidOperationException("Minimize-lock fixture failed");
                OnWindowMinimized();
                bool revoked = !VaultService.Instance.ApplyUnlock(pending) && !VaultService.Instance.IsUnlocked && _isLocked;
                SmokeTest.Report("ui-smoke: minimize while locked revokes pending credentials=" + revoked);
                if (!revoked) throw new InvalidOperationException("Minimize did not revoke pending credentials");
            }
            finally { SettingsService.Instance.Data.LockOnMinimize = originalMinimizeSetting; }

            // Deliver notifications to this isolated window without locking the desktop.
            var cfg = SettingsService.Instance.Data;
            var previousSystemLock = cfg.LockOnSystemSessionEnd;
            try
            {
                using var pending = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                if (!VaultService.Instance.ApplyUnlock(pending)) throw new InvalidOperationException("System lock setup failed");
                OnUnlocked();
                cfg.LockOnSystemSessionEnd = false;
                NativeMethods.SendMessage(CurrentHandle, NativeMethods.WM_WTSSESSION_CHANGE, NativeMethods.WTS_SESSION_LOCK, 0);
                if (!VaultService.Instance.IsUnlocked) throw new InvalidOperationException("Disabled Windows lock changed the session");
                cfg.LockOnSystemSessionEnd = true;
                NativeMethods.SendMessage(CurrentHandle, NativeMethods.WM_WTSSESSION_CHANGE, 8, 0); // WTS_SESSION_UNLOCK
                if (!VaultService.Instance.IsUnlocked) throw new InvalidOperationException("Unlock notification ended the session");
                NativeMethods.SendMessage(CurrentHandle, NativeMethods.WM_WTSSESSION_CHANGE, NativeMethods.WTS_SESSION_LOCK, 0);
                if (VaultService.Instance.IsUnlocked || NavFrame.Content is not LockPage)
                    throw new InvalidOperationException("Windows lock did not close the workspace");
                using var verifying = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
                NativeMethods.SendMessage(CurrentHandle, NativeMethods.WM_POWERBROADCAST, NativeMethods.PBT_APMSUSPEND, 0);
                if (VaultService.Instance.ApplyUnlock(verifying)) throw new InvalidOperationException("Suspend did not revoke pending verification");
                SmokeTest.Report("privacy: native session lock, disabled option, unlock notification and suspend revocation passed; WTS registration=" + BossKeyService.Instance.SessionNotificationsRegistered);
            }
            finally { cfg.LockOnSystemSessionEnd = previousSystemLock; }

            using var unlock = await VaultService.Instance.VerifyPasswordAsync("SmokeTest#123");
            SmokeTest.Report("ui-smoke: password re-verified");
            bool applied = VaultService.Instance.ApplyUnlock(unlock);
            SmokeTest.Report("ui-smoke: unlock applied=" + applied);
            OnUnlocked();
            await Task.Delay(600);
            SmokeTest.Report("ui-smoke: navigated after re-unlock");
            bool clearedTabs = cachedBrowser.SmokeSessionIsEmpty;
            SmokeTest.Report("browser: locking from another page clears cached tabs + reopen history=" + clearedTabs);
            if (!clearedTabs) throw new InvalidOperationException("Cached browser retained locked state");

            // Leave a live native WebView for the direct Window.Close regression.
            NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
            var closingBrowser = (BrowserPage)NavFrame.Content;
            // Unlock may finish while the old browser profile is still closing.
            // The page selects a fresh tab once that cleanup has completed.
            for (int i = 0; i < 150 && closingBrowser.SmokeSelectedTab == null; i++) await Task.Delay(100);
            var closingTab = closingBrowser.SmokeSelectedTab;
            if (closingTab == null || !await closingTab.RunSmokeNavigationAsync())
                throw new InvalidOperationException("Closing fixture could not initialize WebView");
            SmokeTest.Report("closing fixture: live WebView before direct Window.Close");

            SmokeTest.Report("ui-smoke complete; closing window");
        }
        catch (Exception ex)
        {
            AppLog.Error("UI 冒烟页面走查失败", ex);
            SmokeTest.Report("ui-smoke failed: " + ex.GetType().Name + " | " + ex.Message);
        }
        finally
        {
            // Exercise the production direct-close fallback, without test-only pre-cleanup.
            try { Close(); } catch { }
        }
    }

    private void ApplySettingsOnStartup()
    {
        try
        {
            var cfg = SettingsService.Instance.Data;

            RootGrid.RequestedTheme = cfg.AppTheme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };

            NavView.PaneDisplayMode = cfg.IsCompactNav ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Auto;

            RefreshStatusBadge();
        }
        catch (Exception ex)
        {
            AppLog.Error("应用启动设置失败", ex);
        }
    }

    private async Task StartupMaintenanceAsync()
    {
        // Stale browser caches can contain thousands of files. Never delete them
        // synchronously before the first window paints, or touch real profiles
        // from an isolated diagnostic process.
        if (!SmokeTest.Enabled)
            await Task.Run(BrowserProfileManager.SweepStaleProfiles);
        try
        {
            await Task.Run(() => VaultService.Instance.SweepOrphanedMedia());
        }
        catch (Exception ex)
        {
            AppLog.Error("启动清理保险箱残留失败", ex);
        }

        try
        {
            if (!SettingsService.Instance.Data.AutoCheckThumbcacheOnStartup) return;

            int residue = await Task.Run(CountThumbcacheFiles);
            if (residue <= 0) return;

            _pendingResidueCount = residue;
            RefreshStatusBadge();
        }
        catch (Exception ex)
        {
            AppLog.Error("启动缩略图扫描失败", ex);
        }
    }

    private static int CountThumbcacheFiles()
    {
        try
        {
            string explorerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Explorer");
            if (!Directory.Exists(explorerDir)) return 0;
            return Directory.GetFiles(explorerDir, "thumbcache_*.db").Length;
        }
        catch
        {
            return 0;
        }
    }

    private void RefreshStatusBadge()
    {
        if (_shutdownStarted || _windowClosed) return;
        var work = VaultWork.Current;
        bool busy = work != null && !_isLocked;
        StatusIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusProgress.IsActive = busy;
        StatusBadge.IsEnabled = !_pageNavigationBlocked && !busy;

        (string label, string hint) = ("就绪", "打开痕迹清理");
        if (busy && work != null)
        {
            string taskName = SettingsService.Instance.Data.CamouflageIndex > 0 ? "处理中" : work.Label;
            label = work.Token.IsCancellationRequested ? "任务正在结束"
                : work.Percent is int percent ? $"{taskName} {percent}%" : taskName;
            hint = label;
        }
        else if (SettingsService.Instance.ReadDeferred)
            (label, hint) = ("设置暂时只读", "设置文件暂不可访问。解除占用后重启，即可读取并保存最新设置。");
        else if (_pendingResidueCount > 0)
            (label, hint) = ("发现系统残留", $"检测到 {_pendingResidueCount} 个资源管理器缩略图缓存文件，点击前往清理。");
        else if (!BossKeyService.Instance.IsBossKeyEnabled && SettingsService.Instance.Data.BossKeyEnabled)
            (label, hint) = ("老板键未注册", BossKeyService.Instance.LastBossKeyError);
        else if (SettingsService.Instance.LoadFailed)
            (label, hint) = ("设置未能读取", "本次启动使用默认配置。");
        StatusBadgeText.Text = label;
        ToolTipService.SetToolTip(StatusBadge, hint);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBadge, busy ? label : $"{label}，打开痕迹清理");
    }

    private void StatusBadge_Tapped(object sender, RoutedEventArgs e)
    {
        if (_isLocked) return;
        NavigateToPage(typeof(CleanerPage), null, "cleaner", force: true);
    }

    #endregion

    #region Idle / minimise lock

    private void SetupIdleDetection()
    {
        _idleTimer.Tick += OnIdleTick;
    }

    internal void RefreshIdleDeadline() => ScheduleIdleCheck(false);

    private void OnIdleTick(object? sender, object e) => ScheduleIdleCheck(true);

    private void ScheduleIdleCheck(bool lockIfDue)
    {
        _idleTimer.Stop();
        int minutes = SettingsService.Instance.Data.AutoLockMinutes;
        // The fixture has its own inputs. Production uses the session's actual
        // Windows input timestamp, so activity in other applications counts.
        if (SmokeTest.UiSmoke || _isLocked || _shutdownStarted || minutes <= 0 || !VaultService.Instance.IsUnlocked) return;
        long delayMs = 5_000;
        try
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
            };

            if (NativeMethods.GetLastInputInfo(ref info))
            {
                delayMs = MillisecondsUntilIdleLock(minutes, unchecked((uint)Environment.TickCount), info.dwTime);
                if (lockIfDue && delayMs == 0)
                {
                    LockWorkspace();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("闲置锁定检测失败", ex);
        }
        _idleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _idleTimer.Start();
    }

    internal static long MillisecondsUntilIdleLock(int minutes, uint now, uint lastInput) =>
        Math.Max(0L, minutes * 60_000L - unchecked(now - lastInput));

    private void OnWindowMinimized()
    {
        // LockWorkspace marshals to the UI thread and also revokes in-flight setup
        // or verification while the lock page is already visible.
        if (SettingsService.Instance.Data.LockOnMinimize) LockWorkspace();
    }

    private void OnSystemSessionLeaving()
    {
        if (SettingsService.Instance.Data.LockOnSystemSessionEnd) LockWorkspace();
    }

    private void OnWindowVisibilityChanged(bool visible) => DispatcherQueue.TryEnqueue(() =>
    {
        Controls.BrowserTabControl.SetHostWindowVisible(visible);
        if (visible) RestoreBossKeyMuteIfSettled();
    });

    #endregion

    #region Boss key

    private void QuickBossKey_Click(object sender, RoutedEventArgs e)
    {
        BossKeyService.Instance.TriggerBossKey();
    }

    private void OnBossKeyPressed()
    {
        DispatcherQueue.TryEnqueue(HandleBossKey);
    }

    private void HandleBossKey()
    {
        try
        {
            if (_camouflageVisible)
            {
                HideCamouflage();
                return;
            }

            bool minimized = BossKeyService.Instance.IsWindowMinimized
                             || (CurrentHandle != IntPtr.Zero && !NativeMethods.IsWindowVisible(CurrentHandle));

            if (minimized)
            {
                RestoreFromBossKey();
                return;
            }

            var cfg = SettingsService.Instance.Data;
            if (cfg.MuteOnBossKey)
            {
                PrivacyMediaGuard.MuteForBossKey();
                _bossKeyMuted = true;
            }

            switch (cfg.BossKeyAction)
            {
                case 1: // lock + minimise (no tray icon: taskbar restore is the recovery path)
                    LockWorkspace();
                    BossKeyService.Instance.MinimizeWindow();
                    break;

                case 2:
                    ShowCamouflage();
                    break;

                default:
                    PrepareWorkspaceForPrivacy();
                    BossKeyService.Instance.MinimizeWindow();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("老板键动作执行失败", ex);
        }
    }

    private void RestoreFromBossKey()
    {
        BossKeyService.Instance.RestoreWindow();

        if (_bossKeyMuted)
        {
            _bossKeyMuted = false;
            PrivacyMediaGuard.RestoreAfterBossKey();
        }
    }

    /// <summary>
    /// If the window was restored through the taskbar (not via the boss key), the
    /// boss-key mute would otherwise stay active indefinitely. Check after the
    /// native show/restore message. Camouflage keeps the mute until dismissed.
    /// </summary>
    private void RestoreBossKeyMuteIfSettled()
    {
        if (!_bossKeyMuted || _camouflageVisible) return;
        if (BossKeyService.Instance.IsWindowMinimized) return;
        if (CurrentHandle != IntPtr.Zero && !NativeMethods.IsWindowVisible(CurrentHandle)) return;

        _bossKeyMuted = false;
        PrivacyMediaGuard.RestoreAfterBossKey();
    }

    public void BringToForeground()
    {
        try
        {
            if (BossKeyService.Instance.IsWindowMinimized
                || (CurrentHandle != IntPtr.Zero && !NativeMethods.IsWindowVisible(CurrentHandle)))
            {
                BossKeyService.Instance.RestoreWindow();
            }
            Activate();
            if (CurrentHandle != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(CurrentHandle);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("激活窗口失败: " + ex.GetType().Name);
        }
    }

    #endregion

    #region Camouflage

    public void ShowCamouflage()
    {
        if (_camouflageVisible) return;
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is UIElement focused)
            _focusBeforeCamouflage = new WeakReference<UIElement>(focused);
        CamouflageOverlay.Visibility = Visibility.Visible;
        CamouflageProgress.IsActive = UiMotion.AnimationsEnabled;
        _camouflageVisible = true;
        _restoreAppAudioAfterCover = !PrivacyMediaGuard.IsAppAudioMuted;
        if (_restoreAppAudioAfterCover) PrivacyMediaGuard.SetAppAudioMuted(true);
        PrepareWorkspaceForPrivacy();
        WorkspaceContent.Visibility = Visibility.Collapsed;
        Controls.BrowserTabControl.SetPrivacyCoverActive(true);
        CamouflageOverlay.Focus(FocusState.Programmatic);
    }

    private void PrepareWorkspaceForPrivacy()
    {
        (NavFrame.Content as CleanerPage)?.CancelOperation();
        // Menus and dialogs live in the window's popup layer above the frame.
        // Close them in this turn as well as replacing the page beneath them.
        try
        {
            WorkspaceDialogs.DismissForPrivacy();
            if (RootGrid.XamlRoot is not { } root) return;
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.IsLightDismissEnabled) popup.IsOpen = false;
        }
        catch (Exception ex) { AppLog.Warn("关闭临时面板失败: " + ex.GetType().Name); }
    }

    public void HideCamouflage()
    {
        bool wasVisible = _camouflageVisible;
        CamouflageOverlay.Visibility = Visibility.Collapsed;
        CamouflageProgress.IsActive = false;
        WorkspaceContent.Visibility = Visibility.Visible;
        _camouflageVisible = false;
        if (wasVisible) Controls.BrowserTabControl.SetPrivacyCoverActive(false);
        if (wasVisible && !_isLocked && !_shutdownStarted &&
            _focusBeforeCamouflage?.TryGetTarget(out var previous) == true && previous.XamlRoot == RootGrid.XamlRoot)
            previous.Focus(FocusState.Programmatic);
        _focusBeforeCamouflage = null;
        if (wasVisible && _restoreAppAudioAfterCover)
        {
            _restoreAppAudioAfterCover = false;
            PrivacyMediaGuard.SetAppAudioMuted(false);
        }

        if (wasVisible && _bossKeyMuted && !BossKeyService.Instance.IsWindowMinimized)
        {
            _bossKeyMuted = false;
            PrivacyMediaGuard.RestoreAfterBossKey();
        }
    }

    private void CamouflageOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideCamouflage();
            e.Handled = true;
        }
    }

    internal static string GetCamouflageTitle(int index) => index switch
    {
        1 => "Windows 性能与系统诊断工具",
        2 => "Markdown 知识库与本地笔记",
        3 => "Terminal Developer Console",
        _ => "Stealth"
    };

    #endregion

    #region Lock / unlock

    public void LockWorkspace()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(LockWorkspace);
            return;
        }

        if (_locking) return;
        _locking = true;
        try
        {
            bool firstLock = !_isLocked;
            bool endingUnlockedSession = firstLock && VaultService.Instance.IsUnlocked;
            _isLocked = true;
            _idleTimer.Stop();
            SettingsService.Instance.SetBookmarkWorkspace(null);

            // Every explicit lock must revoke pending verification/setup, including
            // another lock request while the lock page is already visible.
            PrepareWorkspaceForPrivacy();
            VaultService.Instance.Lock();

            if (firstLock)
            {
                // Isolated diagnostics must not clear the user's global clipboard.
                if (endingUnlockedSession && !SmokeTest.Enabled && SettingsService.Instance.Data.ClearClipboardOnLock)
                {
                    try { Windows.ApplicationModel.DataTransfer.Clipboard.Clear(); } catch { }
                }

                // Guarantees WebView2s are destroyed even if the browser page is not the
                // currently navigated page.
                _ = Controls.BrowserTabControl.ShutdownAllAsync();
            }

            HideCamouflage();
            ExitFullScreenIfNeeded();

            _suppressNavSelection = true;
            try { NavView.SelectedItem = null; } catch { }
            finally { _suppressNavSelection = false; }

            NavView.IsPaneOpen = false;
            NavView.IsPaneVisible = false;
            AppTitleBar.IsPaneToggleButtonVisible = false;
            StatusBadge.Visibility = Visibility.Collapsed;

            if (NavFrame.CurrentSourcePageType != typeof(LockPage))
            {
                if (NavFrame.Content is Page leavingPage) SetPageNavigationBlocked(leavingPage, false);
                NavFrame.Navigate(typeof(LockPage), null, new SuppressNavigationTransitionInfo());
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("锁定工作台失败", ex);
        }
        finally
        {
            _locking = false;
        }
    }

    public void OnUnlocked()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnUnlocked);
            return;
        }

        if (!VaultService.Instance.IsUnlocked) return;
        SettingsService.Instance.SetBookmarkWorkspace(VaultService.Instance.IsDecoyMode);
        _isLocked = false;
        RefreshIdleDeadline();
        NavView.IsPaneVisible = true;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        StatusBadge.Visibility = Visibility.Visible;
        RefreshStatusBadge();

        string startPage = SettingsService.Instance.Data.DefaultStartPage;
        switch (startPage)
        {
            case "browser":
                NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
                break;
            case "cleaner":
                NavigateToPage(typeof(CleanerPage), null, "cleaner", force: true);
                break;
            case "player":
                NavigateToPage(typeof(PlayerPage), null, "player", force: true);
                break;
            default:
                NavigateToVault();
                break;
        }
    }

    #endregion

    #region Navigation

    internal void SetPageNavigationBlocked(Page owner, bool blocked)
    {
        if (!ReferenceEquals(owner, NavFrame.Content) || _pageNavigationBlocked == blocked) return;
        _pageNavigationBlocked = blocked;
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>()) item.IsEnabled = !blocked;
        if (NavView.SettingsItem is NavigationViewItem settings) settings.IsEnabled = !blocked;
        RefreshStatusBadge();
    }

    private void SelectMenuItem(string tag)
    {
        foreach (var menuItem in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(menuItem.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(NavView.SelectedItem, menuItem))
                {
                    _suppressNavSelection = true;
                    try { NavView.SelectedItem = menuItem; }
                    finally { _suppressNavSelection = false; }
                }
                return;
            }
        }
    }

    private void NavigateToPage(Type pageType, object? parameter, string? tag, bool force = false)
    {
        if (_isLocked && pageType != typeof(LockPage))
        {
            LockWorkspace();
            return;
        }

        if (_pageNavigationBlocked) return;

        try
        {
            if (tag != null) SelectMenuItem(tag);

            bool samePage = NavFrame.CurrentSourcePageType == pageType;
            if (!force && samePage && parameter == null) return;

            // Only the incoming content moves. A frame-level outgoing transition
            // can keep an old page (including a WebView or media surface) on screen.
            _navigationCue = parameter is MediaOpenRequest ? MotionCue.FromRight
                : pageType == typeof(VaultPage) && NavFrame.CurrentSourcePageType == typeof(PlayerPage) ? MotionCue.FromLeft
                : MotionCue.Page;
            NavFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());

            if (pageType == typeof(CleanerPage))
            {
                _pendingResidueCount = 0;
                RefreshStatusBadge();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"导航到 {pageType.Name} 失败", ex);
        }
    }

    public void NavigateToPlayer(VaultItem item)
    {
        if (item == null) return;
        var gallery = NavFrame.Content as VaultPage;
        gallery?.FlushPendingSearch();
        var items = (gallery?.DisplayedItems ?? VaultService.Instance.VisibleItems)
            .Where(media => media.MediaType == item.MediaType).DistinctBy(media => media.Id).ToList();
        if (items.All(media => media.Id != item.Id)) items.Add(item);
        NavigateToPage(typeof(PlayerPage), new MediaOpenRequest(item, items, gallery?.CaptureViewState()), "player", force: true);
    }

    public void NavigateToVault()
    {
        NavigateToPage(typeof(VaultPage), null, "vault", force: true);
    }

    internal void ReturnToVault(VaultViewState? state) => NavigateToPage(typeof(VaultPage), state, "vault", force: true);

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection || _isLocked) return;

        if (args.IsSettingsSelected)
        {
            NavigateToPage(typeof(SettingsPage), null, null, force: true);
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "vault":
                    NavigateToPage(typeof(VaultPage), null, null);
                    break;
                case "player":
                    NavigateToPage(typeof(PlayerPage), null, null, force: true);
                    break;
                case "browser":
                    NavigateToPage(typeof(BrowserPage), null, null);
                    break;
                case "cleaner":
                    NavigateToPage(typeof(CleanerPage), null, null);
                    _pendingResidueCount = 0;
                    break;
            }
        }
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // Navigation is explicit (sidebar and viewer return state), never Frame.GoBack.
        // Do not retain old media queues and plaintext names in the frame journal.
        NavFrame.BackStack.Clear();
        NavFrame.ForwardStack.Clear();
        if (e.Content is FrameworkElement page && page is not LockPage)
        {
            var cue = _navigationCue;
            void RevealPage()
            {
                if (_isLocked || _camouflageVisible || !ReferenceEquals(page, NavFrame.Content)) return;
                UIElement target = page switch
                {
                    BrowserPage browser => browser.NavigationChrome,
                    PlayerPage player => player.NavigationChrome,
                    _ => page
                };
                UiMotion.Reveal(target, cue);
            }
            void OnLoaded(object _, RoutedEventArgs __) { page.Loaded -= OnLoaded; RevealPage(); }
            if (page.IsLoaded) RevealPage();
            else page.Loaded += OnLoaded;
        }
        bool isBrowser = e.SourcePageType == typeof(BrowserPage);
        if (isBrowser != _browserNavigationLayout)
        {
            _browserNavigationLayout = isBrowser;
            if (isBrowser)
            {
                _paneOpenBeforeBrowser = NavView.IsPaneOpen;
                NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
                NavView.IsPaneOpen = false;
            }
            else
            {
                NavView.PaneDisplayMode = SettingsService.Instance.Data.IsCompactNav
                    ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Auto;
                NavView.IsPaneOpen = !_isLocked && _paneOpenBeforeBrowser;
            }
        }

        if (AppTitleBar != null)
        {
            AppTitleBar.IsBackButtonVisible = false;
        }

        if (SmokeTest.Enabled)
        {
            SmokeTest.Report("page-visited: " + (e.SourcePageType?.Name ?? "unknown"));
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        if (!_isLocked) NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        // Deliberately no-op: navigating back could reveal a locked page.
    }

    private void QuickLockBtn_Click(object sender, RoutedEventArgs e)
    {
        LockWorkspace();
    }

    #endregion

    #region Window chrome

    public void UpdateTitle(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        if (SmokeTest.Enabled) newTitle += " · 测试窗口";
        Title = newTitle;
        if (AppTitleBar != null)
        {
            AppTitleBar.Title = newTitle;
            if (VaultWork.IsBusy) RefreshStatusBadge();
        }
    }

    public void SetFullScreenMode(bool enable)
    {
        try
        {
            if (enable)
            {
                if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;
                _paneVisibleBeforeFullScreen = NavView.IsPaneVisible;
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                TitleBarRow.Height = new GridLength(0);
                TitleBarHost.Visibility = Visibility.Collapsed;
                NavView.IsPaneVisible = false;
            }
            else
            {
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) return;
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                TitleBarRow.Height = new GridLength(44);
                TitleBarHost.Visibility = Visibility.Visible;
                NavView.IsPaneVisible = !_isLocked && _paneVisibleBeforeFullScreen;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("切换全屏失败: " + ex.GetType().Name);
        }
    }

    private void ExitFullScreenIfNeeded()
    {
        try
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                SetFullScreenMode(false);
            }
        }
        catch
        {
            // ignored
        }
    }

    #endregion

    #region Shutdown

    private Task? _shutdownTask;
    private bool _windowClosed;

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        await ShutdownWorkspaceAsync();
        if (!_windowClosed) { try { Close(); } catch { } }
    }

    private Task ShutdownWorkspaceAsync() => _shutdownTask ??= RunShutdownAsync();

    private async Task RunShutdownAsync()
    {
        SmokeTest.Report("closing: 清理流程开始");
        try
        {
            try { _idleTimer.Stop(); } catch { }
            PrepareWorkspaceForPrivacy();
            try { BossKeyService.Instance.Uninitialize(); } catch { }
            // Revoke keys immediately, before the first asynchronous browser operation.
            try { VaultService.Instance.Lock(); } catch { }
            SmokeTest.Report("closing: vault locked=" + !VaultService.Instance.IsUnlocked);
            Task? finishingVaultWork = VaultWork.Current?.Completion;
            await CleanerService.Instance.WaitForCleaningToFinishAsync();
            try
            {
                await Controls.BrowserTabControl.ShutdownAllAsync().WaitAsync(TimeSpan.FromSeconds(6));
                SmokeTest.Report("closing: browser controls released");
            }
            catch (Exception ex) { AppLog.Warn("关闭浏览器标签超时/失败: " + ex.GetType().Name); }
            // A browser import may itself be waiting for the WebView that was just closed.
            if (finishingVaultWork != null) await finishingVaultWork;
            try
            {
                bool cleaned = await BrowserProfileManager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(8));
                SmokeTest.Report("closing: browser profiles removed=" + cleaned);
            }
            catch (Exception ex)
            {
                AppLog.Warn("关闭浏览器沙箱超时/失败: " + ex.GetType().Name);
                SmokeTest.Report("closing: browser profiles removed=False, " + ex.GetType().Name);
            }
            try
            {
                if (!SmokeTest.Enabled && SettingsService.Instance.Data.AutoFlushDnsOnExit)
                    NativeMethods.DnsFlushResolverCache();
            }
            catch { }
            try { PrivacyMediaGuard.RestoreAfterBossKey(); } catch { }
            try { SettingsService.Instance.SaveSettings(); } catch { }
        }
        catch (Exception ex) { AppLog.Error("关闭清理流程失败", ex); }
        finally { _shutdownComplete = true; }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        // Direct Window.Close bypasses AppWindow.Closing. Keep the UI dispatcher alive
        // so pending WebView initialization/disposal can finish instead of blocking it.
        _windowClosed = true;
        VaultWork.Changed -= RefreshStatusBadge;
        SmokeTest.Report("Window Closed 事件已触发");
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        try { await ShutdownWorkspaceAsync(); }
        finally
        {
            Current = null;
            App.MainWindowInstance = null;
            Application.Current.Exit();
        }
    }

    #endregion
}
