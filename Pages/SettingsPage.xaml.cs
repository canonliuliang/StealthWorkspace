using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isLoaded;
    private VaultWork? _work;
    private bool _picking;

    public SettingsPage()
    {
        InitializeComponent();
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) =>
        {
            SettingsSearch.StartBringIntoView();
            SettingsSearch.Focus(FocusState.Keyboard);
            SettingsSearch.SelectAll();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(find);
        Loaded += SettingsPage_Loaded;
        Unloaded += (_, _) =>
        {
            VaultWork.Changed -= UpdateActions;
            _isLoaded = false;
            _bookmarkTransfer?.Cancel();
        };
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        RuntimeArchitectureText.Text = $"运行架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version.Major}";
        var cfg = SettingsService.Instance.Data;

        // Display protection: reflect the *actual* Win32 state, not just user intent.
        AffinityToggle.IsOn = BossKeyService.Instance.IsDisplayAffinityProtected;
        TaskbarThumbnailToggle.IsOn = BossKeyService.Instance.IsPeekExcluded;

        AutoLockCombo.SelectedIndex = cfg.AutoLockMinutes switch
        {
            5 => 0,
            15 => 1,
            30 => 2,
            _ => 3
        };
        LockOnMinimizeToggle.IsOn = cfg.LockOnMinimize;
        LockOnSystemToggle.IsOn = cfg.LockOnSystemSessionEnd;
        StartPageCombo.SelectedIndex = cfg.DefaultStartPage switch { "vault" => 1, "player" => 2, "cleaner" => 3, _ => 0 };

        ShredPassesCombo.SelectedIndex = cfg.ShredPasses switch
        {
            1 => 0,
            3 => 1,
            7 => 2,
            _ => 1
        };
        ClearClipboardToggle.IsOn = cfg.ClearClipboardOnLock;

        BossKeyToggle.IsOn = BossKeyService.Instance.IsBossKeyEnabled;
        BossKeyActionCombo.SelectedIndex = Math.Clamp(cfg.BossKeyAction, 0, 2);
        BossKeyMuteToggle.IsOn = cfg.MuteOnBossKey;

        SearchEngineCombo.SelectedIndex = cfg.SearchEngine switch
        {
            "Startpage" => 1,
            "Bing" => 2,
            "Google" => 3,
            _ => 0
        };

        AutoCheckThumbToggle.IsOn = cfg.AutoCheckThumbcacheOnStartup;
        AutoFlushDnsToggle.IsOn = cfg.AutoFlushDnsOnExit;

        CamouflageCombo.SelectedIndex = Math.Clamp(cfg.CamouflageIndex, 0, 3);
        ThemeCombo.SelectedIndex = cfg.AppTheme switch
        {
            "Dark" => 1,
            "Light" => 2,
            _ => 0
        };

        RefreshVaultSecuritySection();

        if (!BossKeyService.Instance.IsBossKeyEnabled && cfg.BossKeyEnabled)
        {
            ShowInfo("全局老板键 Ctrl+Alt+B 未能注册，可能已被其他程序占用。", InfoBarSeverity.Warning);
        }

        _isLoaded = true;
        VaultWork.Changed += UpdateActions;
        UpdateActions();
    }

    private void RefreshVaultSecuritySection()
    {
        // In the decoy space the password management UI is hidden: showing "诱饵已设置"
        // would immediately reveal the decoy to anyone who coerced the password.
        bool decoyMode = VaultService.Instance.IsDecoyMode;
        BookmarkTransferSection.Visibility = decoyMode ? Visibility.Collapsed : Visibility.Visible;
        BackupSection.Visibility = decoyMode ? Visibility.Collapsed : Visibility.Visible;
        VaultSecuritySection.Visibility = decoyMode ? Visibility.Collapsed : Visibility.Visible;
        if (!decoyMode)
        {
            bool hasDecoy = VaultService.Instance.HasDecoyVault;
            VaultSecurityStatusText.Text = hasDecoy
                ? "主密码已启用 · 诱饵密码已设置 · 媒体采用 AES-256-GCM 分块加密"
                : "主密码已启用 · 尚未设置诱饵密码 · 媒体采用 AES-256-GCM 分块加密";
            DecoyPwdBtn.Content = hasDecoy ? "修改诱饵密码" : "设置诱饵密码";
            ClearDecoyBtn.IsEnabled = hasDecoy;
        }
        IndexVisibleSettings();
    }

    private void StartPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.DefaultStartPage = StartPageCombo.SelectedIndex switch { 1 => "vault", 2 => "player", 3 => "cleaner", _ => "browser" };
        SavePreferences();
    }

    #region Display protection

    private void AffinityToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        bool requested = AffinityToggle.IsOn;
        bool ok = BossKeyService.Instance.SetDisplayAffinityProtection(requested);
        if (!ok)
        {
            _isLoaded = false;
            AffinityToggle.IsOn = !requested;
            _isLoaded = true;
            ShowInfo("设置防截屏保护失败（当前系统可能不支持 WDA_EXCLUDEFROMCAPTURE）。", InfoBarSeverity.Error);
            return;
        }

        SettingsService.Instance.Data.DisplayAffinityProtected = requested;
        SavePreferences();
    }

    private void TaskbarThumbnailToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        bool requested = TaskbarThumbnailToggle.IsOn;
        bool ok = BossKeyService.Instance.SetTaskbarPeekProtection(requested);
        if (!ok)
        {
            _isLoaded = false;
            TaskbarThumbnailToggle.IsOn = !requested;
            _isLoaded = true;
            ShowInfo("设置任务栏预览防窥失败（当前系统可能不支持该 DWM 属性）。", InfoBarSeverity.Error);
            return;
        }

        SettingsService.Instance.Data.TaskbarPeekExcluded = requested;
        SavePreferences();
    }

    #endregion

    #region Vault / auto lock

    private void AutoLockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.AutoLockMinutes = AutoLockCombo.SelectedIndex switch
        {
            0 => 5,
            1 => 15,
            2 => 30,
            _ => 0
        };
        MainWindow.Current?.RefreshIdleDeadline();
        SavePreferences();
    }

    private void LockOnMinimizeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.LockOnMinimize = LockOnMinimizeToggle.IsOn;
        SavePreferences();
    }

    private void ShredPassesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.ShredPasses = ShredPassesCombo.SelectedIndex switch
        {
            0 => 1,
            1 => 3,
            2 => 7,
            _ => 3
        };
        SavePreferences();
    }

    private void LockOnSystemToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        var settings = SettingsService.Instance;
        bool previous = settings.Data.LockOnSystemSessionEnd;
        settings.Data.LockOnSystemSessionEnd = LockOnSystemToggle.IsOn;
        if (!settings.SaveSettings())
        {
            settings.Data.LockOnSystemSessionEnd = previous;
            _isLoaded = false;
            LockOnSystemToggle.IsOn = previous;
            _isLoaded = true;
            ShowInfo("系统锁定偏好未能保存。", InfoBarSeverity.Error);
            return;
        }
        if (LockOnSystemToggle.IsOn)
        {
            BossKeyService.Instance.RegisterSessionNotifications();
            if (!BossKeyService.Instance.SessionNotificationsRegistered)
                ShowInfo("Windows 锁屏通知暂不可用，重新打开应用后重试。", InfoBarSeverity.Warning);
        }
    }

    private void ClearClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.ClearClipboardOnLock = ClearClipboardToggle.IsOn;
        SavePreferences();
    }

    #endregion

    #region Boss key

    private void BossKeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        bool requested = BossKeyToggle.IsOn;
        bool ok = BossKeyService.Instance.EnableBossKey(requested);
        if (requested && !ok)
        {
            _isLoaded = false;
            BossKeyToggle.IsOn = false;
            _isLoaded = true;
            ShowInfo(BossKeyService.Instance.LastBossKeyError, InfoBarSeverity.Error);
            SettingsService.Instance.Data.BossKeyEnabled = false;
            SavePreferences();
            return;
        }

        SettingsService.Instance.Data.BossKeyEnabled = requested;
        SavePreferences();
    }

    private void BossKeyActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.BossKeyAction = Math.Clamp(BossKeyActionCombo.SelectedIndex, 0, 2);
        SavePreferences();
    }

    private void BossKeyMuteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.MuteOnBossKey = BossKeyMuteToggle.IsOn;
        SavePreferences();
    }

    #endregion

    #region Browser / cleaner

    private void SearchEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.SearchEngine = SearchEngineCombo.SelectedIndex switch
        {
            1 => "Startpage",
            2 => "Bing",
            3 => "Google",
            _ => "DuckDuckGo"
        };
        SavePreferences();
    }

    private void AutoCheckThumbToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.AutoCheckThumbcacheOnStartup = AutoCheckThumbToggle.IsOn;
        SavePreferences();
    }

    private void AutoFlushDnsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SettingsService.Instance.Data.AutoFlushDnsOnExit = AutoFlushDnsToggle.IsOn;
        SavePreferences();
    }

    #endregion

    #region Appearance

    private void CamouflageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || MainWindow.Current == null) return;

        SettingsService.Instance.Data.CamouflageIndex = Math.Clamp(CamouflageCombo.SelectedIndex, 0, 3);
        SavePreferences();

        MainWindow.Current.UpdateTitle(MainWindow.GetCamouflageTitle(CamouflageCombo.SelectedIndex));
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || MainWindow.Current == null) return;

        var theme = ThemeCombo.SelectedIndex switch
        {
            1 => ElementTheme.Dark,
            2 => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        SettingsService.Instance.Data.AppTheme = ThemeCombo.SelectedIndex switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "Default"
        };
        SavePreferences();

        if (MainWindow.Current.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme;
        }
    }

    #endregion

    #region Password management

    private async void ChangeMasterPwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy) return;

        var current = CreatePasswordBox("当前主密码", "验证身份后才会修改");
        var newPwd = CreatePasswordBox("新的主密码", "至少 6 个字符");
        var confirm = CreatePasswordBox("确认新密码", "再次输入新密码");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "修改后会用新密码重新加密全部真实媒体，过程中请勿锁定或关闭应用。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        panel.Children.Add(current);
        panel.Children.Add(newPwd);
        panel.Children.Add(confirm);

        if (!await ShowDialogAsync("修改主密码", panel, "开始重新加密")) return;

        string oldPassword = current.Password;
        string newPassword = newPwd.Password;
        string newConfirm = confirm.Password;

        if (newPassword.Length < 6)
        {
            ShowInfo("新密码至少需要 6 个字符。", InfoBarSeverity.Warning);
            return;
        }
        if (!string.Equals(newPassword, newConfirm, StringComparison.Ordinal))
        {
            ShowInfo("两次输入的新密码不一致。", InfoBarSeverity.Warning);
            return;
        }

        if (!BeginWork("修改主密码")) return;
        try
        {
            ShowActivity("正在验证主密码…");
            using var verify = await VaultService.Instance.VerifyPasswordAsync(oldPassword);
            if (verify.Status != VaultUnlockStatus.Success || verify.IsDecoy)
            {
                verify.ForgetKey();
                ShowInfo(verify.Status == VaultUnlockStatus.LockedOut
                    ? $"尝试次数过多，请等待 {Math.Max(1, verify.RetryAfterSeconds)} 秒后重试。"
                    : verify.Status == VaultUnlockStatus.Corrupted ? verify.Message : "当前主密码不正确。",
                    verify.Status == VaultUnlockStatus.LockedOut ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                return;
            }
            verify.ForgetKey();

            var progress = CreateProgress("正在重新加密真实媒体…");
            _work!.Token.ThrowIfCancellationRequested();
            await VaultService.Instance.ChangeMasterPasswordAsync(newPassword, progress, _work.Token);
            ShowInfo("主密码已更新，全部真实媒体已重新加密。", InfoBarSeverity.Success);
            RefreshVaultSecuritySection();
        }
        catch (Exception ex)
        {
            AppLog.Error("修改主密码失败", ex);
            ShowInfo("修改主密码失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndWork();
        }
    }

    private async void DecoyPwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy) return;

        bool hasDecoy = VaultService.Instance.HasDecoyVault;
        PasswordBox? oldDecoy = null;
        var newPwd = CreatePasswordBox("新的诱饵密码", "至少 6 个字符");
        var confirm = CreatePasswordBox("确认诱饵密码", "再次输入诱饵密码");

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = hasDecoy
                ? "修改诱饵密码需要先验证当前诱饵密码，随后重新加密诱饵空间内的媒体。若已忘记旧诱饵密码，请先移除诱饵空间再重新创建。"
                : "设置诱饵密码后，在锁屏界面输入它即可进入一个独立的无害空间。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        if (hasDecoy)
        {
            oldDecoy = CreatePasswordBox("当前诱饵密码", "用于验证身份");
            panel.Children.Add(oldDecoy);
        }
        panel.Children.Add(newPwd);
        panel.Children.Add(confirm);

        if (!await ShowDialogAsync(hasDecoy ? "修改诱饵密码" : "设置诱饵密码", panel, "确认")) return;

        string newPassword = newPwd.Password;
        string newConfirm = confirm.Password;
        if (newPassword.Length < 6)
        {
            ShowInfo("诱饵密码至少需要 6 个字符。", InfoBarSeverity.Warning);
            return;
        }
        if (!string.Equals(newPassword, newConfirm, StringComparison.Ordinal))
        {
            ShowInfo("两次输入的诱饵密码不一致。", InfoBarSeverity.Warning);
            return;
        }

        if (!BeginWork("设置诱饵密码")) return;
        try
        {
            var progress = CreateProgress(hasDecoy ? "正在重新加密诱饵空间…" : "正在创建诱饵空间…");
            await VaultService.Instance.SetDecoyPasswordAsync(newPassword, oldDecoy?.Password, progress, _work!.Token);
            ShowInfo(hasDecoy ? "诱饵密码已更新。" : "诱饵空间已创建。", InfoBarSeverity.Success);
            RefreshVaultSecuritySection();
        }
        catch (Exception ex)
        {
            AppLog.Error("设置诱饵密码失败", ex);
            ShowInfo("设置诱饵密码失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndWork();
        }
    }

    private async void ClearDecoyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy) return;

        if (!await ShowDialogAsync("移除诱饵空间",
                "将永久删除诱饵空间内的全部媒体，并清除诱饵密码。真实保险箱不受影响。",
                "确认移除")) return;

        if (!BeginWork("移除诱饵空间")) return;
        try
        {
            ShowActivity("正在移除诱饵空间…");
            await VaultService.Instance.ClearDecoyAsync();
            ShowInfo("诱饵空间已移除。", InfoBarSeverity.Success);
            RefreshVaultSecuritySection();
        }
        catch (Exception ex)
        {
            AppLog.Error("移除诱饵空间失败", ex);
            ShowInfo("移除失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndWork();
        }
    }

    private async void ResetVaultBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy) return;

        if (!await ShowDialogAsync("重置保险箱",
                "此操作会永久销毁真实保险箱与诱饵空间的全部加密媒体和配置，无法恢复。建议先导出重要资料。",
                "永久销毁")) return;

        if (!BeginWork("重置保险箱")) return;
        try
        {
            ShowActivity("正在重置保险箱…");
            await VaultService.Instance.ResetVaultAsync();
            ShowInfo("保险箱已重置。", InfoBarSeverity.Success);
            MainWindow.Current?.LockWorkspace();
        }
        catch (Exception ex)
        {
            AppLog.Error("重置保险箱失败", ex);
            ShowInfo("重置失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndWork();
        }
    }

    #endregion

    #region Helpers

    private static PasswordBox CreatePasswordBox(string header, string placeholder) => new()
    {
        Header = header,
        PlaceholderText = placeholder,
        PasswordRevealMode = PasswordRevealMode.Peek,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private async Task<bool> ShowDialogAsync(string title, object content, string primaryText)
    {
        if (XamlRoot == null) return false;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        try
        {
            return await WorkspaceDialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Error("显示设置对话框失败", ex);
            return false;
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        if (!IsLoaded) return;
        SettingsProgress.IsIndeterminate = false;
        SettingsProgress.Visibility = Visibility.Collapsed;
        SettingsInfoBar.IsClosable = true;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.Severity = severity;
        UiMotion.Open(SettingsInfoBar);
    }

    private void SavePreferences()
    {
        if (!SettingsService.Instance.SaveSettings())
            ShowInfo("此项更改未能保存到磁盘。请检查设置文件的可用性，重启后核对偏好。", InfoBarSeverity.Warning);
    }

    private void ShowActivity(string message, bool indeterminate = true)
    {
        _work?.SetPhase(message.TrimEnd('…'));
        if (!IsLoaded) return;
        SettingsProgress.Value = 0;
        SettingsProgress.Visibility = Visibility.Visible;
        SettingsProgress.IsIndeterminate = indeterminate;
        SettingsInfoBar.IsClosable = false;
        SettingsInfoBar.Severity = InfoBarSeverity.Informational;
        SettingsInfoBar.Message = message;
        UiMotion.Open(SettingsInfoBar);
    }

    private IProgress<double> CreateProgress(string message)
    {
        ShowActivity(message, indeterminate: false);
        var work = _work;
        string phase = message.TrimEnd('…');
        return new PercentProgress(percent =>
        {
            if (work?.Label != phase || !ReferenceEquals(_work, work)) return;
            work.Report(percent / 100);
            if (IsLoaded && SettingsInfoBar.Message == message)
                SettingsProgress.Value = percent;
        });
    }

    // File operations can report per block. Coalesce before dispatch to the UI thread.
    private sealed class PercentProgress(Action<double> update) : Progress<double>(update)
    {
        private readonly System.Threading.Lock _sync = new();
        private int _previous = -1;

        protected override void OnReport(double value)
        {
            int percent = (int)Math.Floor(Math.Clamp(value, 0, 1) * 100);
            lock (_sync)
            {
                if (percent <= _previous) return;
                _previous = percent;
                base.OnReport(percent);
            }
        }
    }

    private bool BeginWork(string label)
    {
        if (!IsLoaded || _work != null) return false;
        _work = VaultWork.TryBegin(label);
        if (_work == null) ShowInfo("请等待当前保险箱任务完成后重试。", InfoBarSeverity.Warning);
        return _work != null;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
    }

    private void UpdateActions()
    {
        bool busy = VaultWork.IsBusy || _picking;
        ImportBookmarksButton.IsEnabled = !busy && !SettingsService.Instance.ReadDeferred;
        ExportBookmarksButton.IsEnabled = !busy && SettingsService.Instance.CurrentBookmarks.Count > 0;
        BackupButton.IsEnabled = !busy;
        VerifyBackupButton.IsEnabled = !busy;
        ChangeMasterPwdBtn.IsEnabled = !busy;
        DecoyPwdBtn.IsEnabled = !busy;
        ClearDecoyBtn.IsEnabled = !busy && VaultService.Instance.HasDecoyVault;
        ResetVaultBtn.IsEnabled = !busy;
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy || _picking) return;
        _picking = true;
        UpdateActions();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(MainWindow.CurrentHandle))
            {
                SuggestedFileName = $"保险箱备份-{DateTime.Now:yyyyMMdd-HHmm}",
                DefaultFileExtension = ".swbackup"
            };
            picker.FileTypeChoices.Add("加密保险箱备份", new[] { ".swbackup" });
            var target = await WorkspaceNativeDialogs.PickAsync(picker.PickSaveFileAsync);
            if (target == null || !BeginWork("创建备份")) return;
            int count = await VaultService.Instance.CreateVerifiedBackupAsync(target.Path, CreateProgress("正在创建并核对备份…"), _work!.Token);
            ShowInfo($"备份已保存，已核对 {count} 个媒体文件的摘要、文件头和索引。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("备份未完成：" + ex.Message, InfoBarSeverity.Error); }
        finally { _picking = false; EndWork(); if (IsLoaded) UpdateActions(); }
    }

    private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
    {
        if (VaultWork.IsBusy || _picking) return;
        _picking = true;
        UpdateActions();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(MainWindow.CurrentHandle));
            picker.FileTypeFilter.Add(".swbackup");
            var source = await WorkspaceNativeDialogs.PickAsync(picker.PickSingleFileAsync);
            if (source == null || !BeginWork("检查备份")) return;
            int count = await VaultService.Instance.VerifyBackupAsync(source.Path, _work!.Token, CreateProgress("正在检查备份…"));
            ShowInfo($"已核对 {count} 个媒体文件的摘要、文件头和索引。要确认媒体可解密，请用原密码恢复后打开或导出。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("备份检查失败：" + ex.Message, InfoBarSeverity.Error); }
        finally { _picking = false; EndWork(); if (IsLoaded) UpdateActions(); }
    }

    #endregion
}
