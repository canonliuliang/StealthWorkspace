using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StealthWorkspace.Services;

public class BookmarkItem
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayHost => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.IdnHost : string.Empty;
    public string FolderId { get; init; } = "";
    public int? Position { get; init; }
    internal bool ContainsLoginInfo => Url.Contains('@') && Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.UserInfo.Length != 0;
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Url : Title;
    public string Title { get; set; } = string.Empty;
    public string Url { get; init; } = string.Empty;
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
    public bool LockOnSystemSessionEnd { get; set; }
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
    public ImmutableList<BookmarkItem> Bookmarks { get; set; } = [];
    public ImmutableList<BookmarkFolder> BookmarkFolders { get; set; } = [];

    internal AppSettingsData WithBookmarks(ImmutableList<BookmarkItem> bookmarks)
    {
        var copy = (AppSettingsData)MemberwiseClone();
        copy.Bookmarks = bookmarks;
        return copy;
    }

    // Cleaner Preferences
    public bool AutoCheckThumbcacheOnStartup { get; set; }
    public bool AutoFlushDnsOnExit { get; set; }
    public bool AutoWipeJumpList { get; set; } = true;

    // Camouflage
    public int CamouflageIndex { get; set; } = 0;
}

/// <summary>
/// Settings persistence. Writes are atomic (temp file + replace) and a .bak copy of the
/// last known good file is kept, so a crash while saving can never silently reset the
/// user's configuration.
/// </summary>
public partial class SettingsService
{
    private static readonly Lazy<SettingsService> Shared = new(() => new SettingsService { _bookmarkSession = null });
    public static SettingsService Instance => Shared.Value;

    private readonly string _settingsFilePath;
    private readonly string _settingsBackupPath;
    private readonly object _saveGate = new();
    // Transfer a warmed index between immutable snapshots under this short
    // cache lock. Readers never take the DPAPI write lock to look up a star.
    private readonly object _bookmarkIndexGate = new();
    private readonly record struct IndexedBookmark(BookmarkItem First, bool Multiple = false);
    private readonly ConditionalWeakTable<ImmutableList<BookmarkItem>, Dictionary<BrowserAddress.PageKey, IndexedBookmark>> _bookmarkIndexes = new();
    internal sealed class BookmarkSession(bool temporary)
    {
        internal bool Temporary { get; } = temporary;
        internal string? DownloadDirectoryName { get; } = temporary ? $"session-{Environment.ProcessId}-{Guid.NewGuid():N}" : null;
        internal volatile ImmutableList<BookmarkItem> Items = [];
        internal volatile ImmutableList<BookmarkFolder> Folders = [];
    }
    // Services opened directly against an isolated settings file work with its
    // persistent data. The application's shared instance starts locked.
    private volatile BookmarkSession? _bookmarkSession = new(false);
    private AppSettingsData? _realDataBeforeDecoy;
    internal BookmarkSession? ActiveBookmarkSession => _bookmarkSession;
    internal BookmarkSession? PersonalBookmarkSession => _bookmarkSession is { Temporary: false } current ? current : null;
    internal ImmutableList<BookmarkItem> CurrentBookmarks => BookmarksFor(_bookmarkSession);

    private ImmutableList<BookmarkItem> BookmarksFor(BookmarkSession? session) => session == null
        ? [] : session.Temporary ? session.Items : Data.Bookmarks;

    internal void SetBookmarkWorkspace(bool? decoy)
    {
        lock (_saveGate)
        {
            if (_bookmarkSession?.Temporary == true && decoy != true && _realDataBeforeDecoy != null)
            {
                Data = _realDataBeforeDecoy;
                _realDataBeforeDecoy = null;
            }
            else if (decoy == true && _bookmarkSession?.Temporary != true)
            {
                _realDataBeforeDecoy = Data;
                Data = Data.WithBookmarks([]);
                Data.BookmarkFolders = [];
            }

            if (_bookmarkSession?.Temporary == decoy) return;
            _bookmarkSession = decoy.HasValue ? new(decoy.Value) : null;
        }
    }

    public AppSettingsData Data { get; private set; }

    /// <summary>True when the persisted settings file existed but could not be read.</summary>
    public bool LoadFailed { get; private set; }
    public bool ReadDeferred { get; private set; }

    private enum SettingsReadState { Valid, Missing, Invalid, Unavailable }

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

            _ = TryLoadFrom(_settingsFilePath, out var existing);
            if (existing == SettingsReadState.Missing)
            {
                _ = TryLoadFrom(_settingsBackupPath, out var fallback);
                // A backup may be the only current copy. A legacy file must not
                // take precedence over it, including when it is temporarily busy.
                if (fallback != SettingsReadState.Missing) return;
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
            if (TryLoadFrom(_settingsFilePath, out _) == null) return;
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
        var loaded = TryLoadFrom(_settingsFilePath, out var primary);
        ReadDeferred = primary == SettingsReadState.Unavailable;
        LoadFailed = primary is SettingsReadState.Invalid or SettingsReadState.Unavailable;
        if (loaded != null) return loaded;

        if (primary == SettingsReadState.Invalid)
        {
            AppLog.Error("设置文件无法解密/解析，尝试备份", null);
            if (!BackupCorruptFile(_settingsFilePath)) ReadDeferred = true;
        }

        loaded = TryLoadFrom(_settingsBackupPath, out var fallback);
        ReadDeferred |= fallback == SettingsReadState.Unavailable;
        if (loaded != null)
        {
            AppLog.Warn(ReadDeferred ? "主设置暂不可访问，使用只读备份" : "已从设置备份恢复");
            LoadFailed = ReadDeferred;
            return loaded;
        }

        LoadFailed |= fallback != SettingsReadState.Missing;
        return new AppSettingsData();
    }

    private AppSettingsData? TryLoadFrom(string path, out SettingsReadState state)
    {
        state = SettingsReadState.Invalid;
        try
        {
            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return null;
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettingsData>(decrypted);
                if (settings != null) state = SettingsReadState.Valid;
                return settings;
            }
            finally { CryptographicOperations.ZeroMemory(decrypted); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            state = SettingsReadState.Missing;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            if (ex is IOException or UnauthorizedAccessException) state = SettingsReadState.Unavailable;
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
    public bool SaveSettings() => SaveSettings(null);

    private bool SaveSettings(ImmutableList<BookmarkItem>? bookmarkChange, Action? transferIndex = null, ImmutableList<BookmarkFolder>? folderChange = null)
    {
        lock (_saveGate)
        {
            if (ReadDeferred) return false;
            if (_bookmarkSession?.Temporary == true)
            {
                // Decoy sessions must never overwrite the real workspace's persisted settings on disk.
                Normalize(Data);
                NotifySettingsChanged();
                return true;
            }
            try
            {
                var snapshot = bookmarkChange == null ? Data : Data.WithBookmarks(bookmarkChange);
                if (folderChange != null) snapshot.BookmarkFolders = folderChange;
                Normalize(snapshot);
                byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot);
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
                if (bookmarkChange != null)
                {
                    transferIndex?.Invoke();
                    Data.Bookmarks = snapshot.Bookmarks;
                    Data.BookmarkFolders = snapshot.BookmarkFolders;
                }
                NotifySettingsChanged();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("保存设置失败", ex);
                return false;
            }
        }
    }

    private static bool BackupCorruptFile(string path)
    {
        try
        {
            string backup = Path.Combine(Path.GetDirectoryName(path)!, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dat");
            File.Move(path, backup);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clamps/validates every field so a corrupted or hand-edited file cannot cause
    /// out-of-range indexing, integer overflow or unexpected behaviour downstream.</summary>
    public void Normalize() => Normalize(Data);

    private static void Normalize(AppSettingsData d)
    {
        d.Bookmarks ??= [];
        d.BookmarkFolders ??= [];
        if (d.Bookmarks.Any(b => b == null || string.IsNullOrWhiteSpace(b.Url)))
            d.Bookmarks = d.Bookmarks.RemoveAll(b => b == null || string.IsNullOrWhiteSpace(b.Url));
        foreach (var b in d.Bookmarks)
        {
            b.Title ??= string.Empty;
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

    private void NotifySettingsChanged()
    {
        if (SettingsChanged is not { } changed) return;
        foreach (Action subscriber in changed.GetInvocationList())
            try { subscriber(); }
            catch (Exception ex) { AppLog.Warn("刷新设置视图失败: " + ex.GetType().Name); }
    }

    public bool IsBookmarked(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var bookmarks = CurrentBookmarks;
        if (bookmarks.Count >= 32)
            return FindIndexedBookmark(bookmarks, BrowserAddress.PageKey.From(url), out _);
        return bookmarks.Any(b => BrowserAddress.SamePage(b.Url, url));
    }

    internal BookmarkItem? FirstBookmark(ImmutableList<BookmarkItem> bookmarks, Uri address) =>
        bookmarks.Count < 32 ? bookmarks.Find(item => BrowserAddress.SamePage(address, item.Url)) :
        FindIndexedBookmark(bookmarks, BrowserAddress.PageKey.From(address), out var match) ? match.First : null;

    private bool FindIndexedBookmark(ImmutableList<BookmarkItem> bookmarks, BrowserAddress.PageKey key, out IndexedBookmark match)
    {
        lock (_bookmarkIndexGate)
        {
            var index = _bookmarkIndexes.GetValue(bookmarks, static items =>
            {
                var result = new Dictionary<BrowserAddress.PageKey, IndexedBookmark>(items.Count);
                foreach (var item in items)
                {
                    var address = BrowserAddress.PageKey.From(item.Url);
                    if (!result.TryAdd(address, new(item)) && !result[address].Multiple)
                        result[address] = result[address] with { Multiple = true };
                }
                return result;
            });
            return index.TryGetValue(key, out match);
        }
    }

    private static BookmarkItem NewBookmark(string? title, Uri uri, DateTime created, string folderId = "", int? position = null)
    {
        string name = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
        if (name.Length > 200) name = name[..(char.IsHighSurrogate(name[199]) ? 199 : 200)];
        return new BookmarkItem { Title = name, Url = uri.AbsoluteUri, CreatedAt = created, FolderId = folderId, Position = position };
    }

    public bool AddBookmark(string title, string url) => AddBookmark(title, url, _bookmarkSession);

    internal bool AddBookmark(string title, string url, BookmarkSession? session)
    {
        if (session == null) return false;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        lock (_saveGate)
        {
            if (!ReferenceEquals(_bookmarkSession, session)) return false;
            var bookmarks = BookmarksFor(session);
            var key = BrowserAddress.PageKey.From(uri);
            if (bookmarks.Count >= 32 ? FindIndexedBookmark(bookmarks, key, out _) : bookmarks.Any(b => BrowserAddress.SamePage(uri, b.Url))) return true;
            var item = NewBookmark(title, uri, DateTime.Now);
            return SaveBookmarks(session, bookmarks.Insert(0, item), index => index[key] = new(item));
        }
    }

    public bool RemoveBookmark(string url) => RemoveBookmark(url, _bookmarkSession);

    internal bool RemoveBookmark(string url, BookmarkSession? session)
    {
        if (session == null) return false;
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_saveGate)
        {
            if (!ReferenceEquals(_bookmarkSession, session)) return false;
            var bookmarks = BookmarksFor(session);
            var key = BrowserAddress.PageKey.From(url);
            IndexedBookmark match = default;
            if (bookmarks.Count >= 32 && !FindIndexedBookmark(bookmarks, key, out match)) return true;
            var updated = match is { First: not null, Multiple: false } ? bookmarks.Remove(match.First) : bookmarks.RemoveAll(b => BrowserAddress.SamePage(b.Url, url));
            return updated.Count == bookmarks.Count || SaveBookmarks(session, updated, index => index.Remove(key));
        }
    }

    public bool RenameBookmark(string url, string title) => RenameBookmark(url, title, _bookmarkSession);

    internal bool RenameBookmark(string url, string title, BookmarkSession? session)
    {
        if (session == null) return false;
        title = title.Trim();
        if (title.Length == 0) return false;
        lock (_saveGate)
        {
            if (!ReferenceEquals(_bookmarkSession, session)) return false;
            var bookmarks = BookmarksFor(session);
            var key = BrowserAddress.PageKey.From(url);
            IndexedBookmark match = default;
            if (bookmarks.Count >= 32 && !FindIndexedBookmark(bookmarks, key, out match)) return false;
            var item = match.First ?? bookmarks.FirstOrDefault(b => BrowserAddress.SamePage(b.Url, url));
            if (item == null) return false;
            if (item.Title == title) return true;
            if (title.Length > 200) title = title[..(char.IsHighSurrogate(title[199]) ? 199 : 200)];
            var replacement = new BookmarkItem { Title = title, Url = item.Url, CreatedAt = item.CreatedAt, FolderId = item.FolderId, Position = item.Position };
            return SaveBookmarks(session, bookmarks.Replace(item, replacement), index => index[key] = new(replacement, match.Multiple));
        }
    }

    internal bool ImportBookmarks(IReadOnlyList<BookmarkItem> incoming, out int added, CancellationToken token = default, BookmarkSession? expectedSession = null)
    {
        added = 0;
        var session = expectedSession ?? PersonalBookmarkSession;
        if (session == null || session.Temporary) return false;
        token.ThrowIfCancellationRequested();
        lock (_saveGate)
        {
            if (!ReferenceEquals(session, _bookmarkSession)) return false;
            var bookmarks = BookmarksFor(session);
            var newKeys = new Dictionary<BrowserAddress.PageKey, BookmarkItem>();
            var additions = new List<BookmarkItem>();
            foreach (var item in incoming)
            {
                token.ThrowIfCancellationRequested();
                if (!Uri.TryCreate(item.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                    uri.UserInfo.Length != 0) continue;
                var key = BrowserAddress.PageKey.From(uri);
                if (newKeys.ContainsKey(key) || FindIndexedBookmark(bookmarks, key, out _)) continue;
                var fresh = NewBookmark(item.Title, uri, item.CreatedAt);
                newKeys.Add(key, fresh);
                additions.Add(fresh);
            }
            token.ThrowIfCancellationRequested();
            if (additions.Count == 0) return true;
            if (!SaveBookmarks(session, bookmarks.InsertRange(0, additions), index =>
                { foreach (var pair in newKeys) index[pair.Key] = new(pair.Value); })) return false;
            added = additions.Count;
            return true;
        }
    }

    // The caller holds _saveGate from the initial snapshot through persistence.
    private bool SaveBookmarks(BookmarkSession session, ImmutableList<BookmarkItem> updated, Action<Dictionary<BrowserAddress.PageKey, IndexedBookmark>> updateIndex)
    {
        if (!ReferenceEquals(session, _bookmarkSession)) return false;
        var previous = BookmarksFor(session);
        void TransferIndex()
        {
            lock (_bookmarkIndexGate)
            {
                if (updated.Count >= 32 && _bookmarkIndexes.TryGetValue(previous, out var index))
                {
                    _bookmarkIndexes.Remove(previous);
                    updateIndex(index);
                    _bookmarkIndexes.AddOrUpdate(updated, index);
                }
            }
        }
        if (session.Temporary)
        {
            TransferIndex();
            session.Items = updated;
            NotifySettingsChanged();
            return true;
        }
        return SaveSettings(updated, TransferIndex);
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
