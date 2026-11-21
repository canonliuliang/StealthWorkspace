using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

/// <summary>
/// Binds an exclusive WebView2 environment to the current unlock session.
/// Personal vaults retain their own native site-account directory and clear
/// history at the session boundaries. Decoy sessions use disposable profiles.
/// The aged-profile sweep only recognizes the private, marked disposable format.
/// </summary>
public static partial class BrowserProfileManager
{
    private const string ProfileDirectoryPrefix = "Stealth_WebProfile_";
    private const string OwnerMarkerName = ".stealth-session-owner";
    private const int DeleteAttempts = 20;
    private const int DeleteRetryDelayMs = 250;
    private const int ShutdownWaitRounds = 20;
    private const int ShutdownWaitRoundMs = 100;

    private static readonly TimeSpan StaleProfileAge = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim EnvironmentCreationLock = new(1, 1);
    private static readonly SemaphoreSlim ShutdownLock = new(1, 1);
    private static readonly object PendingDeletionGate = new();
    private static readonly List<string> PendingDeletion = new();

    private static CoreWebView2Environment? _sharedEnvironment;
    private static string? _sharedUserDataDirectory;
    private static FileStream? _profileOwnerLease;
    private static TaskCompletionSource<bool>? _sessionEnding;
    private static int _environmentUsers;

    internal static string? ActiveSessionDirectory => _sharedUserDataDirectory;

    /// <summary>
    /// Registers that a tab is about to use (or is already using) the shared
    /// environment. Paired with <see cref="ReleaseEnvironmentUse"/>; used by
    /// <see cref="ShutdownAsync"/> to wait for the browser handles to drain.
    /// </summary>
    public static void AcquireEnvironmentUse()
    {
        Interlocked.Increment(ref _environmentUsers);
    }

    /// <summary>Releases one environment lease previously taken by <see cref="AcquireEnvironmentUse"/>.</summary>
    public static void ReleaseEnvironmentUse()
    {
        int remaining = Interlocked.Decrement(ref _environmentUsers);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _environmentUsers, 0);
            AppLog.Warn("BrowserProfileManager: 环境引用计数下溢，已重置为 0");
        }
    }

    /// <summary>
    /// Returns the shared environment owned by the current unlock session.
    /// </summary>
    public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync() =>
        GetSharedEnvironmentAsync(SettingsService.Instance.ActiveBookmarkSession);

    internal static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(SettingsService.BookmarkSession? workspace)
    {
        if (Volatile.Read(ref _sessionEnding) is { } ending) await ending.Task;
        RequireCurrentWorkspace(workspace);
        // Disposal may have exceeded a previous deadline and released its
        // final controller later. Retry teardown before a different unlock
        // session attaches; never hand it the previous environment.
        if (_sharedEnvironment != null && !ReferenceEquals(workspace,_environmentWorkspace)) await ShutdownAsync();

        await EnvironmentCreationLock.WaitAsync();
        try
        {
            RequireCurrentWorkspace(workspace);
            if (_sharedEnvironment != null)
            {
                if (!ReferenceEquals(workspace, _environmentWorkspace)) throw new InvalidOperationException("上一个浏览空间尚未关闭，请稍后重试。");
                return _sharedEnvironment;
            }

            LastInitializationError = string.Empty;

            // Keep normal rendering capabilities: disabling canvas readback breaks
            // legitimate image tools and can prevent site verification from working.
            // Use these two privacy options on every directory attempt. A
            // fallback with default options could share the browser process or
            // send a crash dump to Microsoft under the Windows consent setting.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = SmokeTest.ReferenceBrowserProbe ? "" :
                    "--disable-features=Translate,OptimizationHints,MediaRouter --no-first-run",
                IsCustomCrashReportingEnabled = true,
                ExclusiveUserDataFolderAccess = true,
                EnableTrackingPrevention = true,
            };

            Exception? lastError = null;
            int candidateIndex = 0;
            bool personal = !workspace!.Temporary;
            string vaultRoot = AppPaths.DataRoot;
            string identity = personal ? VaultService.Instance.GetWebsiteAccountIdentity() : "";
            IEnumerable<string> candidates = personal ? [BrowserAccountDirectory.ForVault(vaultRoot, identity)] : EnumerateProfileDirectoryCandidates();
            foreach (string directory in candidates)
            {
                candidateIndex++;
                FileStream? ownerLease = null;
                string phase = "create-directory";
                try
                {
                    if (personal)
                    {
                        await WaitForPreviousHistoryOperationAsync(directory);
                        ownerLease = BrowserAccountDirectory.Acquire(vaultRoot,directory,identity);
                    }
                    else
                    {
                        Directory.CreateDirectory(directory);
                        AppPaths.Hide(directory);
                        phase = "create-owner-lease";
                        ownerLease = new FileStream(Path.Combine(directory, OwnerMarkerName), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (SmokeTest.Enabled) SmokeTest.Report($"profile candidate {candidateIndex}: phase={phase}; parentExists={Directory.Exists(Path.GetDirectoryName(directory))}; error={DescribeErrorCode(ex)}");
                    AppLog.Warn($"BrowserProfileManager: 用户数据目录候选不可用（{DescribeErrorCode(ex)}）");
                    continue;
                }

                // Disposable profiles may try another location. Persistent
                // accounts have exactly one path and must never silently move.
                try
                {
                    CoreWebView2Environment created = await CoreWebView2Environment.CreateWithOptionsAsync(
                        string.Empty, directory, options);
                    RequireCurrentWorkspace(workspace);
                    if (_sessionEnding != null) throw new OperationCanceledException("浏览会话已结束。");
                    if (!IsSameDirectory(created.UserDataFolder,directory)) throw new IOException("WebView2 实际使用的账户目录与本空间不符。");
                    _sharedEnvironment = created;
                    _sharedUserDataDirectory = directory;
                    _profileOwnerLease = ownerLease;
                    _environmentWorkspace = workspace;
                    _ownerDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    AppLog.Info(personal ? "WebView2 已连接当前保险箱的网站账户目录" : "WebView2 已创建临时网站账户目录");
                    return created;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppLog.Warn($"BrowserProfileManager: WebView2 环境创建尝试失败（{DescribeErrorCode(ex)}）");
                }

                ownerLease?.Dispose();
                if (!personal) AddPendingDeletion(directory);
            }

            LastInitializationError = DescribeRuntimeProblem(lastError);
            AppLog.Error("BrowserProfileManager: WebView2 环境初始化失败", lastError);
            throw new InvalidOperationException(LastInitializationError, lastError);
        }
        finally
        {
            EnvironmentCreationLock.Release();
        }
    }

    private static IEnumerable<string> EnumerateProfileDirectoryCandidates()
    {
        string guid = Guid.NewGuid().ToString("N");
        return EnumerateProfileRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, ProfileDirectoryPrefix + guid));
    }

    private static IEnumerable<string> EnumerateProfileRoots()
    {
        // Browser cache/database IO should stay on the local disk even when the
        // executable, vault, or TEMP folder is redirected to a shared drive.
        string? local = null;
        try
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StealthWorkspace", "BrowserSessions");
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(local)) yield return local!;

        string? temp = null;
        try { temp = Path.GetTempPath(); } catch { }
        if (!string.IsNullOrWhiteSpace(temp))
        {
            yield return temp!;
        }

        string? dataRoot = null;
        try
        {
            dataRoot = AppPaths.EnsureDataRoot();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"BrowserProfileManager: 私有数据目录不可用（{ex.GetType().Name}）");
        }

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            yield return dataRoot!;
        }

        string? legacyLocal = null;
        try
        {
            legacyLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StealthWorkspace");
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(legacyLocal))
        {
            yield return legacyLocal!;
        }

        // Portable/restricted launches may only permit writes beside the executable.
        // Keep the same unique, hidden, disposable profiles in this final location.
        yield return AppContext.BaseDirectory;
    }

    internal static string DescribeErrorCode(Exception exception)
    {
        Exception cause = exception.GetBaseException();
        return $"{cause.GetType().Name}, 0x{cause.HResult:X8}";
    }

    /// <summary>Human readable diagnosis for the kernel-unavailable UI.</summary>
    public static string DescribeRuntimeProblem(Exception? exception)
    {
        string version = GetWebView2RuntimeVersion();
        string detail = exception == null ? string.Empty : "（" + DescribeErrorCode(exception) + "）";
        Exception? cause = exception?.GetBaseException();
        if (cause is UnauthorizedAccessException || cause?.HResult == unchecked((int)0x80070005))
        {
            return "浏览器会话目录访问被拒绝，请将应用放在当前用户可写的文件夹后重试。" + detail;
        }
        if (version == "未安装")
        {
            return "未检测到 Microsoft Edge WebView2 Runtime，请安装后点击刷新重试。" + detail;
        }

        return $"WebView2 运行时 {version} 初始化失败{detail}。请检查安全软件是否拦截浏览器进程，或重新安装 WebView2 Runtime 后重试。";
    }

    /// <summary>Uses the loader's runtime discovery, with a registry fallback.</summary>
    public static string GetWebView2RuntimeVersion()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch
        {
            // Discovery may fail before WinRT initialization; try the registry.
        }

        string[] keys =
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };

        foreach (string subKey in keys)
        {
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine,
                    Microsoft.Win32.RegistryView.Registry64);
                using var key = hklm.OpenSubKey(subKey);
                string? pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(pv)) return pv!;
            }
            catch
            {
                // try next location
            }
        }

        return "未安装";
    }

    /// <summary>Error text from the last failed initialization attempt (encoded, no paths).</summary>
    public static string LastInitializationError { get; private set; } = string.Empty;

    /// <summary>
    /// Legacy helper kept for API compatibility. It only returns a unique path;
    /// it does not create or register a directory (the shared environment is the
    /// only profile this process owns).
    /// </summary>
    public static string CreateVolatileProfilePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            ProfileDirectoryPrefix + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Deletes one specific volatile profile directory (with retries). Refuses to
    /// delete the directory currently owned by this process while the shared
    /// environment is still alive.
    /// </summary>
    public static async Task DeleteProfileAsync(string profileDir)
    {
        if (string.IsNullOrWhiteSpace(profileDir)) return;

        if (IsSameDirectory(profileDir, _sharedUserDataDirectory))
        {
            AppLog.Warn("DeleteProfileAsync: 拒绝删除本进程仍在使用的 WebView2 用户数据目录");
            return;
        }

        if (await DeleteDirectoryWithRetriesAsync(profileDir).ConfigureAwait(false))
        {
            RemovePendingDeletion(profileDir);
        }
    }

    /// <summary>
    /// Startup cleanup: removes only stale (older than 24 hours)
    /// <c>Stealth_WebProfile_*</c> directories. Never touches the current process
    /// directory, the temp root, or any live/young directory.
    /// </summary>
    public static void SweepStaleProfiles()
    {
        try
        {
            string? currentDirectory = _sharedUserDataDirectory;
            int removed = 0;
            int kept = 0;

            foreach ((string root, string[] candidates) in EnumerateProfileSweepRoots())
            {
                foreach (string candidate in candidates)
                {
                    try
                    {
                        // Defensive: never delete the directory this process is using.
                        if (IsSameDirectory(candidate, currentDirectory))
                        {
                            kept++;
                            continue;
                        }

                        var info = new DirectoryInfo(candidate);
                        if (!info.Exists) continue;

                        if (DateTime.UtcNow - info.LastWriteTimeUtc < StaleProfileAge)
                        {
                            kept++;
                            continue;
                        }

                        // Age alone does not prove a profile is abandoned: a user
                        // can keep a browser open for days. A live owner holds this
                        // marker exclusively. Legacy unmarked profiles are left for
                        // explicit cleanup, never guessed safe by the startup sweep.
                        if (TryDeleteAbandonedProfile(candidate, root)) removed++;
                        else kept++;
                    }
                    catch (Exception ex)
                    {
                        // Never log the path itself.
                        AppLog.Warn($"SweepStaleProfiles: 删除陈旧配置目录失败（{ex.GetType().Name}）");
                    }
                }
            }

            if (removed > 0 || kept > 0)
            {
                AppLog.Info($"SweepStaleProfiles: 清理 {removed} 个超过 24 小时的陈旧目录，保留 {kept} 个");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SweepStaleProfiles failed", ex);
        }
    }

    internal static bool TryDeleteAbandonedProfile(string candidate, string expectedRoot)
    {
        candidate = Path.GetFullPath(candidate);
        // Only direct children of the explicitly enumerated profile root qualify.
        if (!IsSameDirectory(Path.GetDirectoryName(candidate), Path.GetFullPath(expectedRoot))) return false;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate));
        if (!name.StartsWith(ProfileDirectoryPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[ProfileDirectoryPrefix.Length..], "N", out _) ||
            !Directory.Exists(candidate) || IsSameDirectory(candidate, _sharedUserDataDirectory)) return false;
        var directory = new DirectoryInfo(candidate);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || DateTime.UtcNow - directory.LastWriteTimeUtc < StaleProfileAge)
            return false;
        try
        {
            DeleteOwnedProfile(candidate);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void DeleteOwnedProfile(string directory)
    {
        var profile = new DirectoryInfo(directory);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(profile.FullName); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        if (!profile.Name.StartsWith(ProfileDirectoryPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(profile.Name[ProfileDirectoryPrefix.Length..], "N", out _) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Invalid browser profile directory.");

        string marker = Path.Combine(profile.FullName, OwnerMarkerName);
        if ((File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Invalid browser profile marker.");

        // A live owner denies this open. Preserve the marker if a child still
        // has a browser/AV handle, so a later startup can reclaim the profile.
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            foreach (var entry in profile.EnumerateFileSystemInfos())
            {
                if (entry.Name.Equals(OwnerMarkerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    child.Delete(recursive: true);
                else entry.Delete();
            }
            // Recursive Windows deletes may return before a child disappears.
            if (profile.EnumerateFileSystemInfos().Any(entry => !entry.Name.Equals(OwnerMarkerName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Browser profile still contains pending items.");
            File.Delete(marker);
        }

        try { profile.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A final directory lock or a late file creation can still prevent
            // removal. Keep a safe retry possible after the host exits.
            if (profile.Exists && !File.GetAttributes(profile.FullName).HasFlag(FileAttributes.ReparsePoint))
            {
                using var restored = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            throw;
        }
    }

    private static IEnumerable<(string Root, string[] Candidates)> EnumerateProfileSweepRoots()
    {
        foreach (string root in EnumerateProfileRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(root, ProfileDirectoryPrefix + "*");
            }
            catch (Exception ex)
            {
                AppLog.Error("SweepStaleProfiles: 无法枚举配置目录", ex);
                candidates = Array.Empty<string>();
            }

            yield return (root, candidates);
        }
    }

    /// <summary>
    /// Backwards-compatible alias for <see cref="SweepStaleProfiles"/> (App startup
    /// calls this name). It is now stale-age gated and never touches live profiles.
    /// </summary>
    public static void SweepOrphanedProfiles() => SweepStaleProfiles();

    /// <summary>
    /// Waits (bounded) for every tab to release the shared environment, drops the
    /// static environment reference, then deletes this process' volatile user data
    /// directory with retries. Idempotent: calling it again is a cheap no-op once
    /// the directory is gone, and any directory left behind is retried on the next
    /// call. Never throws.
    /// </summary>
    public static async Task<bool> ShutdownAsync()
    {
        try
        {
            await ShutdownLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 1) Wait for the tab controls to release their environment leases.
                int waitedRounds = 0;
                while (Volatile.Read(ref _environmentUsers) > 0 && waitedRounds < ShutdownWaitRounds)
                {
                    await Task.Delay(ShutdownWaitRoundMs).ConfigureAwait(false);
                    waitedRounds++;
                }

                int remainingUsers = Volatile.Read(ref _environmentUsers);
                if (remainingUsers > 0)
                {
                    AppLog.Warn($"ShutdownAsync: 仍有 {remainingUsers} 个标签，保留所有权并等待下一次清理");
                    return false;
                }

                // 2) Drop the static environment reference so the browser process can exit.
                string? ownedDirectory = null;
                await EnvironmentCreationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_environmentWorkspace is { Temporary: false } && _sharedEnvironment is { } personalEnvironment)
                    {
                        bool cleared = await FinishHistoryWithinDeadlineAsync(personalEnvironment, _sharedUserDataDirectory!).ConfigureAwait(false);
                        LastPersonalHistoryCleanupSucceeded = cleared;
                    }
                    else ownedDirectory = _sharedUserDataDirectory;
                    _sharedEnvironment = null;
                    _sharedUserDataDirectory = null;
                    _environmentWorkspace = null;
                    _entryCleanup = null;
                    _entryBrowserProcess = 0;
                    _ownerDispatcher = null;
                    _profileOwnerLease?.Dispose();
                    _profileOwnerLease = null;
                }
                finally
                {
                    EnvironmentCreationLock.Release();
                }

                if (!string.IsNullOrEmpty(ownedDirectory))
                {
                    AddPendingDeletion(ownedDirectory!);
                    // Closed WinRT WebView/environment wrappers can retain an EBWebView
                    // directory handle until finalization. Reclaim only at session teardown,
                    // after every controller is closed; never pause normal browsing for GC.
                    if (Volatile.Read(ref _environmentUsers) == 0)
                    {
                        await Task.Run(() =>
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }).ConfigureAwait(false);
                    }
                }

                // 3) Retry every directory we still owe the file system. The WebView2
                //    browser process needs a moment to release its file handles.
                string[] pending = SnapshotPendingDeletion();
                int removed = 0;
                foreach (string directory in pending)
                {
                    if (await Task.Run(() => DeleteDirectoryWithRetriesAsync(directory)).ConfigureAwait(false))
                    {
                        RemovePendingDeletion(directory);
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    AppLog.Info($"ShutdownAsync: 已销毁 {removed} 个 WebView2 易失性用户数据目录");
                }
                else if (pending.Length == 0)
                {
                    AppLog.Info("ShutdownAsync: 没有需要销毁的 WebView2 用户数据目录");
                }
            }
            finally
            {
                ShutdownLock.Release();
            }
            return SnapshotPendingDeletion().Length == 0 && LastPersonalHistoryCleanupSucceeded;
        }
        catch (Exception ex)
        {
            // Must be safe to call from the app teardown path: never throw.
            AppLog.Error("ShutdownAsync failed", ex);
            return false;
        }
    }

    internal static async Task<bool> EndSessionAsync(Func<Task> closeTabs)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool>? current = Interlocked.CompareExchange(ref _sessionEnding, completion, null);
        if (current != null) return await current.Task;

        bool cleaned = false;
        try
        {
            await closeTabs();
            cleaned = await ShutdownAsync();
            return cleaned;
        }
        finally
        {
            Interlocked.Exchange(ref _sessionEnding, null);
            completion.TrySetResult(cleaned);
        }
    }

    private static async Task<bool> DeleteDirectoryWithRetriesAsync(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return true;

        for (int attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                DeleteOwnedProfile(directory);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == DeleteAttempts - 1)
                {
                    AppLog.Warn($"删除 WebView2 易失性目录失败，将留待后续清理（{ex.GetType().Name}）");
                    return false;
                }

                await Task.Delay(DeleteRetryDelayMs).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static void AddPendingDeletion(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        lock (PendingDeletionGate)
        {
            if (!PendingDeletion.Contains(directory))
            {
                PendingDeletion.Add(directory);
            }
        }
    }

    private static string[] SnapshotPendingDeletion()
    {
        lock (PendingDeletionGate)
        {
            return PendingDeletion.ToArray();
        }
    }

    private static void RemovePendingDeletion(string directory)
    {
        lock (PendingDeletionGate)
        {
            PendingDeletion.RemoveAll(existing =>
                string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
