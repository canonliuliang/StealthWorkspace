using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StealthWorkspace.Services;

internal static class RemovalSelfTest
{
    private sealed class FixtureUnavailableException(string message) : Exception(message);
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
    private sealed class FractionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    internal static async Task RunAsync(string testRoot, Action<bool, string, string> report, Action<string, string> skip)
    {
        string root = Path.Combine(testRoot, "removal-regression");
        Directory.CreateDirectory(root);
        byte[] bytes = RandomNumberGenerator.GetBytes(96 * 1024 + 13);
        string source = Path.Combine(root, "source.bin"); File.WriteAllBytes(source, bytes);
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Removal regression assertion failed"); }
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(true, name, ""); }
            catch (FixtureUnavailableException ex) { skip(name, ex.Message); }
            catch (Exception ex) { report(false, name, ex.ToString()); }
        }
        static async Task ExpectFailure(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
            throw new InvalidOperationException("Expected operation to report failure");
        }
        async Task<VaultService> NewVault(string name, bool decoy = false)
        {
            var vault = new VaultService(Path.Combine(root, name));
            await vault.InitializeVaultAsync("RemovalMain#123", decoy ? "RemovalDecoy#123" : null);
            return vault;
        }
        static void Unlock(VaultService vault, bool decoy = false)
        {
            vault.Lock(); Require(vault.ApplyUnlock(vault.VerifyPassword(decoy ? "RemovalDecoy#123" : "RemovalMain#123")));
        }
        void VerifyRemaining(VaultService vault)
        {
            using var clear = vault.OpenDecryptedStream(vault.VisibleItems.Single());
            using var copy = new MemoryStream(); clear.CopyTo(copy);
            Require(copy.ToArray().AsSpan().SequenceEqual(bytes));
        }
        string MediaPath(string name, string file) => Path.Combine(root, name, "MediaVault", file);

        await Check("Cleaner: thumbnail shredding retains linked files and leaves icon caches alone", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "thumbnail-cache")).FullName;
            string link = Path.Combine(directory, "thumbcache_link.db");
            string cache = Path.Combine(directory, "thumbcache_32.db");
            string icon = Path.Combine(directory, "iconcache_32.db");
            File.WriteAllBytes(cache, bytes);
            File.WriteAllBytes(icon, bytes);
            Require(CreateHardLinkW(link, source, IntPtr.Zero));
            var item = new CleanerItem { Category = CleanerCategory.ThumbnailCache };
            var options = new CleanerOptions { SecureShredFiles = true };
            CleanerItem result = CleanerService.CleanThumbnails(item, directory, options, CancellationToken.None);
            Require(result.DetectedCount == 1 && result.Outcome == CleanOutcome.Skipped && File.Exists(link) && !File.Exists(cache));
            options.SecureShredFiles = false;
            string readOnlyCache = Path.Combine(directory, "thumbcache_readonly.db");
            File.WriteAllBytes(readOnlyCache, bytes);
            File.SetAttributes(readOnlyCache, FileAttributes.ReadOnly);
            result = CleanerService.CleanThumbnails(item, directory, options, CancellationToken.None);
            Require(result.DetectedCount == 0 && result.Outcome == CleanOutcome.Complete && !File.Exists(link) && !File.Exists(readOnlyCache));
            Require(File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes) && File.ReadAllBytes(icon).AsSpan().SequenceEqual(bytes));
            return Task.CompletedTask;
        });
        await Check("Cleaner: normal deletion preserves the read-only bit on a sibling hard link", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "readonly-links")).FullName;
            string keep = Path.Combine(directory, "keep.bin"), cache = Path.Combine(directory, "thumbcache_link.db");
            File.WriteAllBytes(keep, bytes);
            Require(CreateHardLinkW(cache, keep, IntPtr.Zero));
            File.SetAttributes(keep, FileAttributes.ReadOnly);
            try
            {
                var result = CleanerService.CleanThumbnails(new CleanerItem(), directory, new CleanerOptions(), default);
                Require(result.DetectedCount == 0 && !File.Exists(cache));
                Require(File.ReadAllBytes(keep).AsSpan().SequenceEqual(bytes) && (File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0);
            }
            finally { File.SetAttributes(keep, FileAttributes.Normal); }
            return Task.CompletedTask;
        });
        await Check("Cleaner: a held read-only file is counted as skipped in both modes and can be retried", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "busy-cache")).FullName;
            string cache = Path.Combine(directory, "thumbcache_busy.db");
            File.WriteAllBytes(cache, bytes);
            File.SetAttributes(cache, FileAttributes.ReadOnly);
            using (var reader = File.Open(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                foreach (bool shred in new[] { true, false })
                {
                    var result = CleanerService.CleanThumbnails(new CleanerItem(), directory, new CleanerOptions { SecureShredFiles = shred }, default);
                    Require(result.Outcome == CleanOutcome.Skipped && result.DetectedCount == 1);
                    Require(File.ReadAllBytes(cache).AsSpan().SequenceEqual(bytes) && (File.GetAttributes(cache) & FileAttributes.ReadOnly) != 0);
                }
            }
            Require(CleanerService.CleanThumbnails(new CleanerItem(), directory, new CleanerOptions(), default).DetectedCount == 0);
            return Task.CompletedTask;
        });
        await Check("Cleaner: shutdown waits for active work and cancellation skips queued work", async () =>
        {
            var cleaner = CleanerService.Instance;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            // An unknown category can exercise dispatch without touching any real system settings or files.
            CleanerItem[] items = [new() { Category = (CleanerCategory)(-1) }];
            var options = new CleanerOptions();
            var progress = new InlineProgress(_ => { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); });
            Task active = cleaner.CleanAsync(items, options, progress, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int queuedReports = 0;
            Task queued = cleaner.CleanAsync(items, options, new InlineProgress(_ => Interlocked.Increment(ref queuedReports)), cancellation.Token);
            Task shutdown = cleaner.WaitForCleaningToFinishAsync();
            try
            {
                Require(!shutdown.IsCompleted);
                cancellation.Cancel();
            }
            finally { release.Set(); }
            foreach (Task operation in new[] { active, queued })
            {
                try { await operation; throw new InvalidOperationException("Cleanup ignored cancellation"); }
                catch (OperationCanceledException) when (operation.IsCanceled) { }
            }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Require(queuedReports == 0);
        });
        await Check("Removal: read-only source can be imported and shredded", async () =>
        {
            var vault = await NewVault("readonly-import");
            string readOnly = Path.Combine(root, "readonly.bin"); File.WriteAllBytes(readOnly, bytes);
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            bool removed = false;
            await vault.ImportFileAsync(readOnly, true, reportOriginalRemoval: value => removed = value);
            Require(removed && !File.Exists(readOnly)); VerifyRemaining(vault); vault.Lock();
        });
        await Check("Removal: read-only hard link preserves data and attributes", () =>
        {
            string original = Path.Combine(root, "readonly-original.bin"), link = Path.Combine(root, "readonly-link.bin");
            File.WriteAllBytes(original, bytes); Require(CreateHardLinkW(link, original, IntPtr.Zero));
            File.SetAttributes(original, FileAttributes.ReadOnly);
            try
            {
                Require(!CryptoService.ShredFile(link, 1));
                Require(File.Exists(link) && (File.GetAttributes(original) & FileAttributes.ReadOnly) != 0);
                Require(File.ReadAllBytes(original).AsSpan().SequenceEqual(bytes));
            }
            finally { File.SetAttributes(original, FileAttributes.Normal); File.Delete(link); }
            return Task.CompletedTask;
        });
        await Check("Removal: failed read-only wipe restores the read-only bit", () =>
        {
            string file = Path.Combine(root, "readonly-busy.bin"); File.WriteAllBytes(file, bytes);
            File.SetAttributes(file, FileAttributes.ReadOnly);
            using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                Require(!CryptoService.ShredFile(file, 1) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
            Require(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes));
            Require(CryptoService.ShredFile(file, 1) && !File.Exists(file));
            return Task.CompletedTask;
        });
        await Check("Removal: SUBST alias accepts the same file identity", () =>
        {
            uint drives = GetLogicalDrives();
            int letter = Enumerable.Range(3, 23).Reverse().First(index => (drives & (1u << index)) == 0);
            string drive = ((char)('A' + letter)) + ":", target = @"\??\" + Path.GetFullPath(root);
            if (!DefineDosDeviceW(0x1 | 0x8, drive, target))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 5) throw new FixtureUnavailableException("The host denies creation of temporary DOS drive mappings (ERROR_ACCESS_DENIED); SUBST was not exercised.");
                throw new Win32Exception(error);
            }
            try
            {
                string file = Path.Combine(root, "drive-alias.bin"); File.WriteAllBytes(file, bytes);
                Require(CryptoService.ShredFile(drive + @"\drive-alias.bin", 1) && !File.Exists(file));
            }
            finally
            {
                // Remove only this exact temporary mapping, preserving any other DOS definition.
                if (!DefineDosDeviceW(0x1 | 0x2 | 0x4 | 0x8, drive, target)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return Task.CompletedTask;
        });
        string longName = Path.Combine(root, "long filename for short-name compatibility.bin"); File.WriteAllBytes(longName, bytes);
        var shortName = new StringBuilder(32768);
        uint shortLength = GetShortPathNameW(Path.GetFullPath(longName), shortName, (uint)shortName.Capacity);
        if (shortLength > 0 && shortLength < shortName.Capacity && !string.Equals(shortName.ToString(), Path.GetFullPath(longName), StringComparison.OrdinalIgnoreCase))
            await Check("Removal: actual 8.3 alias accepts the same file identity", () =>
            {
                Require(CryptoService.ShredFile(shortName.ToString(), 1) && !File.Exists(longName)); return Task.CompletedTask;
            });
        else skip("Removal: actual 8.3 alias accepts the same file identity", "The fixture volume did not provide a distinct short name.");

        await Check("Removal: busy item remains indexed and readable, retry removes it", async () =>
        {
            var vault = await NewVault("busy-item"); var item = await vault.ImportFileAsync(source, false);
            string path = MediaPath("busy-item", item.StoredFileName);
            byte[] ciphertext = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(() => vault.DeleteItemAsync(item));
                Require(vault.VisibleItems.Count == 1 && File.ReadAllBytes(path).AsSpan().SequenceEqual(ciphertext));
                await ExpectFailure(() => vault.CreateBackupAsync(Path.Combine(root, "pending-backup.svbackup")));
            }
            vault.Lock(); vault = new VaultService(Path.Combine(root, "busy-item")); Unlock(vault); VerifyRemaining(vault);
            await vault.DeleteItemAsync(vault.VisibleItems.Single());
            Require(vault.VisibleItems.Count == 0 && !File.Exists(path) && !File.Exists(Path.Combine(root, "busy-item", "remove.dat")));
            File.WriteAllBytes(Path.Combine(root, "busy-item", "system_catalog.dat"), [1, 2, 3]);
            var fallback = new VaultService(Path.Combine(root, "busy-item")); Unlock(fallback); Require(fallback.VisibleItems.Count == 0); fallback.Lock();
        });
        await Check("Library: rename preserves ciphertext, batch exports preserve existing names and partial deletion retries", async () =>
        {
            var vault = await NewVault("library-management");
            var first = await vault.ImportFileAsync(source, false);
            var second = await vault.ImportFileAsync(source, false);
            string firstPath = MediaPath("library-management", first.StoredFileName);
            string secondPath = MediaPath("library-management", second.StoredFileName);
            byte[] ciphertext = File.ReadAllBytes(firstPath);
            await vault.RenameItemAsync(first, "新名称.bin");
            Require(File.ReadAllBytes(firstPath).AsSpan().SequenceEqual(ciphertext));
            vault.Lock();
            vault = new VaultService(Path.Combine(root, "library-management"));
            Unlock(vault);
            first = vault.VisibleItems.Single(item => item.OriginalFileName == "新名称.bin");
            second = vault.VisibleItems.Single(item => item.Id == second.Id);
            string folder = Directory.CreateDirectory(Path.Combine(root, "library-exports")).FullName;
            string target = Path.Combine(folder, "同名.bin");
            File.WriteAllText(target, "existing");
            await vault.ExportItemAsync(first, target, useUniqueName: true);
            await vault.ExportItemAsync(second, target, useUniqueName: true);
            Require(File.ReadAllText(target) == "existing" &&
                File.ReadAllBytes(Path.Combine(folder, "同名 (2).bin")).AsSpan().SequenceEqual(bytes) &&
                File.ReadAllBytes(Path.Combine(folder, "同名 (3).bin")).AsSpan().SequenceEqual(bytes));
            using (var held = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(() => vault.DeleteItemsAsync([first, second]));
                Require(!File.Exists(firstPath) && vault.VisibleItems.Single().Id == second.Id);
            }
            await vault.DeleteItemsAsync([second]);
            Require(vault.VisibleItems.Count == 0 && !File.Exists(secondPath));
            vault.Lock();
        });
        await Check("Removal: retrying one item preserves other previously blocked items and their journal", async () =>
        {
            var vault = await NewVault("subset-retry");
            var first = await vault.ImportFileAsync(source, false);
            var second = await vault.ImportFileAsync(source, false);
            string firstPath = MediaPath("subset-retry", first.StoredFileName);
            string secondPath = MediaPath("subset-retry", second.StoredFileName);
            string journal = Path.Combine(root, "subset-retry", "remove.dat");
            using (var firstReader = File.Open(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var secondReader = File.Open(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await ExpectFailure(() => vault.DeleteItemsAsync([first, second]));
            await vault.DeleteItemAsync(first);
            Require(!File.Exists(firstPath) && File.Exists(secondPath) && File.Exists(journal));
            VerifyRemaining(vault);
            await vault.DeleteItemAsync(second);
            Require(!File.Exists(secondPath) && !File.Exists(journal));
            vault.Lock();
        });
        await Check("Removal: partial decoy clear preserves failed item and supports retry", async () =>
        {
            var vault = await NewVault("busy-decoy", true); await vault.ImportFileAsync(source, false);
            Unlock(vault, true); var first = await vault.ImportFileAsync(source, false); var second = await vault.ImportFileAsync(source, false);
            Unlock(vault);
            using (var held = new FileStream(MediaPath("busy-decoy", second.StoredFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(vault.ClearDecoyAsync);
                Require(vault.HasDecoyVault && !File.Exists(MediaPath("busy-decoy", first.StoredFileName)));
                VerifyRemaining(vault); Unlock(vault, true); VerifyRemaining(vault);
            }
            vault.Lock(); vault = new VaultService(Path.Combine(root, "busy-decoy")); Unlock(vault);
            await vault.ClearDecoyAsync(); Require(!vault.HasDecoyVault); VerifyRemaining(vault);
            Require(Directory.GetFiles(Path.Combine(root, "busy-decoy", "MediaVault")).Length == 1); vault.Lock();
        });
        await Check("Removal: a main-space decoy clear can finish a previously blocked decoy-item removal", async () =>
        {
            var vault = await NewVault("decoy-escalate", true);
            await vault.ImportFileAsync(source, false);
            Unlock(vault, true);
            var item = await vault.ImportFileAsync(source, false);
            await vault.ImportFileAsync(source, false);
            using (var reader = File.Open(MediaPath("decoy-escalate", item.StoredFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
                await ExpectFailure(() => vault.DeleteItemAsync(item));
            Unlock(vault);
            await vault.ClearDecoyAsync();
            Require(!vault.HasDecoyVault && !File.Exists(Path.Combine(root, "decoy-escalate", "remove.dat")));
            VerifyRemaining(vault);
            vault.Lock();
        });
        await Check("Removal: the next write finishes a committed password rotation after a reader releases the old file", async () =>
        {
            var vault = await NewVault("rotation-retry");
            var item = await vault.ImportFileAsync(source, false);
            string old = MediaPath("rotation-retry", item.StoredFileName);
            string journal = Path.Combine(root, "rotation-retry", "rotate.dat");
            using (var reader = File.Open(old, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await vault.ChangeMasterPasswordAsync("RotatedMain#123");
                Require(vault.IsUnlocked && File.Exists(journal) && File.Exists(old));
                await ExpectFailure(() => vault.ImportFileAsync(source, false));
            }
            await vault.RenameItemAsync(vault.VisibleItems.Single(), "renamed.bin");
            Require(!File.Exists(journal) && !File.Exists(old));
            VerifyRemaining(vault);
            vault.Lock();
        });
        await Check("Removal: cancellation after decrypting export data retains the previous target and removes the part file", async () =>
        {
            var vault = await NewVault("export-cancel");
            var item = await vault.ImportFileAsync(source, false);
            string folder = Directory.CreateDirectory(Path.Combine(root, "cancelled-export")).FullName;
            string target = Path.Combine(folder, "saved.bin");
            File.WriteAllText(target, "keep");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await vault.ExportItemAsync(item, target, new FractionProgress(f => { if (f > 0) cancellation.Cancel(); }), cancellation.Token);
                throw new InvalidOperationException("Export unexpectedly committed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            if (File.ReadAllText(target) != "keep") throw new InvalidOperationException("A canceled export replaced the destination.");
            if (Directory.EnumerateFiles(folder, "*.part").Any()) throw new InvalidOperationException("A canceled export left its temporary file behind.");
            vault.Lock();
        });
        await Check("Removal: journal write failure leaves ciphertext and index untouched", async () =>
        {
            var vault = await NewVault("journal-failure"); var item = await vault.ImportFileAsync(source, false);
            string temporary = Path.Combine(root, "journal-failure", "remove.dat.tmp"); Directory.CreateDirectory(temporary);
            try { await ExpectFailure(() => vault.DeleteItemAsync(item)); VerifyRemaining(vault); }
            finally { Directory.Delete(temporary, false); }
            await vault.DeleteItemAsync(item); Require(vault.VisibleItems.Count == 0); vault.Lock();
        });
        await Check("Removal: index write failure is recoverable after payload deletion", async () =>
        {
            var vault = await NewVault("catalog-failure"); var doomed = await vault.ImportFileAsync(source, false); await vault.ImportFileAsync(source, false);
            string catalog = Path.Combine(root, "catalog-failure", "system_catalog.dat");
            using (var held = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(() => vault.DeleteItemAsync(doomed));
                Require(!File.Exists(MediaPath("catalog-failure", doomed.StoredFileName)) && File.Exists(Path.Combine(root, "catalog-failure", "remove.dat")));
            }
            vault.Lock(); vault = new VaultService(Path.Combine(root, "catalog-failure")); Unlock(vault); VerifyRemaining(vault);
            Require(!File.Exists(Path.Combine(root, "catalog-failure", "remove.dat"))); vault.Lock();
        });
        await Check("Removal: failed backup-index sync retains recovery intent", async () =>
        {
            var vault = await NewVault("backup-failure"); var doomed = await vault.ImportFileAsync(source, false);
            using (var held = new FileStream(Path.Combine(root, "backup-failure", "system_catalog.dat.bak"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await ExpectFailure(() => vault.DeleteItemAsync(doomed));
            Require(File.Exists(Path.Combine(root, "backup-failure", "remove.dat")));
            vault.Lock(); vault = new VaultService(Path.Combine(root, "backup-failure")); Unlock(vault); Require(vault.VisibleItems.Count == 0);
            Require(!File.Exists(Path.Combine(root, "backup-failure", "remove.dat"))); vault.Lock();
        });
        await Check("Removal: failed reset remains nonempty across restart and can restore after retry", async () =>
        {
            var vault = await NewVault("reset-busy"); var item = await vault.ImportFileAsync(source, false);
            string backup = Path.Combine(root, "reset-backup.svbackup"); await vault.CreateBackupAsync(backup);
            using (var held = new FileStream(MediaPath("reset-busy", item.StoredFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(vault.ResetVaultAsync); Require(vault.HasVault && vault.IsCorrupted);
                vault = new VaultService(Path.Combine(root, "reset-busy")); Require(vault.HasVault && vault.IsCorrupted);
                await ExpectFailure(() => vault.RestoreBackupAsync(backup));
                await ExpectFailure(() => vault.InitializeVaultAsync("MustNotReplace#123"));
            }
            await vault.ResetVaultAsync(); Require(!vault.HasVault && !vault.IsCorrupted);
            await vault.RestoreBackupAsync(backup); Unlock(vault); VerifyRemaining(vault); vault.Lock();
        });
        await Check("Removal: reset metadata failure cannot advertise an empty vault", async () =>
        {
            var vault = await NewVault("reset-catalog"); await vault.ImportFileAsync(source, false);
            using (var held = new FileStream(Path.Combine(root, "reset-catalog", "system_catalog.dat"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ExpectFailure(vault.ResetVaultAsync); Require(vault.HasVault && vault.IsCorrupted);
            }
            vault = new VaultService(Path.Combine(root, "reset-catalog")); Require(vault.HasVault && vault.IsCorrupted);
            await vault.ResetVaultAsync(); Require(!vault.HasVault && !vault.IsCorrupted);
        });
        await Check("Removal: a journal-only legacy root remains the selected vault", () =>
        {
            string legacy = Path.Combine(root, "legacy-reset"), local = Path.Combine(root, "local-reset"), portable = Path.Combine(root, "portable-reset");
            Directory.CreateDirectory(legacy); File.WriteAllBytes(Path.Combine(legacy, "remove.dat"), [1, 2, 3]);
            Require(AppPaths.ResolveDataRoot(legacy, local, portable) == legacy);
            var incomplete = new VaultService(legacy);
            Require(incomplete.HasVault && incomplete.IsCorrupted);
            return Task.CompletedTask;
        });
        foreach (bool shell in new[] { true, false })
            await Check("Process: ArgumentList works with UseShellExecute=" + shell, async () =>
            {
                var launch = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                { UseShellExecute = shell, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
                foreach (string arg in new[] { "/d", "/c", "exit", "23" }) launch.ArgumentList.Add(arg);
                using var child = Process.Start(launch) ?? throw new InvalidOperationException("No process handle returned");
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Require(child.ExitCode == 23); }
                finally { if (!child.HasExited) child.Kill(); }
            });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDeviceW(uint flags, string name, string target);
    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);
}
