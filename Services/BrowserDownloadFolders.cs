using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StealthWorkspace.Services;

/// <summary>Per-vault download locations. Only an explicit download or folder command creates a directory.</summary>
internal static class BrowserDownloadFolders
{
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    private static readonly object Gate = new();
    private static string? _selectedHome;
    private static string? _selectedForRoot;
    private const string WriteProbePrefix = ".write-";

    internal static IEnumerable<FileInfo> EnumerateCompletedFiles(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles().Where(file =>
            !file.Extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) && !IsWriteProbe(file.Name));

    private static bool IsWriteProbe(string name) =>
        name.Length == WriteProbePrefix.Length + 32 && name.StartsWith(WriteProbePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name.AsSpan(WriteProbePrefix.Length), "N", out _);

    internal static string PathFor(string home, string? decoySession) => decoySession == null
        ? Path.Combine(home, "IsolatedDownloads")
        : Path.Combine(home, "IsolatedDownloadsDecoy", decoySession);

    internal static string Current
    {
        get
        {
            string root = AppPaths.DataRoot;
            lock (Gate)
                return PathFor(_selectedForRoot == root ? _selectedHome! : root,
                    SettingsService.Instance.ActiveBookmarkSession?.DownloadDirectoryName);
        }
    }

    internal static IEnumerable<string> Homes()
    {
        string root = AppPaths.DataRoot;
        yield return root;
        // Diagnostics must never fall back to a real user's directory.
        if (AppPaths.HasDataRootOverride) yield return Path.Combine(root, "IsolatedDownloadFallback");
        else if (GetLowHome(root) is { } low) yield return low;
    }

    internal static string Prepare(SettingsService.BookmarkSession? workspace)
    {
        if (!BrowserDownloadPath.IsCurrentWorkspace(workspace)) throw new InvalidOperationException("当前空间已锁定。");
        string root = AppPaths.DataRoot;
        lock (Gate)
        {
            string? sessionName = workspace!.DownloadDirectoryName;
            Exception? failure = null;
            foreach (string home in Homes())
            {
                string directory = PathFor(home, sessionName);
                try
                {
                    string? ancestor = directory;
                    while (ancestor != null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
                    if (ancestor == null || !SafeFileSystem.IsPlainPath(ancestor))
                        throw new UnauthorizedAccessException("下载目录含有重定向。");
                    Directory.CreateDirectory(directory);
                    if (!SafeFileSystem.IsPlainPath(directory)) throw new UnauthorizedAccessException("下载目录含有重定向。");
                    // An existing, readable folder may still deny new files to this process.
                    using (new FileStream(Path.Combine(directory, WriteProbePrefix + Guid.NewGuid().ToString("N")),
                        FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    AppPaths.Hide(directory);
                    _selectedHome = home;
                    _selectedForRoot = root;
                    return directory;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { failure = ex; }
            }
            throw failure ?? new IOException("Windows 没有可写的应用下载目录。");
        }
    }

    internal static string FailureMessage(Exception error) => error switch
    {
        Win32Exception { NativeErrorCode: 5 } => "Windows 拒绝了此次文件夹访问。仍可在这里浏览文件或复制文件夹路径。",
        UnauthorizedAccessException => "Windows 拒绝了文件夹访问权限。",
        DirectoryNotFoundException => "下载目录不存在或已移动。",
        _ => $"Windows 未能打开应用下载目录（0x{error.HResult:X8}）。"
    };

    private static string? GetLowHome(string root)
    {
        string? low = GetLocalAppDataLow();
        if (string.IsNullOrEmpty(low)) return null;
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return Path.Combine(low, "StealthWorkspace", "Downloads", key);
    }

    internal static string? GetLocalAppDataLow()
    {
        Guid id = LocalAppDataLow;
        if (SHGetKnownFolderPath(ref id, 0, 0, out nint pointer) < 0) return null;
        try { return Marshal.PtrToStringUni(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    internal static string? Previous(SettingsService.BookmarkSession? workspace)
    {
        if (!BrowserDownloadPath.IsCurrentWorkspace(workspace) || workspace!.Temporary) return null;
        string current = Current;
        return Homes().Select(home => PathFor(home, null)).FirstOrDefault(directory =>
            !string.Equals(current, directory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory) && SafeFileSystem.IsPlainPath(directory));
    }

    internal static async Task OpenAsync(string directory, Func<bool> isCurrent)
    {
        if (!Directory.Exists(directory) || !SafeFileSystem.IsPlainPath(directory)) throw new DirectoryNotFoundException();
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
            if (!isCurrent()) return;
            if (await Windows.System.Launcher.LaunchFolderAsync(folder)) return;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        { if (SmokeTest.BrowserAudit) SmokeTest.Report($"folder launcher unavailable: {ex.GetType().Name} (0x{ex.HResult:X8})"); }
        if (!isCurrent()) return;
        using var launched = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true, Verb = "open" });
    }

    internal static void SelectFile(string file)
    {
        if (!File.Exists(file) || !SafeFileSystem.IsPlainPath(file)) throw new FileNotFoundException();
        // With zero child IDs the Shell opens the parent folder and selects the item.
        Marshal.ThrowExceptionForHR(SHParseDisplayName(file, 0, out nint item, 0, out _));
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0)); }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint requestedAttributes, out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint children, uint flags);
}
