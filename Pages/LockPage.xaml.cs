using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Pages;

public sealed partial class LockPage : Page
{
    private bool _busy;
    private CancellationTokenSource? _cooldownCts;

    public LockPage()
    {
        InitializeComponent();
        BuildLabel.Text = "构建 " + AppInfo.BuildStamp;
        Loaded += LockPage_Loaded;
        Unloaded += (_, _) => _cooldownCts?.Cancel();
    }

    internal void ClearSensitiveInputs()
    {
        _cooldownCts?.Cancel();
        UnlockPasswordBox.Password = string.Empty;
        if (SetupPanel != null)
        {
            SetupMasterPwd.Password = SetupConfirmPwd.Password = string.Empty;
            SetupDecoyPwd.Password = SetupDecoyConfirmPwd.Password = string.Empty;
            SetDecoyPassword.IsChecked = false;
        }
        if (IsLoaded) CheckVaultState();
    }

    private void LockPage_Loaded(object sender, RoutedEventArgs e)
    {
        CheckVaultState();
    }

    private void CheckVaultState()
    {
        if (!ReferenceEquals(Frame?.Content, this)) return;
        var vault = VaultService.Instance;
        MsgInfoBar.IsOpen = false;

        bool corrupted = vault.IsCorrupted;
        bool unlock = !corrupted && vault.HasVault;
        if (corrupted)
        {
            CorruptDetailText.Text = string.IsNullOrEmpty(vault.LoadError)
                ? "加密配置无法解密或已损坏。为避免覆盖数据，应用不会自动进入首次设置。"
                : vault.LoadError;
        }

        ForgotButton.Visibility = unlock ? Visibility.Visible : Visibility.Collapsed;
        UiMotion.SetVisible(UnlockPanel, unlock);
        bool setup = !corrupted && !unlock;
        if (setup && SetupPanel == null) FindName(nameof(SetupPanel));
        if (SetupPanel != null) UiMotion.SetVisible(SetupPanel, setup);
        UiMotion.SetVisible(CorruptPanel, corrupted);
        FocusField(corrupted ? RetryButton : unlock ? UnlockPasswordBox : SetupMasterPwd);
    }

    private void FocusField(Control target)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && ReferenceEquals(Frame?.Content, this) && target.IsEnabled) target.Focus(FocusState.Programmatic);
        });
    }

    private void UnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = DoUnlockAsync();
    }

    private void UnlockPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = DoUnlockAsync();
            e.Handled = true;
        }
    }

    private async Task DoUnlockAsync()
    {
        if (_busy || _cooldownCts != null) return;

        string password = UnlockPasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowMessage("请输入密码", InfoBarSeverity.Warning, UnlockPasswordBox);
            return;
        }

        UnlockPasswordBox.Password = string.Empty;
        SetBusy(true, "正在校验密码…");
        try
        {
            // PBKDF2 (600k iterations) must never run on the UI thread.
            using var result = await VaultService.Instance.VerifyPasswordAsync(password);

            switch (result.Status)
            {
                case VaultUnlockStatus.Success:
                    UnlockPasswordBox.Password = string.Empty;
                    if (VaultService.Instance.ApplyUnlock(result))
                    {
                        MainWindow.Current?.OnUnlocked();
                    }
                    else
                    {
                        ShowMessage("无法加载保险箱，请重试。", InfoBarSeverity.Error);
                    }
                    break;

                case VaultUnlockStatus.LockedOut:
                    ShowMessage(result.Message, InfoBarSeverity.Warning);
                    UnlockPasswordBox.Password = string.Empty;
                    _ = CoolDownAsync(result.RetryAfterSeconds);
                    break;

                case VaultUnlockStatus.Corrupted:
                    UnlockPasswordBox.Password = string.Empty;
                    CheckVaultState();
                    break;

                case VaultUnlockStatus.NoVault:
                    UnlockPasswordBox.Password = string.Empty;
                    CheckVaultState();
                    break;

                case VaultUnlockStatus.Error:
                    UnlockPasswordBox.Password = string.Empty;
                    ShowMessage(string.IsNullOrEmpty(result.Message) ? "校验失败。" : result.Message, InfoBarSeverity.Error);
                    break;

                default:
                    UnlockPasswordBox.Password = string.Empty;
                    ShowMessage("密码错误，请重试", InfoBarSeverity.Error, UnlockPasswordBox);
                    break;
            }
        }
        catch (Exception ex)
        {
            UnlockPasswordBox.Password = string.Empty;
            AppLog.Error("解锁流程异常", ex);
            ShowMessage("解锁时发生内部错误，请重试。", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task CoolDownAsync(int seconds)
    {
        if (seconds <= 0) return;

        _cooldownCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _cooldownCts = cts;

        BusyRing.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        BuildLabel.Visibility = Visibility.Visible;
        UnlockButton.IsEnabled = false;
        UnlockPasswordBox.IsEnabled = false;
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                if (!IsLoaded || cts.IsCancellationRequested) return;
                ShowMessage($"尝试次数过多，请在 {remaining} 秒后重试。", InfoBarSeverity.Warning);
                await Task.Delay(1000, cts.Token);
            }
            MsgInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_cooldownCts, cts))
            {
                _cooldownCts = null;
                UnlockButton.IsEnabled = !_busy;
                UnlockPasswordBox.IsEnabled = !_busy;
            }
        }
        FocusField(UnlockPasswordBox);
    }

    private async void SetupConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string master = SetupMasterPwd.Password;
        string confirm = SetupConfirmPwd.Password;
        string decoy = SetDecoyPassword.IsChecked == true ? SetupDecoyPwd.Password : string.Empty;
        string decoyConfirm = SetDecoyPassword.IsChecked == true ? SetupDecoyConfirmPwd.Password : string.Empty;

        if (string.IsNullOrWhiteSpace(master))
        {
            ShowMessage("访问密码不能为空", InfoBarSeverity.Warning, SetupMasterPwd);
            return;
        }
        if (master.Length < 6)
        {
            ShowMessage("访问密码至少需要 6 个字符", InfoBarSeverity.Warning, SetupMasterPwd);
            return;
        }
        if (!string.Equals(master, confirm, StringComparison.Ordinal))
        {
            ShowMessage("两次输入的密码不一致", InfoBarSeverity.Warning, SetupConfirmPwd);
            return;
        }

        if (SetDecoyPassword.IsChecked == true)
        {
            if (decoy.Length < 6)
            {
                ShowMessage("诱饵密码至少需要 6 个字符", InfoBarSeverity.Warning, SetupDecoyPwd);
                return;
            }
            if (!string.Equals(decoy, decoyConfirm, StringComparison.Ordinal))
            {
                ShowMessage("两次输入的诱饵密码不一致", InfoBarSeverity.Warning, SetupDecoyConfirmPwd);
                return;
            }
            if (string.Equals(decoy, master, StringComparison.Ordinal))
            {
                ShowMessage("诱饵密码不能与主密码相同", InfoBarSeverity.Warning, SetupDecoyPwd);
                return;
            }
        }

        SetBusy(true, "正在创建加密保险箱…");
        try
        {
            await VaultService.Instance.InitializeVaultAsync(master, string.IsNullOrEmpty(decoy) ? null : decoy);

            SetupMasterPwd.Password = string.Empty;
            SetupConfirmPwd.Password = string.Empty;
            SetupDecoyPwd.Password = string.Empty;
            SetupDecoyConfirmPwd.Password = string.Empty;
            if (VaultService.Instance.IsUnlocked) MainWindow.Current?.OnUnlocked();
            else
            {
                CheckVaultState();
                ShowMessage("保险箱已创建，请重新输入密码解锁。", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ClearSensitiveInputs();
            AppLog.Error("初始化保险箱失败", ex);
            ShowMessage("创建保险箱失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e)
    {
        VaultService.Instance.ReloadConfig();
        CheckVaultState();
    }

    private async void ResetVaultBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (XamlRoot == null)
        {
            ShowMessage("界面尚未就绪，请稍后重试。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置保险箱？",
            Content = "此操作会删除当前保险箱内的全部加密媒体与配置，无法通过应用撤销。\n\n旧版迁移副本、导出文件和外部备份不会一并删除。\n\n只有在确认密码遗失或配置损坏时才应继续。",
            PrimaryButtonText = "删除并重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        ContentDialogResult result;
        try
        {
            result = await WorkspaceDialogs.ShowAsync(dialog);
        }
        catch (Exception ex)
        {
            AppLog.Error("显示重置确认框失败", ex);
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        _cooldownCts?.Cancel();
        SetBusy(true, "正在销毁保险箱…");
        try
        {
            await VaultService.Instance.ResetVaultAsync();
            CheckVaultState();
            ShowMessage("保险箱已重置，请重新设置访问密码。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("重置保险箱失败", ex);
            ShowMessage("重置失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        BusyText.Text = text;
        BuildLabel.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UiMotion.SetVisible(BusyPanel, busy, MotionCue.Fade);
        BusyRing.IsActive = busy;
        UnlockButton.IsEnabled = !busy && _cooldownCts == null;
        if (SetupPanel != null)
        {
            SetupButton.IsEnabled = SetupMasterPwd.IsEnabled = SetupConfirmPwd.IsEnabled = !busy;
            SetDecoyPassword.IsEnabled = SetupDecoyPwd.IsEnabled = SetupDecoyConfirmPwd.IsEnabled = !busy;
            RestoreBackupButton.IsEnabled = !busy;
        }
        UnlockPasswordBox.IsEnabled = !busy && _cooldownCts == null;
    }

    private void DecoySetupChanged(object sender, RoutedEventArgs args)
    {
        if (DecoySetupFields == null) return;
        bool enabled = SetDecoyPassword.IsChecked == true;
        DecoySetupFields.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) SetupDecoyPwd.Password = SetupDecoyConfirmPwd.Password = string.Empty;
    }

    internal async Task CheckSetupControlsAsync(bool createDecoy, Func<LockPage?> currentLock)
    {
        var vault = VaultService.Instance;
        if (!SmokeTest.UiSmoke || !AppPaths.HasDataRootOverride || vault.HasVault)
            throw new InvalidOperationException("Setup controls require an empty isolated test vault");
        if (!IsLoaded || SetupPanel?.Visibility != Visibility.Visible) throw new InvalidOperationException("First-run form is missing");
        const string master = "SmokeTest#123", decoy = "Secondary#Fixture17";
        SetupMasterPwd.Password = SetupConfirmPwd.Password = master;
        SetDecoyPassword.IsChecked = true;
        MediaAcceptance.Invoke(SetupButton);
        for (int i = 0; i < 80 && !MsgInfoBar.IsOpen; i++) await Task.Delay(20);
        if (vault.HasVault || !MsgInfoBar.IsOpen || MsgInfoBar.Severity != InfoBarSeverity.Warning)
            throw new InvalidOperationException("An enabled blank secondary password must require a correction");
        SetupDecoyPwd.Password = SetupDecoyConfirmPwd.Password = decoy;
        SetDecoyPassword.IsChecked = false;
        if (SetupDecoyPwd.Password.Length != 0 || SetupDecoyConfirmPwd.Password.Length != 0 || DecoySetupFields.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Clearing the optional password left input behind");
        if (createDecoy)
        {
            SetDecoyPassword.IsChecked = true;
            SetupDecoyPwd.Password = SetupDecoyConfirmPwd.Password = decoy;
        }
        MediaAcceptance.Invoke(SetupButton);
        for (int i = 0; i < 300 && !vault.IsUnlocked; i++) await Task.Delay(20);
        if (!vault.IsUnlocked || vault.IsDecoyMode) throw new InvalidOperationException("Setup failed to open the primary vault");
        async Task<LockPage> OpenLockAsync()
        {
            MainWindow.Current!.LockWorkspace();
            for (int i = 0; i < 200 && currentLock() is not { IsLoaded: true }; i++) await Task.Delay(20);
            var lockPage = currentLock() ?? throw new InvalidOperationException("Lock page did not open");
            if (!lockPage.IsLoaded || lockPage.SetupPanel != null || lockPage.UnlockPanel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Everyday lock instantiated first-run controls");
            return lockPage;
        }
        async Task SubmitAsync(LockPage page, string password)
        {
            page.UnlockPasswordBox.Password = password;
            MediaAcceptance.Invoke(page.UnlockButton);
            for (int i = 0; i < 300 && (page._busy || page.UnlockPasswordBox.Password.Length > 0); i++) await Task.Delay(20);
            if (page._busy || page.UnlockPasswordBox.Password.Length > 0) throw new InvalidOperationException("Unlock button did not finish");
        }
        var ordinary = await OpenLockAsync();
        await SubmitAsync(ordinary, master);
        if (!vault.IsUnlocked || vault.IsDecoyMode) throw new InvalidOperationException("Primary password did not open the primary vault");
        ordinary = await OpenLockAsync();
        await SubmitAsync(ordinary, decoy);
        if (createDecoy ? !vault.IsUnlocked || !vault.IsDecoyMode : vault.IsUnlocked)
            throw new InvalidOperationException("Secondary password did not match the selected setup mode");
        if (!createDecoy && (!ordinary.MsgInfoBar.IsOpen || ordinary.MsgInfoBar.Severity != InfoBarSeverity.Error))
            throw new InvalidOperationException("Unlock did not report the disabled second password as incorrect");
        SmokeTest.Report($"setup controls: cleared disabled secrets, primary opened, secondary enabled={createDecoy}; everyday lock left setup uncreated");
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "正在选择备份…");
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(MainWindow.CurrentHandle));
            picker.FileTypeFilter.Add(".swbackup");
            var source = await WorkspaceNativeDialogs.PickAsync(picker.PickSingleFileAsync);
            if (source == null) return;
            BusyText.Text = "正在校验并恢复备份…";
            int count = await VaultService.Instance.RestoreBackupAsync(source.Path);
            CheckVaultState();
            ShowMessage($"已恢复 {count} 个文件，请使用原保险箱密码解锁。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("恢复失败：" + ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false, string.Empty); }
    }

    private void ShowMessage(string message, InfoBarSeverity severity, Control? focusTarget = null)
    {
        MsgInfoBar.Message = message;
        MsgInfoBar.Severity = severity;
        UiMotion.Open(MsgInfoBar);
        if (focusTarget != null) FocusField(focusTarget);
    }
}
