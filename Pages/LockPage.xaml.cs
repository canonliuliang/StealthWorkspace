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

    public LockPage()
    {
        InitializeComponent();
        BuildLabel.Text = "构建 " + AppInfo.BuildStamp;
        Loaded += LockPage_Loaded;
    }

    private void LockPage_Loaded(object sender, RoutedEventArgs e)
    {
        CheckVaultState();
    }

    private void CheckVaultState()
    {
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
        UiMotion.SetVisible(SetupPanel, !corrupted && !unlock);
        UiMotion.SetVisible(CorruptPanel, corrupted);
        FocusField(corrupted ? RetryButton : unlock ? UnlockPasswordBox : SetupMasterPwd);
    }

    private void FocusField(Control target)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && target.IsEnabled) target.Focus(FocusState.Programmatic);
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
        if (_busy) return;

        string password = UnlockPasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowMessage("请输入密码", InfoBarSeverity.Warning, UnlockPasswordBox);
            return;
        }

        SetBusy(true, "正在校验密码…");
        try
        {
            // PBKDF2 (600k iterations) must never run on the UI thread.
            using var result = await VaultService.Instance.VerifyPasswordAsync(password);

            switch (result.Status)
            {
                case VaultUnlockStatus.Success:
                    if (VaultService.Instance.ApplyUnlock(result))
                    {
                        UnlockPasswordBox.Password = string.Empty;
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
                    await CoolDownAsync(result.RetryAfterSeconds);
                    break;

                case VaultUnlockStatus.Corrupted:
                    CheckVaultState();
                    break;

                case VaultUnlockStatus.NoVault:
                    CheckVaultState();
                    break;

                case VaultUnlockStatus.Error:
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

        BusyRing.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        BuildLabel.Visibility = Visibility.Visible;
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            if (!IsLoaded) return;
            ShowMessage($"尝试次数过多，请在 {remaining} 秒后重试。", InfoBarSeverity.Warning);
            await Task.Delay(1000);
        }
        MsgInfoBar.IsOpen = false;
        FocusField(UnlockPasswordBox);
    }

    private async void SetupConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string master = SetupMasterPwd.Password;
        string confirm = SetupConfirmPwd.Password;
        string decoy = SetupDecoyPwd.Password;
        string decoyConfirm = SetupDecoyConfirmPwd.Password;

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

        if (!string.IsNullOrEmpty(decoy) || !string.IsNullOrEmpty(decoyConfirm))
        {
            if (decoy.Length < 6)
            {
                DecoyExpander.IsExpanded = true;
                ShowMessage("诱饵密码至少需要 6 个字符", InfoBarSeverity.Warning, SetupDecoyPwd);
                return;
            }
            if (!string.Equals(decoy, decoyConfirm, StringComparison.Ordinal))
            {
                DecoyExpander.IsExpanded = true;
                ShowMessage("两次输入的诱饵密码不一致", InfoBarSeverity.Warning, SetupDecoyConfirmPwd);
                return;
            }
            if (string.Equals(decoy, master, StringComparison.Ordinal))
            {
                DecoyExpander.IsExpanded = true;
                ShowMessage("诱饵密码不能与主密码相同", InfoBarSeverity.Warning, SetupDecoyPwd);
                return;
            }
        }
        else
        {
            decoy = string.Empty;
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
        UnlockButton.IsEnabled = !busy;
        SetupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
        UnlockPasswordBox.IsEnabled = !busy;
        SetupMasterPwd.IsEnabled = !busy;
        SetupConfirmPwd.IsEnabled = !busy;
        SetupDecoyPwd.IsEnabled = !busy;
        SetupDecoyConfirmPwd.IsEnabled = !busy;
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "正在选择备份…");
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
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
