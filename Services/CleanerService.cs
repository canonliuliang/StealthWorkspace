using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

public sealed class CleanerOptions
{
    public bool RestartExplorerForThumbnails { get; set; }
    public bool DeepCleanTemp { get; set; }
    public bool SecureShredFiles { get; set; }
}

/// <summary>
/// 清理项数据模型。此处刻意不再实现 INotifyPropertyChanged：
/// 扫描 / 清理全部在后台线程完成并返回全新的实例，由界面线程整体替换列表，
/// 后台线程因此不会触发任何绑定通知，从根本上消除跨线程 UI 异常。
/// </summary>
public class CleanerItem
{
    public CleanerCategory Category { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string IconGlyph { get; set; } = "\uE74D";

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>是否勾选清理（与 CheckBox 双向绑定）。</summary>
    public bool IsSelected { get; set; } = true;

    public string Status { get; set; } = "待扫描";

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

    private static CleanerService? _instance;

    public static CleanerService Instance => _instance ??= new CleanerService();

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
        CleanerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        CleanerOptions effectiveOptions = options ?? new CleanerOptions();

        return Task.Run(() =>
        {
            var results = new List<CleanerItem>(items.Count);
            foreach (CleanerItem item in items)
            {
                try
                {
                    progress?.Report("正在扫描：" + item.Name);
                    results.Add(ScanItem(item, effectiveOptions));
                }
                catch (Exception ex)
                {
                    AppLog.Error($"扫描清理类别 {item.Category} 时发生异常", ex);
                    results.Add(CreateResult(item, "扫描发生内部错误，可稍后重试", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries));
                }
            }

            return results;
        });
    }

    public Task<List<CleanerItem>> CleanAsync(
        IReadOnlyList<CleanerItem> items,
        bool restartExplorerForThumbnails,
        IProgress<string>? progress = null)
    {
        return CleanAsync(
            items,
            new CleanerOptions { RestartExplorerForThumbnails = restartExplorerForThumbnails },
            progress);
    }

    public Task<List<CleanerItem>> CleanAsync(
        IReadOnlyList<CleanerItem> items,
        CleanerOptions options,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        SynchronizationContext? uiContext = SynchronizationContext.Current;

        return Task.Run(async () =>
        {
            var results = new List<CleanerItem>(items.Count);
            foreach (CleanerItem item in items)
            {
                if (!item.IsSelected)
                {
                    results.Add(CreateResult(item, "未勾选，已跳过", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries));
                    continue;
                }

                try
                {
                    progress?.Report("正在清理：" + item.Name);

                    CleanerItem result = item.Category switch
                    {
                        CleanerCategory.ThumbnailCache => CleanThumbnails(item, options.RestartExplorerForThumbnails),
                        CleanerCategory.RecentAndJumpList => CleanRecent(item, options.SecureShredFiles),
                        CleanerCategory.ExplorerSearchAndRunHistory => CleanExplorerHistory(item),
                        CleanerCategory.MediaAndRemoteHistory => CleanMediaAndRemoteHistory(item),
                        CleanerCategory.RecycleBin => CleanRecycleBin(item),
                        CleanerCategory.TempFiles => CleanTemp(item, options.DeepCleanTemp, options.SecureShredFiles),
                        CleanerCategory.CrashDumpsAndWer => CleanCrashDumpsAndWer(item, options.SecureShredFiles),
                        CleanerCategory.Clipboard => await CleanClipboardAsync(item, uiContext).ConfigureAwait(false),
                        CleanerCategory.DnsCache => CleanDns(item),
                        CleanerCategory.IsolatedDownloads => CleanIsolatedDownloads(item, options.SecureShredFiles),
                        _ => CreateResult(item, "未知类别，已跳过", item.DetectedCount, item.DetectedSizeBytes)
                    };

                    results.Add(result);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"清理类别 {item.Category} 时发生异常", ex);
                    results.Add(CreateResult(item, "清理发生内部错误，可稍后重试", item.DetectedCount, item.DetectedSizeBytes, item.IsSizeKnown, item.PreviewEntries));
                }
            }

            return results;
        });
    }

    // ---------------------------------------------------------------------
    // 扫描
    // ---------------------------------------------------------------------

    private static CleanerItem ScanItem(CleanerItem source, CleanerOptions options)
    {
        return source.Category switch
        {
            CleanerCategory.ThumbnailCache => ScanThumbnails(source),
            CleanerCategory.RecentAndJumpList => ScanRecent(source),
            CleanerCategory.ExplorerSearchAndRunHistory => ScanExplorerHistory(source),
            CleanerCategory.MediaAndRemoteHistory => ScanMediaAndRemoteHistory(source),
            CleanerCategory.RecycleBin => ScanRecycleBin(source),
            CleanerCategory.TempFiles => ScanTemp(source, options.DeepCleanTemp),
            CleanerCategory.CrashDumpsAndWer => ScanCrashDumpsAndWer(source),
            CleanerCategory.Clipboard => CreateResult(source, "可直接清空（无法预先统计大小）", 0, 0, false),
            CleanerCategory.DnsCache => CreateResult(source, "可直接刷新（无法预先统计缓存条目）", 0, 0, false),
            CleanerCategory.IsolatedDownloads => ScanIsolatedDownloads(source),
            _ => CreateResult(source, "未知类别，已跳过", 0, 0)
        };
    }

    private static CleanerItem ScanThumbnails(CleanerItem source)
    {
        string directory = GetExplorerCacheDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return CreateResult(source, "无需清理（系统未提供缩略图缓存目录）", 0, 0);
        }

        string[] files = GetThumbnailCacheFiles(directory, out bool enumerationSucceeded);
        if (!enumerationSucceeded)
        {
            return CreateResult(source, "无法读取缩略图缓存目录（权限不足）", source.DetectedCount, source.DetectedSizeBytes);
        }

        if (files.Length == 0)
        {
            return CreateResult(source, "无需清理（未发现缩略图缓存文件）", 0, 0);
        }

        long size = SumFileSizes(files);
        return CreateResult(source, $"已发现 {files.Length} 个缩略图缓存文件", files.Length, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanRecent(CleanerItem source)
    {
        List<string> files = EnumerateRecentFiles(out int enumerationFailures);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "“最近”目录部分内容无法访问，未能完整扫描"
                : "无需清理（未发现最近记录或跳转列表文件）";
            return CreateResult(source, emptyStatus, 0, 0);
        }

        long size = SumFileSizes(files);
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

    private static CleanerItem ScanTemp(CleanerItem source, bool deepClean)
    {
        List<string> files = EnumerateEligibleTempFiles(deepClean, out int enumerationFailures);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "临时目录部分内容无法访问，未能完整扫描"
                : (deepClean ? "无需清理（未发现可清理的临时文件）" : "无需清理（没有 24 小时前生成的临时文件）");
            return CreateResult(source, emptyStatus, 0, 0);
        }

        long size = SumFileSizes(files);
        string status = deepClean
            ? $"已发现 {files.Count} 个临时文件（含近期与子目录）"
            : $"已发现 {files.Count} 个 24 小时前生成的临时文件";
        if (enumerationFailures > 0) status += "（部分内容无法访问）";

        return CreateResult(source, status, files.Count, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanCrashDumpsAndWer(CleanerItem source)
    {
        List<string> files = EnumerateCrashAndWerFiles(out int failures);
        if (files.Count == 0)
        {
            string status = failures > 0
                ? "错误报告目录部分内容无法访问"
                : "无需清理（未发现崩溃转储或错误报告）";
            return CreateResult(source, status, 0, 0);
        }

        long size = SumFileSizes(files);
        return CreateResult(source, $"已发现 {files.Count} 个转储与错误报告文件", files.Count, size, true, BuildPreviewList(files));
    }

    private static CleanerItem ScanIsolatedDownloads(CleanerItem source)
    {
        List<string> files = EnumerateIsolatedDownloads(out int failures);
        if (files.Count == 0)
        {
            string status = failures > 0
                ? "隔离下载目录无法访问"
                : "无需清理（沙箱隔离下载目录为空）";
            return CreateResult(source, status, 0, 0);
        }

        long size = SumFileSizes(files);
        return CreateResult(source, $"已发现 {files.Count} 个沙箱隔离下载文件", files.Count, size, true, BuildPreviewList(files));
    }

    // ---------------------------------------------------------------------
    // 清理
    // ---------------------------------------------------------------------

    private static CleanerItem CleanThumbnails(CleanerItem source, bool restartExplorer)
    {
        string directory = GetExplorerCacheDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return CreateResult(source, "无需清理（系统未提供缩略图缓存目录）", 0, 0);
        }

        string[] files = GetThumbnailCacheFiles(directory, out bool enumerationSucceeded);
        if (!enumerationSucceeded)
        {
            return CreateResult(source, "无法读取缩略图缓存目录，未执行删除", source.DetectedCount, source.DetectedSizeBytes);
        }

        if (files.Length == 0)
        {
            return CreateResult(source, "无需清理（未发现缩略图缓存文件）", 0, 0);
        }

        var sizes = new long[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            try { sizes[i] = new FileInfo(files[i]).Length; }
            catch (Exception ex)
            {
                sizes[i] = 0;
                AppLog.Warn("读取缩略图缓存文件大小失败：" + ex.GetType().Name);
            }
        }

        bool explorerStopped = false;
        if (restartExplorer)
        {
            explorerStopped = TryStopExplorer();
            if (!explorerStopped)
            {
                AppLog.Warn("未能完全结束资源管理器进程，被占用的缩略图文件将按跳过处理");
            }
        }

        var deletedFlags = new bool[files.Length];
        int deleted = 0;
        int locked = 0;
        int failed = 0;
        bool restartAttempted = false;
        bool restartSucceeded = true;

        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    File.Delete(files[i]);
                    deletedFlags[i] = true;
                    deleted++;
                }
                catch (IOException ex)
                {
                    locked++;
                    AppLog.Warn("缩略图缓存文件被占用，已跳过：" + ex.GetType().Name);
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    AppLog.Error("删除缩略图缓存文件失败（权限不足）", ex);
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error("删除缩略图缓存文件失败", ex);
                }
            }
        }
        finally
        {
            if (restartExplorer)
            {
                restartAttempted = true;
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
        if (deleted > 0) parts.Add($"已删除 {deleted} 个缩略图缓存文件");
        if (locked > 0)
        {
            parts.Add(restartExplorer
                ? $"{locked} 个文件仍被占用未能删除" + (explorerStopped ? string.Empty : "（资源管理器未能完全退出）")
                : $"{locked} 个文件被资源管理器占用，可勾选“允许短暂重启资源管理器”后重试");
        }
        if (failed > 0) parts.Add($"{failed} 个文件删除失败");
        if (parts.Count == 0) parts.Add("没有删除任何文件");

        string status = string.Join("；", parts);
        if (remainingCount == 0) status += "；缩略图缓存已全部清除";
        if (restartAttempted)
        {
            if (!explorerStopped) status += "；资源管理器未能被结束，未执行重启";
            else status += restartSucceeded ? "；资源管理器已重启" : "；资源管理器重启失败，请手动重新打开";
        }

        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles));
    }

    private static CleanerItem CleanRecent(CleanerItem source, bool shred)
    {
        List<string> files = EnumerateRecentFiles(out int enumerationFailures);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "“最近”目录部分内容无法访问，未执行删除"
                : "无需清理（未发现最近记录或跳转列表文件）";
            return CreateResult(source, emptyStatus, 0, 0);
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed);

        List<string> remainingFiles = EnumerateRecentFiles(out int refreshFailures);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles);

        string status = BuildBatchStatus("最近记录 / 跳转列表文件", deleted, skipped, failed, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles));
    }

    private static CleanerItem CleanExplorerHistory(CleanerItem source)
    {
        int cleared = 0;
        int failed = 0;

        foreach (string keyPath in ExplorerHistoryKeyPaths)
        {
            ClearRegistryValues(keyPath, ref cleared, ref failed);
        }

        try
        {
            using RegistryKey? openSaveKey = Registry.CurrentUser.OpenSubKey(OpenSavePidlMruPath, writable: true);
            if (openSaveKey != null)
            {
                foreach (string subName in openSaveKey.GetSubKeyNames())
                {
                    ClearRegistryValues($@"{OpenSavePidlMruPath}\{subName}", ref cleared, ref failed);
                }
            }
        }
        catch (Exception ex)
        {
            failed++;
            AppLog.Error("清理 OpenSavePidlMRU 失败", ex);
        }

        var remainingPreview = new List<string>();
        int remaining = CountExplorerHistoryEntries(remainingPreview);

        string status = cleared > 0
            ? $"已清除 {cleared} 条资源管理器历史" + (failed > 0 ? $"（{failed} 项失败）" : string.Empty)
            : (failed > 0 ? "部分注册表历史清理失败" : "无需清理（未发现历史记录）");

        return CreateResult(source, status, remaining, 0, false, remainingPreview);
    }

    private static CleanerItem CleanMediaAndRemoteHistory(CleanerItem source)
    {
        int cleared = 0;
        int failed = 0;

        foreach (string keyPath in MediaAndOfficeRecentKeyPaths)
        {
            ClearRegistryValues(keyPath, ref cleared, ref failed);
        }

        // Terminal Server Client Default MRU*
        try
        {
            using RegistryKey? rdpDef = Registry.CurrentUser.OpenSubKey(TerminalServerDefaultPath, writable: true);
            if (rdpDef != null)
            {
                foreach (string valName in rdpDef.GetValueNames())
                {
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
        catch (Exception ex)
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
        catch (Exception ex)
        {
            failed++;
            AppLog.Error("清理远程桌面 Servers 历史失败", ex);
        }

        var remainingPreview = new List<string>();
        int remaining = CountMediaAndRemoteHistoryEntries(remainingPreview);

        string status = cleared > 0
            ? $"已清除 {cleared} 条媒体 / 应用 / 远程桌面历史" + (failed > 0 ? $"（{failed} 项失败）" : string.Empty)
            : (failed > 0 ? "部分历史清理失败" : "无需清理（未发现历史记录）");

        return CreateResult(source, status, remaining, 0, false, remainingPreview);
    }

    private static CleanerItem CleanRecycleBin(CleanerItem source)
    {
        CleanerItem preScan = ScanRecycleBin(source);
        if (preScan.DetectedCount == 0 && preScan.DetectedSizeBytes == 0)
        {
            return preScan;
        }

        try
        {
            uint flags = NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
            if (hr != 0)
            {
                AppLog.Warn($"SHEmptyRecycleBin 返回 HRESULT: 0x{hr:X8}");
                CleanerItem afterFail = ScanRecycleBin(source);
                return CreateResult(source, $"清空回收站未完全成功 (0x{hr:X8})", afterFail.DetectedCount, afterFail.DetectedSizeBytes, true, afterFail.PreviewEntries);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("清空回收站异常", ex);
            return CreateResult(source, "清空回收站失败：" + ex.Message, source.DetectedCount, source.DetectedSizeBytes, true, source.PreviewEntries);
        }

        CleanerItem postScan = ScanRecycleBin(source);
        string status = postScan.DetectedCount == 0
            ? $"已彻底清空回收站（释放约 {FormatSize(preScan.DetectedSizeBytes)}）"
            : $"部分清空，回收站仍剩 {postScan.DetectedCount} 项";

        return CreateResult(source, status, postScan.DetectedCount, postScan.DetectedSizeBytes, true, postScan.PreviewEntries);
    }

    private static CleanerItem CleanTemp(CleanerItem source, bool deepClean, bool shred)
    {
        List<string> files = EnumerateEligibleTempFiles(deepClean, out int enumerationFailures);
        if (files.Count == 0)
        {
            string emptyStatus = enumerationFailures > 0
                ? "临时目录部分内容无法访问，未执行删除"
                : (deepClean ? "无需清理（未发现可清理的临时文件）" : "无需清理（没有 24 小时前生成的临时文件）");
            return CreateResult(source, emptyStatus, 0, 0);
        }

        DateTime cutoff = DateTime.UtcNow - TempFileMinimumAge;
        int deleted = 0;
        int skippedBusy = 0;
        int skippedTooNew = 0;
        int failed = 0;

        foreach (string file in files)
        {
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

                if (TryDeleteOrShred(file, shred, out bool isBusy))
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
            TryCleanupEmptyTempSubdirectories();
        }

        List<string> remainingFiles = EnumerateEligibleTempFiles(deepClean, out int refreshFailures);
        int remainingCount = refreshFailures == 0 ? remainingFiles.Count : Math.Max(0, files.Count - deleted);
        long remainingSize = refreshFailures == 0 ? SumFileSizes(remainingFiles) : 0;

        var parts = new List<string>();
        if (deleted > 0) parts.Add($"已删除 {deleted} 个临时文件" + (shred ? "（已覆写粉碎）" : string.Empty));
        if (skippedBusy > 0) parts.Add($"{skippedBusy} 个文件被占用已跳过");
        if (skippedTooNew > 0) parts.Add($"{skippedTooNew} 个文件在清理时被更新，已跳过");
        if (failed > 0) parts.Add($"{failed} 个文件删除失败");
        if (parts.Count == 0) parts.Add("没有删除任何文件");

        string status = string.Join("；", parts);
        if (remainingCount > 0) status += $"；仍有 {remainingCount} 个临时文件保留";
        if (enumerationFailures > 0 || refreshFailures > 0) status += "；部分临时文件无法访问，结果可能不完整";

        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles));
    }

    private static CleanerItem CleanCrashDumpsAndWer(CleanerItem source, bool shred)
    {
        List<string> files = EnumerateCrashAndWerFiles(out int enumerationFailures);
        if (files.Count == 0)
        {
            return CreateResult(source, "无需清理（未发现崩溃转储或错误报告）", 0, 0);
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed);

        List<string> remainingFiles = EnumerateCrashAndWerFiles(out int refreshFailures);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles);

        string status = BuildBatchStatus("转储 / 错误报告文件", deleted, skipped, failed, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles));
    }

    private static CleanerItem CleanIsolatedDownloads(CleanerItem source, bool shred)
    {
        List<string> files = EnumerateIsolatedDownloads(out int enumerationFailures);
        if (files.Count == 0)
        {
            return CreateResult(source, "无需清理（沙箱隔离下载目录为空）", 0, 0);
        }

        DeleteFilesBatch(files, shred, out int deleted, out int skipped, out int failed);

        List<string> remainingFiles = EnumerateIsolatedDownloads(out int refreshFailures);
        int remainingCount = refreshFailures > 0 ? Math.Max(0, files.Count - deleted) : remainingFiles.Count;
        long remainingSize = SumFileSizes(remainingFiles);

        string status = BuildBatchStatus("沙箱隔离下载文件", deleted, skipped, failed, remainingCount, enumerationFailures > 0);
        return CreateResult(source, status, remainingCount, remainingSize, true, BuildPreviewList(remainingFiles));
    }

    private static async Task<CleanerItem> CleanClipboardAsync(CleanerItem source, SynchronizationContext? uiContext)
    {
        bool clipboardCleared = false;
        bool historyCleared = false;
        bool historyUnavailable = false;

        void ClearNow()
        {
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
                clipboardCleared = true;
            }
            catch (Exception ex)
            {
                AppLog.Error("清空系统剪贴板失败", ex);
            }

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
            try
            {
                uiContext.Post(_ =>
                {
                    try { ClearNow(); }
                    finally { completion.TrySetResult(true); }
                }, null);

                await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("等待界面线程清空剪贴板超时");
            }
            catch (Exception ex)
            {
                AppLog.Error("无法把剪贴板清理调度到界面线程", ex);
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

        return CreateResult(source, status, clipboardCleared ? 0 : source.DetectedCount, 0, false);
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
            return CreateResult(source, "DNS 刷新失败（系统调用异常），可稍后重试", source.DetectedCount, 0, false);
        }

        return flushResult != 0
            ? CreateResult(source, "DNS 解析缓存已刷新并清空", 0, 0, false)
            : CreateResult(source, "DNS 缓存刷新未生效（系统返回失败），可稍后重试", source.DetectedCount, 0, false);
    }

    // ---------------------------------------------------------------------
    // 通用工具
    // ---------------------------------------------------------------------

    private static CleanerItem CreateResult(
        CleanerItem source,
        string status,
        int detectedCount,
        long detectedSizeBytes,
        bool? isSizeKnown = null,
        IReadOnlyList<string>? previewEntries = null)
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
        out int failed)
    {
        deleted = 0;
        skipped = 0;
        failed = 0;

        foreach (string file in files)
        {
            if (TryDeleteOrShred(file, shred, out bool isBusy))
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
            }
        }
    }

    private static bool TryDeleteOrShred(string file, bool shred, out bool isBusy)
    {
        isBusy = false;
        try
        {
            if (!File.Exists(file))
            {
                return true;
            }

            if (!SafeFileSystem.IsPlainPath(file)) return false;

            if (shred)
            {
                if (CryptoService.ShredFile(file))
                {
                    return true;
                }

                // Determine whether failure was caused by file lock vs permissions.
                try
                {
                    using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    isBusy = true;
                    return false;
                }
                catch
                {
                    return false;
                }

                return false;
            }

            File.Delete(file);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException ex)
        {
            isBusy = true;
            AppLog.Warn("文件被占用，已跳过：" + ex.GetType().Name);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("删除文件失败（权限不足）", ex);
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
        int remainingCount,
        bool hadEnumErrors)
    {
        var parts = new List<string>();
        if (deleted > 0) parts.Add($"已删除 {deleted} 个{unitLabel}");
        if (skipped > 0) parts.Add($"{skipped} 个文件被占用已跳过");
        if (failed > 0) parts.Add($"{failed} 个文件删除失败");
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

    private static void ClearRegistryValues(string subKeyPath, ref int cleared, ref int failed)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
            if (key == null) return;

            foreach (string name in key.GetValueNames())
            {
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
        catch (Exception ex)
        {
            failed++;
            AppLog.Warn("打开注册表失败", ex);
        }
    }

    private static string GetExplorerCacheDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return string.Empty;
        return Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
    }

    private static string[] GetThumbnailCacheFiles(string directory, out bool success)
    {
        try
        {
            string[] files = Directory.GetFiles(directory, ThumbnailSearchPattern, SearchOption.TopDirectoryOnly);
            success = true;
            return files;
        }
        catch (Exception ex)
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

    private static List<string> EnumerateRecentFiles(out int enumerationFailures)
    {
        enumerationFailures = 0;
        var files = new List<string>();
        string root = GetRecentDirectory();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return files;
        }

        try
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsShellMetadataFile(file)) continue;
                files.Add(file);
            }
        }
        catch (Exception ex)
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
            try
            {
                FileAttributes attributes = File.GetAttributes(subDirectory);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                foreach (string file in Directory.GetFiles(subDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsShellMetadataFile(file)) continue;
                    files.Add(file);
                }
            }
            catch (Exception ex)
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

    private static List<string> EnumerateEligibleTempFiles(bool deepClean, out int enumerationFailures)
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
            dir => Path.GetFileName(dir).StartsWith(WebProfilePrefix, StringComparison.OrdinalIgnoreCase));
        foreach (string file in files)
        {
            try
            {
                if (deepClean || File.GetLastWriteTimeUtc(file) < cutoff) eligible.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { enumerationFailures++; }
        }

        return eligible;
    }

    private static void TryCleanupEmptyTempSubdirectories()
    {
        try
        {
            string tempDirectory = Path.GetTempPath();
            foreach (string subDir in Directory.GetDirectories(tempDirectory, "*", SearchOption.TopDirectoryOnly))
            {
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
        catch { }
    }

    private static List<string> EnumerateCrashAndWerFiles(out int enumerationFailures)
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
            files.AddRange(SafeFileSystem.EnumerateFiles(dir, true, out int failures));
            enumerationFailures += failures;
        }

        return files;
    }

    private static List<string> EnumerateIsolatedDownloads(out int enumerationFailures)
    {
        enumerationFailures = 0;
        var files = new List<string>();
        string dir = AppPaths.IsolatedDownloadsRoot;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return files;

        files.AddRange(SafeFileSystem.EnumerateFiles(dir, false, out enumerationFailures));

        return files;
    }

    private static long SumFileSizes(IEnumerable<string> files)
    {
        long total = 0;
        foreach (string file in files)
        {
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

    private static bool TryStopExplorer()
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

        foreach (Process process in processes)
        {
            using (process)
            {
                try
                {
                    if (!IsCurrentSession(process) || process.HasExited) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    AppLog.Error("结束资源管理器进程失败", ex);
                }
            }
        }

        return WaitForExplorerExit(5000);
    }

    private static bool WaitForExplorerExit(int timeoutMilliseconds)
    {
        int elapsed = 0;
        while (elapsed < timeoutMilliseconds)
        {
            if (!IsExplorerRunning()) return true;
            Thread.Sleep(200);
            elapsed += 200;
        }

        return !IsExplorerRunning();
    }

    private static bool EnsureExplorerRunning()
    {
        if (IsExplorerRunning()) return true;

        try
        {
            // Give Windows a brief moment to settle after Explorer termination before relaunching directly.
            Thread.Sleep(400);

            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string executable = string.IsNullOrEmpty(windowsDirectory)
                ? ExplorerProcessName + ".exe"
                : Path.Combine(windowsDirectory, ExplorerProcessName + ".exe");

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = string.IsNullOrEmpty(windowsDirectory) ? Environment.SystemDirectory : windowsDirectory,
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
            if (IsExplorerRunning()) return true;
        }

        return false;
    }
}
