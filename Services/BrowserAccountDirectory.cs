using System.Security.Cryptography;
using System.Text;

namespace StealthWorkspace.Services;

internal static class BrowserAccountDirectory
{
    internal const string OwnerFile = ".stealth-account-owner";
    private const string IdentityFile = ".stealth-vault-binding";

    private static string RootKey(string vaultRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(vaultRoot)).ToUpperInvariant()))).ToLowerInvariant();

    internal static string GroupForVault(string vaultRoot)
    {
        // Chromium appends origin-specific IndexedDB and LevelDB filenames.
        // Test roots already isolate individual vaults, so no hash is needed in
        // that path. The ownership check always uses the full 256-bit identity.
        if (AppPaths.HasDataRootOverride) return Path.Combine(Path.GetFullPath(vaultRoot), "BrowserAccounts", "v");
        string low = BrowserDownloadFolders.GetLocalAppDataLow() ?? throw new IOException("Windows 未提供当前用户的网站账户存储位置。");
        return Path.Combine(low, "StealthWorkspace", "BrowserAccounts", RootKey(vaultRoot)[..16]);
    }

    internal static string ForVault(string vaultRoot, string identity)
    {
        if (!Guid.TryParseExact(identity, "N", out var id) || id == Guid.Empty) throw new IOException("保险箱网站账户标识无效。");
        return Path.Combine(GroupForVault(vaultRoot), id.ToString("N")[..16]);
    }

    internal static FileStream Acquire(string vaultRoot, string directory, string identity)
    {
        if (!string.Equals(directory, ForVault(vaultRoot, identity), StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法安全定位此保险箱的网站账户目录。");
        // One lease covers the group during reset and the native profile's
        // entire lifetime. Both truncated path keys are checked against
        // their full, non-secret values before WebView2 can open an existing UDF.
        var groupLease = AcquireOwnedDirectory(GroupForVault(vaultRoot), OwnerFile, RootKey(vaultRoot));
        try
        {
            using var checkedIdentity = AcquireOwnedDirectory(directory, IdentityFile, identity);
            return groupLease;
        }
        catch { groupLease.Dispose(); throw; }
    }

    private static FileStream AcquireOwnedDirectory(string directory, string markerFile, string expected)
    {
        SafeFileSystem.EnsureWorkspaceDirectory(directory);
        string marker = Path.Combine(directory, markerFile);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("网站账户目录已存在，但无法确认属于此保险箱。");
            using var created = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Write(Encoding.ASCII.GetBytes(expected));
            created.Flush(true);
        }
        if (!SafeFileSystem.IsPlainWorkspacePath(marker)) throw new IOException("网站账户目录的所有权标记不可用。");
        var lease = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            VerifyOwner(lease,expected);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void VerifyOwner(FileStream lease, string identity)
    {
        byte[] expected = Encoding.ASCII.GetBytes(identity);
        byte[] actual = new byte[expected.Length];
        if (lease.Length != actual.Length) throw new IOException("网站账户目录所有权不符。");
        lease.ReadExactly(actual);
        if (!actual.SequenceEqual(expected)) throw new IOException("网站账户目录所有权不符。");
    }

    // A portable vault may have been swapped at the same path. Retire only
    // the identified catalog's profile, leaving other catalogs' website data intact.
    internal static async Task RemoveForReplacementAsync(string vaultRoot, string identity)
    {
        string directory = ForVault(vaultRoot, identity);
        try { _ = File.GetAttributes(directory); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        await BrowserProfileManager.ReleaseProfileForReplacementAsync(directory);
        await Task.Run(async () =>
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!SafeFileSystem.IsPlainWorkspacePath(directory)) throw new IOException("网站账户目录包含重定向，无法安全清理。");
                    var root = new DirectoryInfo(directory);
                    using var group = AcquireOwnedDirectory(GroupForVault(vaultRoot), OwnerFile, RootKey(vaultRoot));
                    string marker = Path.Combine(directory, IdentityFile);
                    if (!SafeFileSystem.IsPlainWorkspacePath(marker)) throw new IOException("无法验证网站账户目录所有权。");
                    // Always preserve the sentinel when a child remains locked. A failed
                    // reset must be resumable without ever adopting an unrelated folder.
                    using (var owned = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    {
                        VerifyOwner(owned,identity);
                        foreach (var entry in root.EnumerateFileSystemInfos())
                        {
                            if (entry.Name == IdentityFile) continue;
                            if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) child.Delete(true);
                            else entry.Delete();
                        }
                        if (root.EnumerateFileSystemInfos().Any(x => x.Name != IdentityFile)) throw new IOException("浏览器仍在释放网站账户文件。");
                        File.Delete(marker);
                    }
                    try { root.Delete(); }
                    catch
                    {
                        if (root.Exists && SafeFileSystem.IsPlainWorkspacePath(directory))
                        {
                            using var repaired = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            repaired.Write(Encoding.ASCII.GetBytes(identity));
                            repaired.Flush(true);
                        }
                        throw;
                    }
                    return;
                }
                catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException) { await Task.Delay(200); }
            }
        });
    }
}
