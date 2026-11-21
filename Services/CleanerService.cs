using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace StealthWorkspace.Services;

/// <summary>
/// 清理类别。内部逻辑一律按枚举分派，不再依赖中文显示名称，
/// 避免调整界面文案后清理逻辑静默失效。
/// </summary>
public enum CleanerCategory
{
    ThumbnailCache,
    RecentAndJumpList,
    ExplorerSearchAndRunHistory,
    MediaAndRemoteHistory,
    RecycleBin,
    TempFiles,
    CrashDumpsAndWer,
    Clipboard,
    DnsCache,
    IsolatedDownloads
}

internal enum CleanOutcome { Complete, Skipped, Failed }

public sealed class CleanerOptions
{
    public bool RestartExplorerForThumbnails { get; set; }
    public bool DeepCleanTemp { get; set; }
    public bool SecureShredFiles { get; set; }
}

/// <summary>
/// 扫描线程构建独立结果。界面线程将结果应用到原有行，保留滚动与复选框焦点。
/// </summary>
public class CleanerItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    public CleanerCategory Category { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string IconGlyph { get; set; } = "\uE74D";

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>是否勾选清理（与 CheckBox 双向绑定）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Status { get; set; } = "待扫描";
    internal CleanOutcome Outcome { get; set; }

    public int DetectedCount { get; set; }

    public long DetectedSizeBytes { get; set; }

    /// <summary>该类别能否可靠统计占用体积（注册表记录 / 剪贴板 / DNS 缓存无法按字节统计）。</summary>
    public bool IsSizeKnown { get; set; } = true;

    /// <summary>扫描到的样本条目路径或注册表键名（用于详情预览，上限 60 条）。</summary>
    public IReadOnlyList<string> PreviewEntries { get; set; } = Array.Empty<string>();

    public string DisplaySize => IsSizeKnown
        ? CleanerService.FormatSize(DetectedSizeBytes)
        : (DetectedCount > 0 ? $"{DetectedCount} 条记录" : "—");

    public bool HasDetails => PreviewEntries.Count > 0;

    internal void ApplyScanResult(CleanerItem result)
    {
        bool displayChanged = Status != result.Status || DetectedCount != result.DetectedCount ||
            DetectedSizeBytes != result.DetectedSizeBytes || IsSizeKnown != result.IsSizeKnown ||
            HasDetails != result.HasDetails;
        Status = result.Status;
        Outcome = result.Outcome;
        DetectedCount = result.DetectedCount;
        DetectedSizeBytes = result.DetectedSizeBytes;
        IsSizeKnown = result.IsSizeKnown;
        PreviewEntries = result.PreviewEntries;
        if (displayChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// 痕迹清理服务。公开方法只做后台计算并返回新的结果列表，
/// 绝不修改传入的实例、也绝不触发任何 UI 通知。
/// </summary>
public class CleanerService
{
    private const int TempFileMinimumAgeHours = 24;
    private const int MaxPreviewEntries = 60;
    private const string ThumbnailSearchPattern = "thumbcache_*.db";
    private const string ExplorerProcessName = "explorer";
    private const string WebProfilePrefix = "Stealth_WebProfile_";

    private static readonly TimeSpan TempFileMinimumAge = TimeSpan.FromHours(TempFileMinimumAgeHours);
    private static readonly SemaphoreSlim CleanGate = new(1, 1);

    private static readonly string[] ExplorerHistoryKeyPaths =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU"
    };

    private const string OpenSavePidlMruPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU";

    private static readonly string[] MediaAndOfficeRecentKeyPaths =
    {
        @"Software\Microsoft\MediaPlayer\Player\RecentFileList",
        @"Software\Microsoft\Windows\CurrentVersion\Applets\Paint\Recent File List",
        @"Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List"
    };

    private const string TerminalServerDefaultPath = @"Software\Microsoft\Terminal Server Client\Default";
    private const string TerminalServerServersPath = @"Software\Microsoft\Terminal Server Client\Servers";

    public static CleanerService Instance { get; } = new();

    /// <summary>把字节数格式化为便于阅读的文本。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    /// <summary>创建默认清理类别（只构造数据，不访问磁盘）。</summary>
    public List<CleanerItem> GetDefaultCategories()
    {
        return new List<CleanerItem>
        {
            new()
            {
                Category = CleanerCategory.ThumbnailCache,
                GroupName = "系统使用痕迹",
                IconGlyph = "\uEB9F",
                Name = "Windows 资源管理器缩略图缓存",
                Description = "清理 Explorer 缩略图预览数据库（仅匹配 thumbcache_*.db，不清理 iconcache 图标缓存）"
            },
            new()
            {
                Category = CleanerCategory.RecentAndJumpList,
                GroupName = "系统使用痕迹",
                IconGlyph = "\uE81C",
                Name = "最近访问记录与任务栏跳转列表",
                Description = "清理“最近使用”快捷方式，以及 AutomaticDestinations / CustomDestinations 跳转列表文件"
            },
            new()
            {
                Category = CleanerCategory.ExplorerSearchAndRunHistory,
                GroupName = "系统使用痕迹",
                IconGlyph = "\uE721",
                Name = "资源管理器搜索、地址栏与运行历史",
                Description = "清理搜索框关键词 (WordWheelQuery)、地址栏输入路径、Win+R 运行记录及文件打开/保存对话框历史",
                IsSizeKnown = false
            },
            new()
            {
                Category = CleanerCategory.MediaAndRemoteHistory,
                GroupName = "系统使用痕迹",
                IconGlyph = "\uE714",
                Name = "系统播放器、画图与远程桌面历史",
                Description = "清理 Windows Media Player、画图、写字板最近文件记录及远程桌面 (mstsc) 连接历史",
                IsSizeKnown = false
            },
            new()
            {
                Category = CleanerCategory.RecycleBin,
                GroupName = "文件与存储缓存",
                IconGlyph = "\uE74D",
                Name = "Windows 回收站",
                Description = "永久清空回收站中已删除但仍可被还原的文件与目录"
            },
            new()
            {
                Category = CleanerCategory.TempFiles,
                GroupName = "文件与存储缓存",
                IconGlyph = "\uE8B7",
                Name = "用户临时文件目录 (%TEMP%)",
                Description = "默认清理 %TEMP% 根目录中 24 小时前的文件；开启深度模式后可清理近期文件与子目录（自动跳过运行中的浏览器沙箱）"
            },
            new()
            {
                Category = CleanerCategory.CrashDumpsAndWer,
                GroupName = "文件与存储缓存",
                IconGlyph = "\uE7BA",
                Name = "崩溃转储与 Windows 错误报告",
                Description = "清理 %LOCALAPPDATA%\\CrashDumps 内存转储文件及 WER 错误报告归档"
            },
            new()
            {
                Category = CleanerCategory.Clipboard,
                GroupName = "网络与剪贴板",
                IconGlyph = "\uE77F",
                Name = "Windows 剪贴板内容与历史记录",
                Description = "清空系统剪贴板内容及 Win + V 剪贴板历史（系统未开启历史记录时仅清空当前内容）",
                IsSizeKnown = false
            },
            new()
            {
                Category = CleanerCategory.DnsCache,
                GroupName = "网络与剪贴板",
                IconGlyph = "\uE774",
                Name = "系统 DNS 解析缓存",
                Description = "调用系统接口刷新并清空本机域名解析缓存 (DnsFlushResolverCache)",
                IsSizeKnown = false
            },
            new()
            {
                Category = CleanerCategory.IsolatedDownloads,
                GroupName = "应用沙箱",
                IconGlyph = "\uE896",
                Name = "工作台沙箱隔离下载文件",
                Description = "清理浏览器在受保护隔离下载目录中保存的文件（默认不勾选，避免误删刚下载的文件）",
                IsSelected = false
            }
        };
    }

    public Task<List<CleanerItem>> ScanAsync(
        IReadOnlyList<CleanerItem> items,
        IProgress<string>? progress = null,
        CleanerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        CleanerOptions effectiveOptions = options ?? new CleanerOptions();

        return Task.Run(() =>
        {
            var results = new List<CleanerItem>(items.Count);
            foreach (CleanerItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report("正在扫描：" + item.Name);
                    results.Add(ScanItem(item, effectiveOptions, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Error($"扫描清理类别 {item.Category} 时发生异常", ex);
                    results.Add(CreateResult(item, "扫描发生内部错误，可稍后重试", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }, cancellationToken);
    }

    public Task<List<CleanerItem>> CleanAsync(
        IReadOnlyList<CleanerItem> items,
        bool restartExplorerForThumbnails,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return CleanAsync(
            items,
            new CleanerOptions { RestartExplorerForThumbnails = restartExplorerForThumbnails },
            progress, cancellationToken);
    }

    public async Task<List<CleanerItem>> CleanAsync(
        IReadOnlyList<CleanerItem> items,
        CleanerOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        SynchronizationContext? uiContext = SynchronizationContext.Current;
        await CleanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(CleanSelectedAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanGate.Release();
        }

        async Task<List<CleanerItem>> CleanSelectedAsync()
        {
            var results = new List<CleanerItem>(items.Count);
            foreach (CleanerItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.IsSelected)
                {
                    results.Add(CreateResult(item, "未勾选，已跳过", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries));
                    continue;
                }

                try
                {
                    progress?.Report("正在清理：" + item.Name);
                    cancellationToken.ThrowIfCancellationRequested();

                    CleanerItem result = item.Category switch
                    {
                        CleanerCategory.ThumbnailCache => CleanThumbnails(item, GetExplorerCacheDirectory(), options, cancellationToken),
                        CleanerCategory.RecentAndJumpList => CleanRecent(item, options.SecureShredFiles, cancellationToken),
                        CleanerCategory.ExplorerSearchAndRunHistory => CleanExplorerHistory(item, cancellationToken),
                        CleanerCategory.MediaAndRemoteHistory => CleanMediaAndRemoteHistory(item, cancellationToken),
                        CleanerCategory.RecycleBin => CleanRecycleBin(item, cancellationToken),
                        CleanerCategory.TempFiles => CleanTemp(item, options.DeepCleanTemp, options.SecureShredFiles, cancellationToken),
                        CleanerCategory.CrashDumpsAndWer => CleanCrashDumpsAndWer(item, options.SecureShredFiles, cancellationToken),
                        CleanerCategory.Clipboard => await CleanClipboardAsync(item, uiContext, cancellationToken).ConfigureAwait(false),
                        CleanerCategory.DnsCache => CleanDns(item),
                        CleanerCategory.IsolatedDownloads => CleanIsolatedDownloads(item, options.SecureShredFiles, cancellationToken),
                        _ => CreateResult(item, "未知类别，已跳过", item.DetectedCount, item.DetectedSizeBytes, outcome: CleanOutcome.Skipped)
                    };

                    results.Add(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Error($"清理类别 {item.Category} 时发生异常", ex);
                    results.Add(CreateResult(item, "清理发生内部错误，可稍后重试", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries, CleanOutcome.Failed));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }
    }

    internal async Task WaitForCleaningToFinishAsync()
    {
        await CleanGate.WaitAsync().ConfigureAwait(false);
        CleanGate.Release();
    }

    // ---------------------------------------------------------------------
    // 扫描
    // ---------------------------------------------------------------------

    private static CleanerItem ScanItem(CleanerItem source, CleanerOptions options, CancellationToken cancellationToken)
    {
        return source.Category switch
        {
            CleanerCategory.ThumbnailCache => ScanThumbnails(source, cancellationToken),
            CleanerCategory.RecentAndJumpList => ScanRecent(source, cancellationToken),
            CleanerCategory.ExplorerSearchAndRunHistory => ScanExplorerHistory(source),
            CleanerCategory.MediaAndRemoteHistory => ScanMediaAndRemoteHistory(source),
            CleanerCategory.RecycleBin => ScanRecycleBin(source),
            CleanerCategory.TempFiles => ScanTemp(source, options.DeepCleanTemp, cancellationToken),
            CleanerCategory.CrashDumpsAndWer => ScanCrashDumpsAndWer(source, cancellationToken),
            CleanerCategory.Clipboard => CreateResult(source, "可直接清空（无法预先统计大小）", 0, 0, false),
            CleanerCategory.DnsCache => CreateResult(source, "可直接刷新（无法预先统计缓存条目）", 0, 0, false),
            CleanerCategory.IsolatedDownloads => ScanIsolatedDownloads(source, cancellationToken),
            _ => CreateResult(source, "未知类别，已跳过", 0, 0)
        };
    }

    private static CleanerItem ScanThumbnails(CleanerItem source, CancellationToken cancellationToken)
    {
        string directory = GetExplorerCacheDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return CreateResult(source, "无需清理（系统未提供缩略图缓存目录）", 0, 0);
        }

        string[] files = GetThumbnailCacheFiles(directory, out bool enumerationSucceeded, cancellationToken);
        if (!enumerationSucceeded)
        {
            return CreateResult(source, "无法读取缩略图缓存目录（权限不足）", source.DetectedCount, source.DetectedSizeBytes);
        }

        if (files.Length == 0)
        {
            return CreateResult(source, "无需清理（未发现缩略图缓存文件）", 0, 0);
        }

        long size = SumFileSizes(files, cancellationToken);
        return CreateResult(source, $"已发现 {files.Length} 个缩略图缓存文件", files.Length, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanRecent(CleanerItem source, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateRecentFiles(out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "“最近”目录部分内容无法访问，未能完整扫描"
                : "无需清理（未发现最近记录或跳转列表文件）";
            return CreateResult(source, emptyStatus, 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        long size = SumFileSizes(files, cancellationToken);
        string status = $"已发现 {files.Count} 条最近记录与跳转列表文件";
        if (enumerationFailures > 0) status += "（部分内容无法访问）";

        return CreateResult(source, status, files.Count, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanExplorerHistory(CleanerItem source)
    {
        var previews = new List<string>();
        int count = CountExplorerHistoryEntries(previews);
        if (count == 0)
        {
            return CreateResult(source, "无需清理（未发现搜索、地址栏或运行历史）", 0, 0, false);
        }

        return CreateResult(source, $"已发现 {count} 条搜索 / 路径 / 对话框历史", count, 0, false, previews);
    }

    private static CleanerItem ScanMediaAndRemoteHistory(CleanerItem source)
    {
        var previews = new List<string>();
        int count = CountMediaAndRemoteHistoryEntries(previews);
        if (count == 0)
        {
            return CreateResult(source, "无需清理（未发现播放器或远程桌面历史）", 0, 0, false);
        }

        return CreateResult(source, $"已发现 {count} 条媒体 / 应用 / 远程桌面历史", count, 0, false, previews);
    }

    private static CleanerItem ScanRecycleBin(CleanerItem source)
    {
        var info = new NativeMethods.SHQUERYRBINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
        };

        try
        {
            int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            if (hr != 0)
            {
                AppLog.Warn($"SHQueryRecycleBin 返回 HRESULT: 0x{hr:X8}");
                return CreateResult(source, "无法查询回收站状态", 0, 0);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("查询回收站失败", ex);
            return CreateResult(source, "查询回收站异常", 0, 0);
        }

        int count = info.i64NumItems > int.MaxValue ? int.MaxValue : (int)Math.Max(0, info.i64NumItems);
        long bytes = Math.Max(0, info.i64Size);

        if (count == 0 && bytes == 0)
        {
            return CreateResult(source, "无需清理（回收站为空）", 0, 0);
        }

        var preview = new[] { $"回收站内共 {count} 个项目，占用 {FormatSize(bytes)}" };
        return CreateResult(source, $"已发现 {count} 个回收站项目", count, bytes, true, preview);
    }

    private static CleanerItem ScanTemp(CleanerItem source, bool deepClean, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateEligibleTempFiles(deepClean, out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "临时目录部分内容无法访问，未能完整扫描"
                : (deepClean ? "无需清理（未发现可清理的临时文件）" : "无需清理（没有 24 小时前生成的临时文件）");
            return CreateResult(source, emptyStatus, 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        long size = SumFileSizes(files, cancellationToken);
        string status = deepClean
            ? $"已发现 {files.Count} 个临时文件（含近期与子目录）"
            : $"已发现 {files.Count} 个 24 小时前生成的临时文件";
        if (enumerationFailures > 0) status += "（部分内容无法访问）";

        return CreateResult(source, status, files.Count, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanCrashDumpsAndWer(CleanerItem source, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateCrashAndWerFiles(out int failures, cancellationToken);
        if (files.Count == 0)
        {
            string status = failures > 0
                ? "错误报告目录部分内容无法访问"
                : "无需清理（未发现崩溃转储或错误报告）";
            return CreateResult(source, status, 0, 0);
        }

        long size = SumFileSizes(files, cancellationToken);
        return CreateResult(source, $"已发现 {files.Count} 个转储与错误报告文件", files.Count, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanIsolatedDownloads(CleanerItem source, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateIsolatedDownloads(out int failures, cancellationToken);
        if (files.Count == 0)
        {
            string status = failures > 0
                ? "隔离下载目录无法访问"
                : "无需清理（沙箱隔离下载目录为空）";
            return CreateResult(source, status, 0, 0);
        }

        long size = SumFileSizes(files, cancellationToken);
        return CreateResult(source, $"已发现 {files.Count} 个沙箱隔离下载文件", files.Count, size, true, BuildPreviewList(files));
    }

    // ---------------------------------------------------------------------
    // 清理
    // ---------------------------------------------------------------------

    internal static CleanerItem CleanThumbnails(CleanerItem source, string directory, CleanerOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return CreateResult(source, "无需清理（系统未提供缩略图缓存目录）", 0, 0);
        }

        string[] files = GetThumbnailCacheFiles(directory, out bool enumerationSucceeded, cancellationToken);
        if (!enumerationSucceeded)
        {
            return CreateResult(source, "无法读取缩略图缓存目录，未执行删除", source.DetectedCount, source.DetectedSizeBytes, outcome: CleanOutcome.Failed);
        }

        if (files.Length == 0)
        {
            return CreateResult(source, "无需清理（未发现缩略图缓存文件）", 0, 0);
        }

        var sizes = new long[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { sizes[i] = new FileInfo(files[i]).Length; }
            catch (Exception ex)
            {
                sizes[i] = 0;
                AppLog.Warn("读取缩略图缓存文件大小失败：" + ex.GetType().Name);
            }
        }

        var deletedFlags = new bool[files.Length];
        int deleted = 0;
        int locked = 0;
        int failed = 0;
        int accessDenied = 0;
        bool explorerStopped = false;
        bool killedExplorer = false;
        bool restartSucceeded = true;

        try
        {
            if (options.RestartExplorerForThumbnails)
            {
                explorerStopped = TryStopExplorer(cancellationToken, ref killedExplorer);
            }
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryDeleteOrShred(files[i], options.SecureShredFiles, out bool isBusy, out bool denied))
                {
                    deletedFlags[i] = true;
                    deleted++;
                }
                else if (isBusy)
                {
                    locked++;
                }
                else
                {
                    failed++;
                    if (denied) accessDenied++;
                }
            }
        }
        finally
        {
            if (killedExplorer)
            {
                restartSucceeded = EnsureExplorerRunning();
                if (!restartSucceeded)
                {
                    AppLog.Error("重启资源管理器失败", null);
                }
            }
        }

        int remainingCount = 0;
        long remainingSize = 0;
        var remainingFiles = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            if (deletedFlags[i]) continue;
            remainingCount++;
            remainingSize += sizes[i];
            remainingFiles.Add(files[i]);
        }

        var parts = new List<string>();
        if (deleted > 0) parts.Add($"已删除 {deleted} 个缩略图缓存文件" + (options.SecureShredFiles ? "（已覆写粉碎）" : string.Empty));
        if (locked > 0)
        {
            parts.Add(options.RestartExplorerForThumbnails
                ? $"{locked} 个文件仍被占用或为硬链接已跳过" + (explorerStopped ? string.Empty : "（资源管理器未能完全退出）")
                : $"{locked} 个文件被占用或为硬链接，已跳过");
        }
        if (failed > 0)
        {
            parts.Add(accessDenied > 0
                ? $"{failed} 个文件删除失败（权限不足或被系统/安全软件拦截）"
                : $"{failed} 个文件删除失败");
        }
        if (parts.Count == 0) parts.Add("没有删除任何文件");

        string status = string.Join("；", parts);
        if (remainingCount == 0) status += "；缩略图缓存已全部清除";
        if (killedExplorer)
        {
            status += restartSucceeded ? "；资源管理器已恢复运行" : "；资源管理器重启失败，请手动重新打开";
        }

        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles), CleanState(failed > 0 || !restartSucceeded, locked > 0));
    }

    private static CleanerItem CleanRecent(CleanerItem source, bool shred, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateRecentFiles(out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "“最近”目录部分内容无法访问，未执行删除"
                : "无需清理（未发现最近记录或跳转列表文件）";
            return CreateResult(source, emptyStatus, 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed, out int accessDenied, cancellationToken);

        List<string> remainingFiles = EnumerateRecentFiles(out int refreshFailures, cancellationToken);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles, cancellationToken);

        string status = BuildBatchStatus("最近记录 / 跳转列表文件", deleted, skipped, failed, accessDenied, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles), CleanState(failed + refreshFailures + enumerationFailures > 0, skipped > 0));
    }

    private static CleanerItem CleanExplorerHistory(CleanerItem source, CancellationToken cancellationToken)
    {
        int cleared = 0;
        int failed = 0;

        foreach (string keyPath in ExplorerHistoryKeyPaths)
        {
            ClearRegistryValues(keyPath, ref cleared, ref failed, cancellationToken);
        }

        try
        {
            using RegistryKey? openSaveKey = Registry.CurrentUser.OpenSubKey(OpenSavePidlMruPath, writable: true);
            if (openSaveKey != null) ClearOpenSaveHistory(openSaveKey, ref cleared, ref failed, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            AppLog.Error("清理 OpenSavePidlMRU 失败", ex);
        }

        var remainingPreview = new List<string>();
        int remaining = CountExplorerHistoryEntries(remainingPreview);

        string status = cleared > 0
            ? $"已清除 {cleared} 条资源管理器历史" + (failed > 0 ? $"（{failed} 项失败）" : string.Empty)
            : (failed > 0 ? "部分注册表历史清理失败（权限受限）" : "无需清理（未发现历史记录）");

        return CreateResult(source, status, remaining, 0, false, remainingPreview, CleanState(failed > 0, remaining > 0));
    }

    private static CleanerItem CleanMediaAndRemoteHistory(CleanerItem source, CancellationToken cancellationToken)
    {
        int cleared = 0;
        int failed = 0;

        foreach (string keyPath in MediaAndOfficeRecentKeyPaths)
        {
            ClearRegistryValues(keyPath, ref cleared, ref failed, cancellationToken);
        }

        // Terminal Server Client Default MRU*
        try
        {
            using RegistryKey? rdpDef = Registry.CurrentUser.OpenSubKey(TerminalServerDefaultPath, writable: true);
            if (rdpDef != null)
            {
                foreach (string valName in rdpDef.GetValueNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (valName.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            rdpDef.DeleteValue(valName, throwOnMissingValue: false);
                            cleared++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            AppLog.Warn("删除远程桌面 MRU 失败", ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            AppLog.Error("清理远程桌面 Default 历史失败", ex);
        }

        // Terminal Server Client Servers subkeys
        try
        {
            using RegistryKey? rdpServers = Registry.CurrentUser.OpenSubKey(TerminalServerServersPath, writable: true);
            if (rdpServers != null)
            {
                foreach (string sub in rdpServers.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        rdpServers.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                        cleared++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AppLog.Warn("删除远程桌面服务器记录失败", ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            AppLog.Error("清理远程桌面 Servers 历史失败", ex);
        }

        var remainingPreview = new List<string>();
        int remaining = CountMediaAndRemoteHistoryEntries(remainingPreview);

        string status = cleared > 0
            ? $"已清除 {cleared} 条媒体 / 应用 / 远程桌面历史" + (failed > 0 ? $"（{failed} 项失败）" : string.Empty)
            : (failed > 0 ? "部分历史清理失败" : "无需清理（未发现历史记录）");

        return CreateResult(source, status, remaining, 0, false, remainingPreview, CleanState(failed > 0, remaining > 0));
    }

    private static CleanerItem CleanRecycleBin(CleanerItem source, CancellationToken cancellationToken)
    {
        CleanerItem preScan = ScanRecycleBin(source);
        if (preScan.DetectedCount == 0 && preScan.DetectedSizeBytes == 0)
        {
            return preScan;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            uint flags = NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
            if (hr != 0)
            {
                AppLog.Warn($"SHEmptyRecycleBin 返回 HRESULT: 0x{hr:X8}");
                CleanerItem afterFail = ScanRecycleBin(source);
                return CreateResult(source, $"清空回收站未完全成功 (0x{hr:X8})", afterFail.DetectedCount, afterFail.DetectedSizeBytes, true, afterFail.PreviewEntries, CleanOutcome.Failed);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("清空回收站异常", ex);
            return CreateResult(source, "清空回收站失败：" + ex.Message, source.DetectedCount, source.DetectedSizeBytes, true, source.PreviewEntries, CleanOutcome.Failed);
        }

        CleanerItem postScan = ScanRecycleBin(source);
        string status = postScan.DetectedCount == 0
            ? $"已彻底清空回收站（释放约 {FormatSize(preScan.DetectedSizeBytes)}）"
            : $"部分清空，回收站仍剩 {postScan.DetectedCount} 项";

        return CreateResult(source, status, postScan.DetectedCount, postScan.DetectedSizeBytes, true, postScan.PreviewEntries, CleanState(false, postScan.DetectedCount > 0));
    }

    private static CleanerItem CleanTemp(CleanerItem source, bool deepClean, bool shred, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateEligibleTempFiles(deepClean, out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "临时目录部分内容无法访问，未执行删除"
                : (deepClean ? "无需清理（未发现可清理的临时文件）" : "无需清理（没有 24 小时前生成的临时文件）");
            return CreateResult(source, emptyStatus, 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        DateTime cutoff = DateTime.UtcNow - TempFileMinimumAge;
        int deleted = 0;
        int skippedBusy = 0;
        int skippedTooNew = 0;
        int failed = 0;
        int accessDenied = 0;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                if (!deepClean && info.LastWriteTimeUtc >= cutoff)
                {
                    skippedTooNew++;
                    continue;
                }

                if (TryDeleteOrShred(file, shred, out bool isBusy, out bool denied))
                {
                    deleted++;
                }
                else if (isBusy)
                {
                    skippedBusy++;
                }
                else
                {
                    failed++;
                    if (denied) accessDenied++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error("删除临时文件失败", ex);
            }
        }

        if (deepClean)
        {
            TryCleanupEmptyTempSubdirectories(cancellationToken);
        }

        List<string> remainingFiles = EnumerateEligibleTempFiles(deepClean, out int refreshFailures, cancellationToken);
        int remainingCount = refreshFailures == 0 ? remainingFiles.Count : Math.Max(0, files.Count - deleted);
        long remainingSize = refreshFailures == 0 ? SumFileSizes(remainingFiles, cancellationToken) : 0;

        var parts = new List<string>();
        if (deleted > 0) parts.Add($"已删除 {deleted} 个临时文件" + (shred ? "（已覆写粉碎）" : string.Empty));
        if (skippedBusy > 0) parts.Add($"{skippedBusy} 个文件被占用或为硬链接，已跳过");
        if (skippedTooNew > 0) parts.Add($"{skippedTooNew} 个文件在清理时被更新，已跳过");
        if (failed > 0)
        {
            parts.Add(accessDenied > 0
                ? $"{failed} 个文件删除失败（权限不足或被系统/安全软件拦截）"
                : $"{failed} 个文件删除失败");
        }
        if (parts.Count == 0) parts.Add("没有删除任何文件");

        string status = string.Join("；", parts);
        if (remainingCount > 0) status += $"；仍有 {remainingCount} 个临时文件保留";
        if (enumerationFailures > 0 || refreshFailures > 0) status += "；部分临时文件无法访问，结果可能不完整";

        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles), CleanState(failed + enumerationFailures + refreshFailures > 0, skippedBusy + skippedTooNew > 0));
    }

    private static CleanerItem CleanCrashDumpsAndWer(CleanerItem source, bool shred, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateCrashAndWerFiles(out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            return CreateResult(source, enumerationFailures > 0 ? "转储目录无法完整访问，未发现可清理的文件" : "无需清理（未发现崩溃转储或错误报告）", 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed, out int accessDenied, cancellationToken);

        List<string> remainingFiles = EnumerateCrashAndWerFiles(out int refreshFailures, cancellationToken);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles, cancellationToken);

        string status = BuildBatchStatus("转储 / 错误报告文件", deleted, skipped, failed, accessDenied, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles), CleanState(failed + enumerationFailures + refreshFailures > 0, skipped > 0));
    }

    private static CleanerItem CleanIsolatedDownloads(CleanerItem source, bool shred, CancellationToken cancellationToken)
    {
        List<string> files = EnumerateIsolatedDownloads(out int enumerationFailures, cancellationToken);
        if (files.Count == 0)
        {
            return CreateResult(source, enumerationFailures > 0 ? "隔离下载目录无法完整访问，未发现可清理的文件" : "无需清理（沙箱隔离下载目录为空）", 0, 0, outcome: CleanState(enumerationFailures > 0, false));
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed, out int accessDenied, cancellationToken);

        List<string> remainingFiles = EnumerateIsolatedDownloads(out int refreshFailures, cancellationToken);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles, cancellationToken);

        string status = BuildBatchStatus("沙箱隔离下载文件", deleted, skipped, failed, accessDenied, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles), CleanState(failed + enumerationFailures + refreshFailures > 0, skipped > 0));
    }

    private static async Task<CleanerItem> CleanClipboardAsync(CleanerItem source, SynchronizationContext? uiContext, CancellationToken cancellationToken)
    {
        bool clipboardCleared = false;
        bool historyCleared = false;
        bool historyUnavailable = false;

        void ClearNow()
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
                clipboardCleared = true;
            }
            catch (Exception ex)
            {
                AppLog.Error("清空系统剪贴板失败", ex);
            }

            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                historyCleared = Windows.ApplicationModel.DataTransfer.Clipboard.ClearHistory();
                historyUnavailable = !Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled();
            }
            catch (Exception ex)
            {
                historyUnavailable = true;
                AppLog.Warn("清空剪贴板历史记录失败：" + ex.GetType().Name);
            }
        }

        if (uiContext is not null)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int startedOrAbandoned = 0;
            try
            {
                uiContext.Post(_ =>
                {
                    if (Interlocked.Exchange(ref startedOrAbandoned, 1) != 0) return;
                    try { ClearNow(); }
                    finally { completion.TrySetResult(true); }
                }, null);

                await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("等待界面线程清空剪贴板超时");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error("无法把剪贴板清理调度到界面线程", ex);
            }
            finally
            {
                // A queued callback must not clear the clipboard after the cleanup has ended.
                if (Interlocked.Exchange(ref startedOrAbandoned, 1) != 0)
                    await completion.Task.ConfigureAwait(false);
            }
        }
        else
        {
            ClearNow();
        }

        string status = (clipboardCleared, historyCleared, historyUnavailable) switch
        {
            (true, true, _) => "剪贴板内容与历史记录已清空",
            (true, false, true) => "剪贴板已清空；剪贴板历史记录不可用或未开启",
            (true, false, false) => "剪贴板已清空；历史记录清理失败",
            _ => "剪贴板清空失败，可能被其他程序短暂占用，可稍后重试"
        };

        return CreateResult(source, status, clipboardCleared ? 0 : source.DetectedCount, 0, false, outcome: CleanState(!clipboardCleared || (!historyCleared && !historyUnavailable), false));
    }

    private static CleanerItem CleanDns(CleanerItem source)
    {
        int flushResult;
        try
        {
            flushResult = NativeMethods.DnsFlushResolverCache();
        }
        catch (Exception ex)
        {
            AppLog.Error("调用 DnsFlushResolverCache 失败", ex);
            return CreateResult(source, "DNS 刷新失败（系统调用异常），可稍后重试", source.DetectedCount, 0, false, outcome: CleanOutcome.Failed);
        }

        return flushResult != 0
            ? CreateResult(source, "DNS 解析缓存已刷新并清空", 0, 0, false)
            : CreateResult(source, "DNS 缓存刷新未生效（系统返回失败），可稍后重试", source.DetectedCount, 0, false, outcome: CleanOutcome.Failed);
    }

    // ---------------------------------------------------------------------
    // 通用工具
    // ---------------------------------------------------------------------

    private static CleanOutcome CleanState(bool incomplete, bool skipped) =>
        incomplete ? CleanOutcome.Failed : skipped ? CleanOutcome.Skipped : CleanOutcome.Complete;

    private static CleanerItem CreateResult(
        CleanerItem source,
        string status,
        int detectedCount,
        long detectedSizeBytes,
        bool? isSizeKnown = null,
        IReadOnlyList<string>? previewEntries = null,
        CleanOutcome outcome = CleanOutcome.Complete)
    {
        return new CleanerItem
        {
            Category = source.Category,
            GroupName = source.GroupName,
            IconGlyph = source.IconGlyph,
            Name = source.Name,
            Description = source.Description,
            IsSelected = source.IsSelected,
            Status = status,
            Outcome = outcome,
            DetectedCount = detectedCount < 0 ? 0 : detectedCount,
            DetectedSizeBytes = detectedSizeBytes < 0 ? 0 : detectedSizeBytes,
            IsSizeKnown = isSizeKnown ?? source.IsSizeKnown,
            PreviewEntries = previewEntries ?? Array.Empty<string>()
        };
    }

    private static IReadOnlyList<string> BuildPreviewList(IEnumerable<string> paths)
    {
        return paths.Take(MaxPreviewEntries).ToList();
    }

    private static void DeleteFilesBatch(
        IEnumerable<string> files,
        bool shred,
        out int deleted,
        out int skipped,
        out int failed,
        out int accessDenied,
        CancellationToken cancellationToken)
    {
        deleted = 0;
        skipped = 0;
        failed = 0;
        accessDenied = 0;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteOrShred(file, shred, out bool isBusy, out bool denied))
            {
                deleted++;
            }
            else if (isBusy)
            {
                skipped++;
            }
            else
            {
                failed++;
                if (denied) accessDenied++;
            }
        }
    }

    private static bool TryDeleteOrShred(string file, bool shred, out bool isBusy, out bool isAccessDenied)
    {
        isBusy = false;
        isAccessDenied = false;
        try
        {
            var attributes = File.GetAttributes(file);
            if (!SafeFileSystem.IsPlainPath(file)) return false;
            if (shred)
            {
                if (SafeShred.OverwriteAndDelete(file, SettingsService.Instance.Data.ShredPasses)) return true;
                isBusy = SafeShred.IsHardLink(file);
                return false;
            }
            if ((attributes & FileAttributes.ReadOnly) != 0) SafeFileSystem.DeleteReadOnlyFile(file);
            else File.Delete(file);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            int error = ex is Win32Exception win32 ? win32.NativeErrorCode : ex.HResult & 0xffff;
            if (error is 2 or 3) return true;
            isBusy = error is 32 or 33;
            isAccessDenied = error == 5;
            if (!isBusy) AppLog.Warn($"删除文件失败，错误码 {error}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("删除文件失败", ex);
            return false;
        }
    }

    private static string BuildBatchStatus(
        string unitLabel,
        int deleted,
        int skipped,
        int failed,
        int accessDenied,
        int remainingCount,
        bool hadEnumErrors)
    {
        var parts = new List<string>();
        if (deleted > 0) parts.Add($"已删除 {deleted} 个{unitLabel}");
        if (skipped > 0) parts.Add($"{skipped} 个文件被占用或为硬链接，已跳过");
        if (failed > 0)
        {
            parts.Add(accessDenied > 0
                ? $"{failed} 个文件删除失败（权限不足或被系统/安全软件拦截）"
                : $"{failed} 个文件删除失败");
        }
        if (parts.Count == 0) parts.Add("没有删除任何文件");

        string status = string.Join("；", parts);
        if (remainingCount > 0) status += $"；仍有 {remainingCount} 个文件保留";
        if (hadEnumErrors) status += "；部分目录无法访问，结果可能不完整";
        return status;
    }

    private static int CountExplorerHistoryEntries(List<string> previews)
    {
        int total = 0;
        foreach (string keyPath in ExplorerHistoryKeyPaths)
        {
            total += CountRegistryValues(keyPath, previews);
        }

        try
        {
            using RegistryKey? openSaveKey = Registry.CurrentUser.OpenSubKey(OpenSavePidlMruPath, writable: false);
            if (openSaveKey != null)
            {
                foreach (string subName in openSaveKey.GetSubKeyNames())
                {
                    total += CountRegistryValues($@"{OpenSavePidlMruPath}\{subName}", previews);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("扫描 OpenSavePidlMRU 失败", ex);
        }

        return total;
    }

    private static int CountMediaAndRemoteHistoryEntries(List<string> previews)
    {
        int total = 0;
        foreach (string keyPath in MediaAndOfficeRecentKeyPaths)
        {
            total += CountRegistryValues(keyPath, previews);
        }

        try
        {
            using RegistryKey? rdpDef = Registry.CurrentUser.OpenSubKey(TerminalServerDefaultPath, writable: false);
            if (rdpDef != null)
            {
                foreach (string valName in rdpDef.GetValueNames())
                {
                    if (valName.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                    {
                        total++;
                        if (previews.Count < MaxPreviewEntries)
                        {
                            object? val = rdpDef.GetValue(valName);
                            previews.Add($@"HKCU\{TerminalServerDefaultPath}\{valName} = {val}");
                        }
                    }
                }
            }

            using RegistryKey? rdpServers = Registry.CurrentUser.OpenSubKey(TerminalServerServersPath, writable: false);
            if (rdpServers != null)
            {
                foreach (string sub in rdpServers.GetSubKeyNames())
                {
                    total++;
                    if (previews.Count < MaxPreviewEntries)
                    {
                        previews.Add($@"HKCU\{TerminalServerServersPath}\{sub}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("扫描远程桌面历史失败", ex);
        }

        return total;
    }

    private static int CountRegistryValues(string subKeyPath, List<string> previews)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            if (key == null) return 0;

            string[] names = key.GetValueNames();
            int count = 0;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                count++;
                if (previews.Count < MaxPreviewEntries)
                {
                    object? raw = key.GetValue(name);
                    string valDesc = raw is string s ? $" = {s}" : string.Empty;
                    previews.Add($@"HKCU\{subKeyPath}\{name}{valDesc}");
                }
            }
            return count;
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取注册表失败", ex);
            return 0;
        }
    }

    internal static void ClearOpenSaveHistory(RegistryKey parent, ref int cleared, ref int failed, CancellationToken token)
    {
        foreach (string name in parent.GetSubKeyNames())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var key = parent.OpenSubKey(name, writable: true);
                if (key == null) continue;
                ClearNamedValues(key, ref cleared, ref failed, token);
                // Keep the global bucket, default values and any unknown nested data.
                if (name == "*" || key.ValueCount != 0 || key.SubKeyCount != 0) continue;
                token.ThrowIfCancellationRequested();
                parent.DeleteSubKey(name, throwOnMissingSubKey: false);
                cleared++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                AppLog.Warn("删除文件选择历史分组失败", ex);
            }
        }
    }

    private static void ClearRegistryValues(string subKeyPath, ref int cleared, ref int failed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
            if (key != null) ClearNamedValues(key, ref cleared, ref failed, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            AppLog.Warn("打开注册表失败", ex);
        }
    }

    private static void ClearNamedValues(RegistryKey key, ref int cleared, ref int failed, CancellationToken token)
    {
        foreach (string name in key.GetValueNames())
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                cleared++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn("删除注册表值失败", ex);
            }
        }
    }

    private static string GetExplorerCacheDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return string.Empty;
        return Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
    }

    private static string[] GetThumbnailCacheFiles(string directory, out bool success, CancellationToken cancellationToken)
    {
        if (!SafeFileSystem.IsPlainPath(directory))
        {
            success = false;
            return Array.Empty<string>();
        }
        try
        {
            var files = new List<string>();
            foreach (string path in Directory.EnumerateFiles(directory, ThumbnailSearchPattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SafeFileSystem.IsPlainPath(path)) files.Add(path);
            }
            success = true;
            return files.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("枚举缩略图缓存目录失败", ex);
            success = false;
            return Array.Empty<string>();
        }
    }

    private static string GetRecentDirectory()
    {
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!string.IsNullOrEmpty(recent)) return recent;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return string.Empty;
        return Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Recent");
    }

    private static List<string> EnumerateRecentFiles(out int enumerationFailures, CancellationToken cancellationToken)
    {
        enumerationFailures = 0;
        var files = new List<string>();
        string root = GetRecentDirectory();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return files;
        }
        if (!SafeFileSystem.IsPlainPath(root))
        {
            enumerationFailures++;
            return files;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsShellMetadataFile(file) || !SafeFileSystem.IsPlainPath(file)) continue;
                files.Add(file);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            enumerationFailures++;
            AppLog.Error("枚举“最近使用”目录失败", ex);
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            enumerationFailures++;
            AppLog.Error("枚举“最近使用”子目录失败", ex);
            return files;
        }

        foreach (string subDirectory in subDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!SafeFileSystem.IsPlainPath(subDirectory)) continue;

                foreach (string file in Directory.EnumerateFiles(subDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsShellMetadataFile(file) || !SafeFileSystem.IsPlainPath(file)) continue;
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                enumerationFailures++;
                AppLog.Error("枚举“最近使用”子目录文件失败", ex);
            }
        }

        return files;
    }

    private static bool IsShellMetadataFile(string path)
    {
        return string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateEligibleTempFiles(bool deepClean, out int enumerationFailures, CancellationToken cancellationToken)
    {
        enumerationFailures = 0;
        var eligible = new List<string>();

        string tempDirectory;
        try
        {
            tempDirectory = Path.GetTempPath();
        }
        catch (Exception ex)
        {
            enumerationFailures++;
            AppLog.Error("获取 %TEMP% 路径失败", ex);
            return eligible;
        }

        if (string.IsNullOrEmpty(tempDirectory) || !Directory.Exists(tempDirectory))
        {
            return eligible;
        }

        DateTime cutoff = DateTime.UtcNow - TempFileMinimumAge;
        var files = SafeFileSystem.EnumerateFiles(tempDirectory, deepClean, out enumerationFailures,
            dir => Path.GetFileName(dir).StartsWith(WebProfilePrefix, StringComparison.OrdinalIgnoreCase), cancellationToken);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (deepClean || File.GetLastWriteTimeUtc(file) < cutoff) eligible.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { enumerationFailures++; }
        }

        return eligible;
    }

    private static void TryCleanupEmptyTempSubdirectories(CancellationToken cancellationToken)
    {
        try
        {
            string tempDirectory = Path.GetTempPath();
            foreach (string subDir in Directory.EnumerateDirectories(tempDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(subDir);
                if (dirName.StartsWith(WebProfilePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (!SafeFileSystem.IsPlainPath(subDir)) continue;
                    if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        Directory.Delete(subDir, false);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<string> EnumerateCrashAndWerFiles(out int enumerationFailures, CancellationToken cancellationToken)
    {
        enumerationFailures = 0;
        var files = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return files;

        string[] candidateDirs =
        {
            Path.Combine(localAppData, "CrashDumps"),
            Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue")
        };

        foreach (string dir in candidateDirs)
        {
            files.AddRange(SafeFileSystem.EnumerateFiles(dir, true, out int failures, cancellationToken: cancellationToken));
            enumerationFailures += failures;
        }

        return files;
    }

    private static List<string> EnumerateIsolatedDownloads(out int enumerationFailures, CancellationToken cancellationToken)
    {
        enumerationFailures = 0;
        var files = new List<string>();
        string dir = AppPaths.IsolatedDownloadsRoot;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return files;

        files.AddRange(SafeFileSystem.EnumerateFiles(dir, false, out enumerationFailures, cancellationToken: cancellationToken));

        return files;
    }

    private static long SumFileSizes(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                AppLog.Warn("读取文件大小失败：" + ex.GetType().Name);
            }
        }

        return total;
    }

    private static bool IsCurrentSession(Process process)
    {
        using var current = Process.GetCurrentProcess();
        return process.SessionId == current.SessionId;
    }

    private static bool IsExplorerRunning()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(ExplorerProcessName);
            bool running = false;
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (IsCurrentSession(process) && !process.HasExited) running = true;
                    }
                    catch
                    {
                        // Inaccessible foreign processes do not prove our desktop is running.
                        AppLog.Warn("无法检查当前会话资源管理器状态");
                    }
                }
            }

            return running;
        }
        catch (Exception ex)
        {
            AppLog.Error("检查资源管理器进程状态失败", ex);
            return false;
        }
    }

    private static bool TryStopExplorer(CancellationToken cancellationToken, ref bool killedExplorer)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ExplorerProcessName);
        }
        catch (Exception ex)
        {
            AppLog.Error("枚举资源管理器进程失败", ex);
            return false;
        }

        bool allTargetsExited = true;
        try
        {
            IntPtr shellWindow = GetShellWindow();
            if (shellWindow != IntPtr.Zero)
            {
                // Request a polite shell exit first so Winlogon does not immediately respawn explorer.exe.
                _ = PostMessageW(shellWindow, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                killedExplorer = true;
            }

            foreach (Process process in processes)
            {
                try
                {
                    if (!IsCurrentSession(process) || process.HasExited) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!process.WaitForExit(800))
                    {
                        process.Kill();
                        killedExplorer = true;
                        process.WaitForExit(3000);
                    }
                    if (!process.HasExited) allTargetsExited = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    allTargetsExited = false;
                    AppLog.Error("结束资源管理器进程失败", ex);
                }
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }

        return allTargetsExited;
    }

    private static bool EnsureExplorerRunning()
    {
        if (GetShellWindow() != IntPtr.Zero) return true;

        try
        {
            // Give Windows a brief moment to settle after Explorer termination before relaunching directly.
            Thread.Sleep(400);
            if (GetShellWindow() != IntPtr.Zero) return true;

            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windowsDirectory))
                windowsDirectory = Path.GetDirectoryName(Environment.SystemDirectory) ?? string.Empty;
            if (!Path.IsPathFullyQualified(windowsDirectory)) return false;

            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(windowsDirectory, ExplorerProcessName + ".exe"),
                WorkingDirectory = windowsDirectory,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("启动资源管理器失败", ex);
            return false;
        }

        int elapsed = 0;
        while (elapsed < 5000)
        {
            Thread.Sleep(200);
            elapsed += 200;
            if (GetShellWindow() != IntPtr.Zero) return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
