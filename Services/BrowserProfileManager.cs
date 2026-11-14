using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

/// <summary>
/// Owns the single, per-process volatile WebView2 user data directory.
///
/// Design rules:
/// <list type="bullet">
/// <item>Every process gets its own unique <c>%TEMP%\Stealth_WebProfile_&lt;guid&gt;</c>
/// directory, opened with <c>ExclusiveUserDataFolderAccess</c>, so two running
/// instances can never share (or delete) each other's data.</item>
/// <item>Nothing is deleted from the process-exit handler: at that point the
/// WebView2 browser process still owns the files, so deletion is guaranteed to
/// fail and a blind sweep could kill another instance's live directory.</item>
/// <item>Deletion is explicit: callers dispose every tab first
/// (<c>BrowserTabControl.ShutdownAllAsync()</c>) and then call
/// <see cref="ShutdownAsync"/>, which waits for the environment leases to drain
/// and retries the directory removal while the browser process exits.</item>
/// <item>Startup cleanup only removes profiles older than 24 hours and never the
/// directory owned by the current process.</item>
/// </list>
/// </summary>
public static class BrowserProfileManager
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
    /// Creates (once per process) and returns the shared WebView2 environment.
    /// The user data directory is unique to this process and volatile.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (Volatile.Read(ref _sessionEnding) is { } ending) await ending.Task;

        CoreWebView2Environment? existing = _sharedEnvironment;
        if (existing != null) return existing;

        await EnvironmentCreationLock.WaitAsync();
        try
        {
            if (_sharedEnvironment != null) return _sharedEnvironment;

            LastInitializationError = string.Empty;

            // Keep normal rendering capabilities: disabling canvas readback breaks
            // legitimate image tools and can prevent site verification from working.
            // Try the full privacy options first, then a minimal set. Some older /
            // enterprise-managed WebView2 runtimes reject individual options.
            var fullOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-features=Translate,OptimizationHints,MediaRouter --no-first-run",
                IsCustomCrashReportingEnabled = false,
                ExclusiveUserDataFolderAccess = true,
            };

            var minimalOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-features=Translate,OptimizationHints,MediaRouter --no-first-run",
            };

            Exception? lastError = null;
            // Explicit isolated diagnostic: compare a stock WebView with the app's
            // host, not a collection of speculative Chromium flags.
            if (SmokeTest.ReferenceBrowserProbe)
            {
                fullOptions.AdditionalBrowserArguments = "";
                minimalOptions.AdditionalBrowserArguments = "";
            }
            int candidateIndex = 0;
            foreach (string directory in EnumerateProfileDirectoryCandidates())
            {
                candidateIndex++;
                FileStream? ownerLease = null;
                string phase = "create-directory";
                try
                {
                    Directory.CreateDirectory(directory);
                    AppPaths.Hide(directory);
                    phase = "create-owner-lease";
                    ownerLease = new FileStream(Path.Combine(directory, OwnerMarkerName), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (SmokeTest.Enabled) SmokeTest.Report($"profile candidate {candidateIndex}: phase={phase}; parentExists={Directory.Exists(Path.GetDirectoryName(directory))}; error={DescribeErrorCode(ex)}");
                    AppLog.Warn($"BrowserProfileManager: 用户数据目录候选不可用（{DescribeErrorCode(ex)}）");
                    continue;
                }

                // A directory can be writable by the host but inaccessible to the
                // browser process. Only select it after the environment starts;
                // otherwise try the remaining locations with a fresh profile.
                foreach (CoreWebView2EnvironmentOptions options in new[] { fullOptions, minimalOptions })
                {
                    try
                    {
                        CoreWebView2Environment created = await CoreWebView2Environment.CreateWithOptionsAsync(
                            string.Empty,
                            directory,
                            options);
                        _sharedEnvironment = created;
                        _sharedUserDataDirectory = directory;
                        _profileOwnerLease = ownerLease;
                        ownerLease = null;
                        AppLog.Info("BrowserProfileManager: 已创建本进程唯一的 WebView2 易失性用户数据目录");
                        return created;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        AppLog.Warn($"BrowserProfileManager: WebView2 环境创建尝试失败（{DescribeErrorCode(ex)}）");
                    }
                }

                ownerLease?.Dispose();
                AddPendingDeletion(directory);
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
        string marker = Path.Combine(candidate, OwnerMarkerName);
        if (!File.Exists(marker)) return false;
        try
        {
            // Exclusive open fails while another process still owns the profile.
            // Keep the ownership lock until deletion finishes. Delete sharing lets
            // Windows remove this marker but still rejects any new reader/writer.
            using var ownership = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            Directory.Delete(candidate, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
                    AppLog.Warn($"ShutdownAsync: 仍有 {remainingUsers} 个标签持有浏览器环境，目录删除将重试");
                }

                // 2) Drop the static environment reference so the browser process can exit.
                string? ownedDirectory = null;
                await EnvironmentCreationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    ownedDirectory = _sharedUserDataDirectory;
                    _sharedEnvironment = null;
                    _sharedUserDataDirectory = null;
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
                else
                {
                    AppLog.Info("ShutdownAsync: 没有需要销毁的 WebView2 用户数据目录");
                }
            }
            finally
            {
                ShutdownLock.Release();
            }
            return SnapshotPendingDeletion().Length == 0;
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
                Directory.Delete(directory, true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
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
