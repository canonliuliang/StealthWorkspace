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
    private bool _camouflageVisible;
    private bool _bossKeyMuted;
    private bool _paneVisibleBeforeFullScreen = true;
    private bool _browserNavigationLayout;
    private bool _paneOpenBeforeBrowser;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private int _pendingResidueCount;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        if (SmokeTest.Enabled) UpdateTitle("Stealth");

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
            int width = Math.Min(work.Width - 48, (int)(1100 * dpi));
            int height = Math.Min(work.Height - 48, (int)(740 * dpi));
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
        LockWorkspace();
        _ = StartupMaintenanceAsync();

        SmokeTest.Report($"UI ready: page={NavFrame.CurrentSourcePageType?.Name ?? "null"}, title={Title}, " +
                         $"paneVisible={NavView.IsPaneVisible}, lockPageVisible={NavView.IsPaneVisible == false}");

        if (SmokeTest.Enabled)
        {
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
                VaultService.Instance.ApplyUnlock(await Task.Run(() => VaultService.Instance.VerifyPassword("SmokeTest#123")));
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
                    var existing = await Task.Run(() => VaultService.Instance.VerifyPassword("SmokeTest#123"));
                    VaultService.Instance.ApplyUnlock(existing);
                }
                else
                {
                    await VaultService.Instance.InitializeVaultAsync("SmokeTest#123");
                }
            }
            SmokeTest.Report($"ui-smoke: unlocked={VaultService.Instance.IsUnlocked}, vault={VaultService.Instance.HasVault}");
            OnUnlocked();
            await Task.Delay(500);

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
                    () => NavFrame.Content as PlayerPage, () => NavFrame.Content as VaultPage);
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
                var result = await vaultPage.ImportFilesAsync(new[] { fixture });
                if (result.Imported != 1 || result.Failed != 0 || !File.Exists(fixture))
                    throw new InvalidOperationException("媒体导入回归检查未通过。");
                SmokeTest.Report("vault: image imported and original preserved");
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

            NavigateToPage(typeof(PlayerPage), null, "player", force: true);
            await Task.Delay(600);

            LockWorkspace();
            await Task.Delay(600);
            SmokeTest.Report($"ui-smoke: locked={!VaultService.Instance.IsUnlocked}");

            var unlock = await Task.Run(() => VaultService.Instance.VerifyPassword("SmokeTest#123"));
            SmokeTest.Report("ui-smoke: password re-verified");
            bool applied = VaultService.Instance.ApplyUnlock(unlock);
            SmokeTest.Report("ui-smoke: unlock applied=" + applied);
            OnUnlocked();
            await Task.Delay(600);
            SmokeTest.Report("ui-smoke: navigated after re-unlock");
            bool clearedTabs = cachedBrowser.SmokeSessionIsEmpty;
            SmokeTest.Report("browser: locking from another page clears cached tabs + reopen history=" + clearedTabs);
            if (!clearedTabs) throw new InvalidOperationException("Cached browser retained locked state");

            SmokeTest.Report("ui-smoke complete; closing window");
        }
        catch (Exception ex)
        {
            AppLog.Error("UI 冒烟页面走查失败", ex);
            SmokeTest.Report("ui-smoke failed: " + ex.GetType().Name + " | " + ex.Message);
        }
        finally
        {
            // Programmatic Close can skip AppWindow.Closing. Release native tabs
            // while the UI dispatcher is alive, including failed/probe runs.
            await Controls.BrowserTabControl.ShutdownAllAsync();
            await BrowserProfileManager.ShutdownAsync();
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

            if (cfg.CamouflageIndex > 0)
            {
                UpdateTitle(GetCamouflageTitle(cfg.CamouflageIndex));
            }

            if (!BossKeyService.Instance.IsBossKeyEnabled && cfg.BossKeyEnabled)
            {
                StatusBadgeText.Text = "老板键未注册";
                ToolTipService.SetToolTip(StatusBadge, BossKeyService.Instance.LastBossKeyError);
            }
            else if (SettingsService.Instance.LoadFailed)
            {
                StatusBadgeText.Text = "设置已重置";
                ToolTipService.SetToolTip(StatusBadge, "设置文件无法读取，已使用默认配置；原始文件已备份。");
            }
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
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isLocked) return;
                StatusBadgeText.Text = "发现系统残留";
                ToolTipService.SetToolTip(StatusBadge, $"检测到 {residue} 个资源管理器缩略图缓存文件，点击前往清理。");
            });
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

    private void StatusBadge_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isLocked || _pendingResidueCount <= 0) return;
        NavigateToPage(typeof(CleanerPage), null, "cleaner", force: true);
    }

    #endregion

    #region Idle / minimise lock

    private void SetupIdleDetection()
    {
        // The isolated smoke walk explicitly exercises locking; the desktop's
        // pre-existing idle time must not interrupt it mid-page.
        if (SmokeTest.UiSmoke) return;
        _idleTimer.Interval = TimeSpan.FromSeconds(5);
        _idleTimer.Tick += OnIdleTick;
        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, object e)
    {
        try
        {
            RestoreBossKeyMuteIfSettled();

            if (!VaultService.Instance.IsUnlocked) return;

            int minutes = SettingsService.Instance.Data.AutoLockMinutes;
            if (minutes <= 0) return;

            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
            };

            if (!NativeMethods.GetLastInputInfo(ref info)) return;

            // Unchecked uint subtraction handles the 49.7 day TickCount wrap correctly.
            uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
            long thresholdMs = Math.Max(60_000L, minutes * 60_000L);
            if (elapsedMs >= thresholdMs)
            {
                LockWorkspace();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("闲置锁定检测失败", ex);
        }
    }

    private void OnWindowMinimized()
    {
        if (!SettingsService.Instance.Data.LockOnMinimize) return;
        if (!VaultService.Instance.IsUnlocked) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsService.Instance.Data.LockOnMinimize && VaultService.Instance.IsUnlocked)
            {
                LockWorkspace();
            }
        });
    }

    private void OnWindowVisibilityChanged(bool visible) => DispatcherQueue.TryEnqueue(() =>
        Controls.BrowserTabControl.SetHostWindowVisible(visible));

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
    /// boss-key mute would otherwise stay active indefinitely. The 5s idle timer is a
    /// cheap place to heal that state. Camouflage mode keeps the mute until dismissed.
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
        CamouflageOverlay.Visibility = Visibility.Visible;
        _camouflageVisible = true;
        CamouflageOverlay.Focus(FocusState.Programmatic);
    }

    public void HideCamouflage()
    {
        bool wasVisible = _camouflageVisible;
        CamouflageOverlay.Visibility = Visibility.Collapsed;
        _camouflageVisible = false;

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

    private static string GetCamouflageTitle(int index) => index switch
    {
        1 => "Windows 性能与系统诊断工具",
        2 => "Markdown 知识库与本地笔记",
        3 => "Terminal Developer Console",
        _ => "StealthWorkspace 隐私控制台"
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
            _isLocked = true;

            if (firstLock)
            {
                // Drops the decryption key and raises Locked so players/browser tabs can
                // stop immediately even before the page navigation completes.
                VaultService.Instance.Lock();

                if (SettingsService.Instance.Data.ClearClipboardOnLock)
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
            StatusBadge.Visibility = Visibility.Collapsed;

            if (NavFrame.CurrentSourcePageType != typeof(LockPage))
            {
                NavFrame.Navigate(typeof(LockPage));
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

        _isLocked = false;
        NavView.IsPaneVisible = true;
        StatusBadge.Visibility = Visibility.Visible;

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

        try
        {
            if (tag != null) SelectMenuItem(tag);

            bool samePage = NavFrame.CurrentSourcePageType == pageType;
            if (!force && samePage && parameter == null) return;

            NavFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());

            if (pageType == typeof(CleanerPage)) _pendingResidueCount = 0;
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
        var images = (gallery?.DisplayedItems ?? VaultService.Instance.VisibleItems)
            .Where(media => media.MediaType == "Image").DistinctBy(media => media.Id).ToList();
        if (item.MediaType == "Image" && images.All(media => media.Id != item.Id)) images.Add(item);
        NavigateToPage(typeof(PlayerPage), new MediaOpenRequest(item, images, gallery?.CaptureViewState()), "player", force: true);
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
        // WebView is a native surface; animate its chrome when selected instead.
        // Locking always replaces private content immediately.
        if (e.Content is FrameworkElement page && page is not BrowserPage && page is not LockPage)
        {
            void Animate(object _, RoutedEventArgs __) { page.Loaded -= Animate; UiMotion.Enter(page); }
            if (page.IsLoaded) UiMotion.Enter(page);
            else page.Loaded += Animate;
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
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
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
                if (AppTitleBar != null) AppTitleBar.Visibility = Visibility.Collapsed;
                NavView.IsPaneVisible = false;
            }
            else
            {
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) return;
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                TitleBarRow.Height = new GridLength(44);
                if (AppTitleBar != null) AppTitleBar.Visibility = Visibility.Visible;
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

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;

        args.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        SmokeTest.Report("closing: 清理流程开始");

        try
        {
            _idleTimer.Stop();

            try { HideCamouflage(); } catch { }
            try { BossKeyService.Instance.Uninitialize(); } catch { }
            try { VaultService.Instance.Lock(); } catch { }

            try
            {
                await Controls.BrowserTabControl.ShutdownAllAsync().WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                AppLog.Warn("关闭浏览器标签超时/失败: " + ex.GetType().Name);
            }

            try
            {
                await BrowserProfileManager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                AppLog.Warn("关闭浏览器沙箱超时/失败: " + ex.GetType().Name);
            }

            try
            {
                if (!SmokeTest.Enabled && SettingsService.Instance.Data.AutoFlushDnsOnExit)
                {
                    NativeMethods.DnsFlushResolverCache();
                }
            }
            catch { }

            try { PrivacyMediaGuard.RestoreAfterBossKey(); } catch { }
            try { SettingsService.Instance.SaveSettings(); } catch { }
        }
        catch (Exception ex)
        {
            AppLog.Error("关闭清理流程失败", ex);
        }
        finally
        {
            _shutdownComplete = true;
            try { Close(); } catch { }
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        SmokeTest.Report("Window Closed 事件已触发");
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;

        // Window.Close() does not always raise AppWindow.Closing, so the WebView2 profile
        // cleanup must also be guaranteed here or a hidden %TEMP%/data-root profile would
        // survive the process. ShutdownAsync never throws and uses ConfigureAwait(false),
        // so a bounded synchronous wait on the UI thread cannot deadlock.
        try
        {
            BrowserProfileManager.ShutdownAsync().Wait(TimeSpan.FromSeconds(6));
        }
        catch { }

        try
        {
            if (!SmokeTest.Enabled && !_shutdownComplete && SettingsService.Instance.Data.AutoFlushDnsOnExit)
            {
                NativeMethods.DnsFlushResolverCache();
            }
        }
        catch { }

        try { _idleTimer.Stop(); } catch { }
        try { BossKeyService.Instance.Uninitialize(); } catch { }
        try { VaultService.Instance.Lock(); } catch { }
        Current = null;
        App.MainWindowInstance = null;
    }

    #endregion
}
