using System;
using System.IO;

namespace StealthWorkspace.Services;

/// <summary>
/// Central paths for the app's private (hidden + system attributed) data root.
/// All persisted state lives below this single root so it can be audited, migrated
/// and removed in one place.
/// </summary>
public static class AppPaths
{
    internal const string LegacyImportDisabledFileName = ".legacy-import-disabled";
    private static readonly object Gate = new();
    private static string? _dataRoot;
    private static string? _overrideRoot;

    /// <summary>Explicit diagnostic roots must not import data beside the installed executable.</summary>
    internal static bool HasDataRootOverride
    {
        get { lock (Gate) return _overrideRoot != null; }
    }

    internal static bool IsLegacyImportDisabled(string root)
    {
        try { _ = File.GetAttributes(Path.Combine(root, LegacyImportDisabledFileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // An inaccessible marker cannot authorize reopening an older preserved vault.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Diagnostic/testing override for the private data root. Must be called before the
    /// first use of <see cref="DataRoot"/> (the app does this from Program.Main when the
    /// <c>--data-root</c> switch is present). Production runs never set it.
    /// </summary>
    public static void SetDataRootOverride(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        lock (Gate)
        {
            _overrideRoot = WorkspaceFileName.FullPath(root);
        }
    }

    /// <summary>Keep using an existing writable vault; copy it intact if its root is unwritable.</summary>
    public static string DataRoot
    {
        get
        {
            lock (Gate)
            {
                if (_overrideRoot != null) return _overrideRoot;
                if (_dataRoot != null) return _dataRoot;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dataRoot ??= ResolveDataRoot(
                    Path.Combine(local, "Microsoft", "Windows", "SystemDiagnostics", "Data"),
                    Path.Combine(local, "StealthWorkspace", "Data"),
                    Path.Combine(AppContext.BaseDirectory, "WorkspaceData"));
                return _dataRoot;
            }
        }
    }

    internal static string ResolveDataRoot(string legacy, string local, string portable)
    {
        // A completed migration or a vault already in the new location takes
        // precedence over the preserved legacy copy.
        static bool HasVault(string root) => File.Exists(Path.Combine(root, "system_catalog.dat"))
            || File.Exists(Path.Combine(root, "system_catalog.dat.bak"))
            || File.Exists(Path.Combine(root, "remove.dat"))
            || File.Exists(Path.Combine(root, "rotate.dat"))
            || File.Exists(Path.Combine(root, ".migrated"))
            || IsLegacyImportDisabled(root);

        string[] roots = { portable, local, legacy };
        string? existing = roots.FirstOrDefault(HasVault)
            ?? roots.FirstOrDefault(root => File.Exists(Path.Combine(root, "settings.dat")));
        if (existing != null && CanWriteVault(existing)) return existing;
        if (existing != null && !string.Equals(existing, legacy, StringComparison.OrdinalIgnoreCase))
            return existing; // Never silently reopen an older copy of a migrated vault.

        foreach (string target in new[] { local, portable })
        {
            try
            {
                if (existing == null)
                {
                    if (CanWriteVault(target)) return target;
                }
                else if (TryCopyLegacyVault(existing, target))
                {
                    return target;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the remaining location; the source vault is never changed.
            }
        }

        return existing ?? local;
    }

    private static bool CanWriteVault(string root)
    {
        try
        {
            string media = Path.Combine(root, "MediaVault");
            SafeFileSystem.EnsureWorkspaceDirectory(media);
            foreach (string folder in new[] { root, media })
            {
                string probe = Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N"));
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
            string catalog = Path.Combine(root, "system_catalog.dat");
            if (File.Exists(catalog) && (File.GetAttributes(catalog) & FileAttributes.ReadOnly) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryCopyLegacyVault(string source, string target)
    {
        // Keep a distinct staging directory until every file has been copied.
        // An existing destination is never merged or overwritten.
        if (Directory.Exists(target) || File.Exists(target)) return false;
        string stage = Path.GetFullPath(target) + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!CanWriteVault(stage)) return false;

            static void CopyIfPresent(string path, string directory)
            {
                string copy = Path.Combine(directory, Path.GetFileName(path));
                try { File.Copy(path, copy); }
                catch (FileNotFoundException) { return; }
                File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
            }

            foreach (string name in new[] { "system_catalog.dat", "system_catalog.dat.bak", "settings.dat", "settings.dat.bak", "rotate.dat", "remove.dat", LegacyImportDisabledFileName })
                CopyIfPresent(Path.Combine(source, name), stage);

            string sourceMedia = Path.Combine(source, "MediaVault");
            try
            {
                foreach (string path in Directory.EnumerateFiles(sourceMedia, "*.svault"))
                {
                    string copy = Path.Combine(stage, "MediaVault", Path.GetFileName(path));
                    File.Copy(path, copy);
                    File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
                }
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(sourceMedia))
            {
                // An existing vault can be empty and not yet have a media directory.
            }

            // Only expose a complete copy to subsequent starts.
            File.WriteAllText(Path.Combine(stage, ".migrated"), "1");
            PublishStagedVault(stage, target);
            return true;
        }
        finally
        {
            // Only our unpublished staging directory is removed, never the source or target.
            if (Directory.Exists(stage) && SafeFileSystem.IsPlainWorkspacePath(stage))
            {
                try
                {
                    foreach (string file in SafeFileSystem.EnumerateFiles(stage, true, out _, workspace: true)) File.Delete(file);
                    string media = Path.Combine(stage, "MediaVault");
                    if (Directory.Exists(media) && SafeFileSystem.IsPlainWorkspacePath(media)) Directory.Delete(media, false);
                    Directory.Delete(stage, false);
                }
                catch (Exception ex) { AppLog.Warn("迁移暂存目录清理失败: " + ex.GetType().Name); }
            }
        }
    }

    internal static void PublishStagedVault(string stage, string target)
    {
        // Windows can deny a parent directory rename while a reader briefly holds
        // a copied file without delete sharing. Reuse the completed copy in place.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(stage, target);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException
                && (ex.HResult & 0xffff) is 5 or 32 or 33
                && !Directory.Exists(target) && !File.Exists(target)
                && SafeFileSystem.IsPlainWorkspacePath(stage))
            {
                Thread.Sleep(25 << attempt); // At most 775 ms across all retries.
            }
        }
    }

    public static string MediaRoot => Path.Combine(DataRoot, "MediaVault");
    public static string LogPath => Path.Combine(DataRoot, "app.log");
    public static string SettingsPath => Path.Combine(DataRoot, "settings.dat");
    public static string VaultConfigPath => Path.Combine(DataRoot, "system_catalog.dat");
    public static string VaultConfigBackupPath => VaultConfigPath + ".bak";
    public static string IsolatedDownloadsRoot => BrowserDownloadFolders.Current;

    internal static async Task FinishBrowserSessionAsync(Task released, string? decoyDirectoryName)
    {
        try { await released.ConfigureAwait(false); }
        finally
        {
            if (decoyDirectoryName != null)
            {
                foreach (string home in BrowserDownloadFolders.Homes())
                {
                    string path = BrowserDownloadFolders.PathFor(home, decoyDirectoryName);
                    await Task.Run(() => ClearDownloadDirectory(path)).ConfigureAwait(false);
                    if (Directory.Exists(path))
                    {
                        await Task.Delay(400).ConfigureAwait(false);
                        await Task.Run(() => ClearDownloadDirectory(path)).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal static void SweepDecoyDownloads()
    {
        foreach (string home in BrowserDownloadFolders.Homes())
            SweepDecoyDownloads(Path.Combine(home, "IsolatedDownloadsDecoy"));
    }

    private static void SweepDecoyDownloads(string root)
    {
        if (!Directory.Exists(root) || !SafeFileSystem.IsPlainWorkspacePath(root)) return;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "session-*"))
            {
                var parts = Path.GetFileName(directory).Split('-');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int pid) || pid <= 0 ||
                    !Guid.TryParseExact(parts[2], "N", out _)) continue;
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); if (!process.HasExited) continue; }
                catch (ArgumentException) { } // The process that owned these downloads has finished.
                catch (System.ComponentModel.Win32Exception) { continue; }
                ClearDownloadDirectory(directory);
            }
        }
        catch (Exception ex) { AppLog.Warn("检查过期临时下载失败：" + ex.GetType().Name); }
    }

    private static void ClearDownloadDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir) || !SafeFileSystem.IsPlainWorkspacePath(dir)) return;
            foreach (string file in SafeFileSystem.EnumerateFiles(dir, false, out _, workspace: true))
            {
                try
                {
                    SafeFileSystem.DeleteReadOnlyFile(file, workspace: true);
                }
                catch (Exception ex) { AppLog.Warn("临时下载尚未移除：" + ex.GetType().Name); }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, false);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>Ensures the root exists and is hidden+system attributed. Best effort.</summary>
    public static string EnsureDataRoot()
    {
        string root = DataRoot;
        try
        {
            SafeFileSystem.EnsureWorkspaceDirectory(root);
            Hide(root);
        }
        catch
        {
            // Attributes are cosmetic camouflage; a failure must never break the app.
        }
        return root;
    }

    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Hide(path);
    }

    public static void Hide(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            File.SetAttributes(path, attrs | FileAttributes.Hidden | FileAttributes.System);
        }
        catch
        {
            // ignored
        }
    }
}
