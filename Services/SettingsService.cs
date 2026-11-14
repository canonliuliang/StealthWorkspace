using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StealthWorkspace.Services;

public class BookmarkItem
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Url : Title;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class AppSettingsData
{
    // Appearance
    public string AppTheme { get; set; } = "Dark";
    public string DefaultStartPage { get; set; } = "browser";
    public bool BrowserFirstExperience { get; set; }
    public bool IsCompactNav { get; set; } = false;

    // Security & Vault
    public bool DisplayAffinityProtected { get; set; } = true;
    public bool TaskbarPeekExcluded { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 15;
    public bool LockOnMinimize { get; set; } = false;
    public int ShredPasses { get; set; } = 3;
    public bool ClearClipboardOnLock { get; set; } = true;

    // Boss Key
    public bool BossKeyEnabled { get; set; } = true;
    public int BossKeyAction { get; set; } = 0; // 0: Minimize & Mute, 1: Lock & Minimize, 2: Camouflage Update Screen
    public bool MuteOnBossKey { get; set; } = true;

    // Browser Privacy
    public string SearchEngine { get; set; } = "DuckDuckGo";
    public bool BlockCamera { get; set; } = true;
    public bool BlockMicrophone { get; set; } = true;
    public bool BlockGeolocation { get; set; } = true;
    public bool BlockNotifications { get; set; } = true;
    // Legacy serialized preference, retained for old settings files. Sessions are
    // now always cleared on lock/exit; page navigation retains the current session.
    public bool AutoBurnOnExit { get; set; } = true;

    // Bookmarks (encrypted at rest via DPAPI)
    public List<BookmarkItem> Bookmarks { get; set; } = new();

    // Cleaner Preferences
    public bool AutoCheckThumbcacheOnStartup { get; set; } = true;
    public bool AutoFlushDnsOnExit { get; set; } = true;
    public bool AutoWipeJumpList { get; set; } = true;

    // Camouflage
    public int CamouflageIndex { get; set; } = 0;
}

/// <summary>
/// Settings persistence. Writes are atomic (temp file + replace) and a .bak copy of the
/// last known good file is kept, so a crash while saving can never silently reset the
/// user's configuration.
/// </summary>
public class SettingsService
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly string _settingsFilePath;
    private readonly string _settingsBackupPath;
    private readonly object _saveGate = new();

    public AppSettingsData Data { get; private set; }

    /// <summary>True when the persisted settings file existed but could not be read.</summary>
    public bool LoadFailed { get; private set; }

    public event Action? SettingsChanged;

    public SettingsService() : this(AppPaths.EnsureDataRoot(),
        AppPaths.HasDataRootOverride ? null : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StealthVault_Store", "settings.json"))
    {
    }

    // Explicit paths keep migration fault tests away from the real user data and
    // installed application's legacy directory. Production uses the constructor above.
    internal SettingsService(string dataRoot, string? legacySettingsFile)
    {
        _settingsFilePath = Path.Combine(Path.GetFullPath(dataRoot), "settings.dat");
        _settingsBackupPath = _settingsFilePath + ".bak";
        try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!); }
        catch { /* Reads/default settings still work if persistence is unavailable. */ }

        if (legacySettingsFile != null) MigrateOldSettingsIfPresent(legacySettingsFile);
        Data = LoadSettings();
        Normalize();
        if (!LoadFailed && !Data.BrowserFirstExperience)
        {
            Data.DefaultStartPage = "browser";
            Data.BrowserFirstExperience = true;
            SaveSettings();
        }
    }

    private void MigrateOldSettingsIfPresent(string oldSettings)
    {
        try
        {
            if (!File.Exists(oldSettings)) return;
            string oldStore = Path.GetDirectoryName(Path.GetFullPath(oldSettings))!;
            string json = File.ReadAllText(oldSettings, Encoding.UTF8);
            var loaded = SafeDeserialize(json);
            // A malformed source may still be manually recoverable. Keep it even
            // if the app can create fresh defaults alongside it.
            if (loaded == null)
            {
                AppLog.Warn("旧设置无法解析，已保留原文件");
                return;
            }

            if (!File.Exists(_settingsFilePath))
            {
                Data = loaded;
                Normalize();
                if (!SaveSettings())
                {
                    AppLog.Warn("旧设置迁移尚未保存，已保留原文件以便重试");
                    return;
                }
            }

            // Cleanup is the last phase: a successful write alone is not enough.
            // Verify the replacement can be decrypted and parsed before removing
            // the only readable copy. Existing corrupt encrypted settings also
            // must not cause an unrelated legacy copy to be discarded.
            if (TryLoadFrom(_settingsFilePath) == null) return;
            try { File.Delete(oldSettings); } catch { }
            try
            {
                if (Directory.Exists(oldStore) && !Directory.EnumerateFileSystemEntries(oldStore).Any())
                {
                    Directory.Delete(oldStore, true);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"旧设置迁移失败: {ex.GetType().Name}");
        }
    }

    public AppSettingsData LoadSettings()
    {
        LoadFailed = false;

        var loaded = TryLoadFrom(_settingsFilePath);
        if (loaded != null) return loaded;

        if (File.Exists(_settingsFilePath))
        {
            LoadFailed = true;
            AppLog.Error("设置文件无法解密/解析，尝试备份", null);
            BackupCorruptFile(_settingsFilePath, "settings.corrupt");
        }

        loaded = TryLoadFrom(_settingsBackupPath);
        if (loaded != null)
        {
            AppLog.Warn("已从设置备份恢复");
            LoadFailed = false; // recovered cleanly from the backup
            return loaded;
        }

        return new AppSettingsData();
    }

    private AppSettingsData? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return null;
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { return SafeDeserialize(Encoding.UTF8.GetString(decrypted)); }
            finally { CryptographicOperations.ZeroMemory(decrypted); }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取设置失败: {ex.GetType().Name}");
            return null;
        }
    }

    private static AppSettingsData? SafeDeserialize(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<AppSettingsData>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Persists settings atomically. Never throws; failures are logged.
    /// </summary>
    public bool SaveSettings()
    {
        lock (_saveGate)
        {
            try
            {
                Normalize();
                string json = JsonSerializer.Serialize(Data);
                byte[] plaintext = Encoding.UTF8.GetBytes(json);
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }

                string tempPath = _settingsFilePath + ".tmp";
                File.WriteAllBytes(tempPath, encrypted);

                if (File.Exists(_settingsFilePath))
                {
                    try
                    {
                        File.Replace(tempPath, _settingsFilePath, _settingsBackupPath, ignoreMetadataErrors: true);
                    }
                    catch (Exception)
                    {
                        // File.Replace is not supported on every file system; fall back to a move.
                        File.Move(tempPath, _settingsFilePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, _settingsFilePath, overwrite: true);
                }

                // Persisted state is already committed. A view that has closed
                // must not turn a successful disk write into a reported failure
                // or prevent the remaining views from updating.
                if (SettingsChanged is { } changed)
                    foreach (Action subscriber in changed.GetInvocationList())
                        try { subscriber(); }
                        catch (Exception ex) { AppLog.Warn("刷新设置视图失败: " + ex.GetType().Name); }
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("保存设置失败", ex);
                return false;
            }
        }
    }

    private static void BackupCorruptFile(string path, string prefix)
    {
        try
        {
            string backup = Path.Combine(Path.GetDirectoryName(path)!, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.dat");
            File.Move(path, backup, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Clamps/validates every field so a corrupted or hand-edited file cannot cause
    /// out-of-range indexing, integer overflow or unexpected behaviour downstream.</summary>
    public void Normalize()
    {
        var d = Data;
        d.Bookmarks ??= new List<BookmarkItem>();
        d.Bookmarks = d.Bookmarks.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Url)).ToList();
        foreach (var b in d.Bookmarks)
        {
            b.Title ??= string.Empty;
            b.Url ??= string.Empty;
        }

        d.AppTheme = d.AppTheme switch
        {
            "Dark" => "Dark",
            "Light" => "Light",
            _ => "Default"
        };

        d.DefaultStartPage = d.DefaultStartPage switch
        {
            "vault" or "browser" or "cleaner" or "player" => d.DefaultStartPage,
            _ => "browser"
        };

        d.SearchEngine = d.SearchEngine switch
        {
            "DuckDuckGo" or "Startpage" or "Bing" or "Google" => d.SearchEngine,
            _ => "DuckDuckGo"
        };

        d.AutoLockMinutes = d.AutoLockMinutes switch
        {
            0 or 5 or 15 or 30 => d.AutoLockMinutes,
            _ => 15
        };

        d.ShredPasses = d.ShredPasses switch
        {
            1 or 3 or 7 => d.ShredPasses,
            _ => 3
        };

        d.BossKeyAction = Math.Clamp(d.BossKeyAction, 0, 2);
        d.CamouflageIndex = Math.Clamp(d.CamouflageIndex, 0, 3);
    }

    public bool IsBookmarked(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current))
            return Data.Bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.Ordinal));
        return Data.Bookmarks.Any(b => BrowserAddress.SamePage(current, b.Url));
    }

    public bool AddBookmark(string title, string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (IsBookmarked(uri.AbsoluteUri)) return true;
        string name = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
        var updated = Data.Bookmarks.ToList();
        updated.Insert(0, new BookmarkItem
        {
            Title = name.Length > 200 ? name[..200] : name,
            Url = uri.AbsoluteUri,
            CreatedAt = DateTime.Now
        });
        return SaveBookmarks(updated);
    }

    public bool RemoveBookmark(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var updated = Data.Bookmarks.Where(b => !BrowserAddress.SamePage(b.Url, url)).ToList();
        return updated.Count == Data.Bookmarks.Count || SaveBookmarks(updated);
    }

    public bool RenameBookmark(string url, string title)
    {
        var bookmark = Data.Bookmarks.FirstOrDefault(b => BrowserAddress.SamePage(b.Url, url));
        title = title.Trim();
        if (bookmark == null || title.Length == 0) return false;
        if (bookmark.Title == title) return true;
        var updated = Data.Bookmarks.Select(item => ReferenceEquals(item, bookmark)
            ? new BookmarkItem { Title = title.Length > 200 ? title[..200] : title, Url = item.Url, CreatedAt = item.CreatedAt }
            : item).ToList();
        return SaveBookmarks(updated);
    }

    private bool SaveBookmarks(List<BookmarkItem> updated)
    {
        lock (_saveGate)
        {
            var previous = Data.Bookmarks;
            Data.Bookmarks = updated;
            if (SaveSettings()) return true;
            Data.Bookmarks = previous;
            return false;
        }
    }

    public string GetSearchUrl(string query)
    {
        string encoded = Uri.EscapeDataString(query ?? string.Empty);
        return Data.SearchEngine switch
        {
            "Startpage" => $"https://www.startpage.com/do/search?q={encoded}",
            "Bing" => $"https://www.bing.com/search?q={encoded}",
            "Google" => $"https://www.google.com/search?q={encoded}",
            _ => $"https://duckduckgo.com/?q={encoded}"
        };
    }

    public string GetSearchHomeUrl()
    {
        return Data.SearchEngine switch
        {
            "Startpage" => "https://www.startpage.com",
            "Bing" => "https://www.bing.com",
            "Google" => "https://www.google.com",
            _ => "https://duckduckgo.com"
        };
    }
}
