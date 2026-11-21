using System.Runtime.InteropServices;

namespace StealthWorkspace.Services;

internal static class WorkspacePathSelfTest
{
    internal static async Task RunAsync(string testRoot, Action<bool, string, string> report)
    {
        // Exercise the native handle paths even when CI places its report beside C:\Temp.
        string root = Path.Combine(testRoot, "owned-long-path");
        while (root.Length < 272) root = Path.Combine(root, "synthetic-long-directory");
        Directory.CreateDirectory(root);

        string[] invalid = [
            @"\\?\GLOBALROOT\Device", @"\\.\NUL", @"\??\C:\Temp", "//?/C:/Temp",
            @"\\localhost\pipe\synthetic", Path.Combine(root, "NUL.txt"), Path.Combine(root, "COM¹.dat"),
            Path.Combine(root, "name:stream"), Path.Combine(root, "ambiguous.")
        ];
        bool refused = invalid.All(path =>
        {
            try { _ = WorkspaceFileName.ForWin32(path); return false; }
            catch (IOException) { return true; }
        });
        string unc = @"\\server\share\" + new string('a', 110) + @"\" + new string('b', 150);
        report(refused && WorkspaceFileName.ForWin32(unc) == @"\\?\UNC\" + unc[2..],
            "Only normalized filesystem paths enter the long-path namespace, including ordinary UNC shares", "");

        const string password = "LongPathFixture#42";
        string source = Path.Combine(root, "source.txt"), output = Path.Combine(root, "recovered.txt");
        string vaultPath = Path.Combine(root, "vault");
        await File.WriteAllTextAsync(source, "深路径 synthetic content");
        try
        {
            var vault = new VaultService(vaultPath);
            await vault.InitializeVaultAsync(password);
            var first = await vault.ImportFileAsync(source, false);
            var second = await vault.ImportFileAsync(source, false);
            vault.Lock();
            if (!vault.ApplyUnlock(vault.VerifyPassword(password))) throw new IOException("Synthetic unlock failed");
            first = vault.VisibleItems.Single(item => item.Id == first.Id);
            second = vault.VisibleItems.Single(item => item.Id == second.Id);
            await vault.ExportItemAsync(first, output);
            string media = Path.Combine(vaultPath, "MediaVault");
            string ciphertext = Path.Combine(media, first.StoredFileName);
            bool bounded = false;
            try { await vault.ExportItemAsync(first, Path.Combine(media, "must-stay-out.txt")); }
            catch (InvalidOperationException) { bounded = true; }
            bool restored = await File.ReadAllTextAsync(output) == await File.ReadAllTextAsync(source);
            await vault.DeleteItemAsync(first);
            bool removed = !File.Exists(ciphertext) && vault.VisibleItems.Single().Id == second.Id;
            await vault.ResetVaultAsync();
            bool reset = !new VaultService(vaultPath).HasVault && !Directory.EnumerateFileSystemEntries(media).Any()
                && !File.Exists(Path.Combine(vaultPath, "remove.dat"));
            report(restored && bounded && removed && reset && File.Exists(source),
                "Deep vault paths support import, reopen, export, item removal and reset while protecting the vault boundary",
                $"ciphertext path length={ciphertext.Length}");
        }
        catch (Exception ex) { report(false, "Deep vault paths support import, reopen, export, item removal and reset while protecting the vault boundary", ex.ToString()); }

        string sibling = Path.Combine(root, "keep-readonly.txt"), download = Path.Combine(root, "decoy-download-name.txt");
        try
        {
            File.WriteAllText(sibling, "keep the original bytes");
            if (!CreateHardLinkW(WorkspaceFileName.ForWin32(download), WorkspaceFileName.ForWin32(sibling), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            File.SetAttributes(sibling, FileAttributes.ReadOnly);
            bool refusedOverwrite = !CryptoService.ShredWorkspaceFile(download, 1) && File.Exists(download);
            SafeFileSystem.DeleteReadOnlyFile(download, workspace: true);
            report(refusedOverwrite && !File.Exists(download) && File.ReadAllText(sibling) == "keep the original bytes" &&
                File.GetAttributes(sibling).HasFlag(FileAttributes.ReadOnly),
                "Deep temporary downloads unlink only their own hard-link name while vault overwrites refuse linked content", "");
        }
        catch (Exception ex) { report(false, "Deep temporary downloads unlink only their own hard-link name while vault overwrites refuse linked content", ex.ToString()); }
        finally { if (File.Exists(sibling)) File.SetAttributes(sibling, FileAttributes.Normal); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr reserved);
}
