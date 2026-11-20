using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

/// <summary>Destructive regression fixtures are confined to SelfTest's disposable root.</summary>
internal static class AuditSelfTest
{
    internal static async Task RunAsync(string root, Action<bool, string, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(true, name, ""); }
            catch (Exception ex) { report(false, name, ex.ToString()); }
        }
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Regression assertion failed"); }
        string source = Path.Combine(root, "audit-source.bin");
        byte[] original = RandomNumberGenerator.GetBytes(1024 * 1024 + 37);
        File.WriteAllBytes(source, original);
        string vaultRoot = Path.Combine(root, "audit-vault");
        var vault = new VaultService(vaultRoot);
        await vault.InitializeVaultAsync("Main-A#123", "Decoy-A#123");
        VaultItem item = await vault.ImportFileAsync(source, false);
        vault.Lock();
        Require(vault.ApplyUnlock(vault.VerifyPassword("Decoy-A#123")));
        await vault.ImportFileAsync(source, false);
        vault.Lock();
        Require(vault.ApplyUnlock(vault.VerifyPassword("Main-A#123")));

        void VerifyContents(VaultService target)
        {
            Require(target.VisibleItems.Count == 1);
            using Stream stream = target.OpenDecryptedStream(target.VisibleItems[0]);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Require(copy.ToArray().AsSpan().SequenceEqual(original));
        }
        for (int round = 1; round <= 3; round++)
        {
            int current = round;
            await Check($"Audit: consecutive real + decoy password rotation {round}", async () =>
            {
                await vault.ChangeMasterPasswordAsync("Main-" + current + "#123");
                await vault.SetDecoyPasswordAsync("Decoy-" + current + "#123",
                    current == 1 ? "Decoy-A#123" : "Decoy-" + (current - 1) + "#123");
                vault.Lock();
                vault = new VaultService(vaultRoot);
                Require(vault.ApplyUnlock(vault.VerifyPassword("Decoy-" + current + "#123")) && vault.IsDecoyMode);
                VerifyContents(vault);
                vault.Lock();
                Require(vault.ApplyUnlock(vault.VerifyPassword("Main-" + current + "#123")) && !vault.IsDecoyMode);
                VerifyContents(vault);
                Require(!File.Exists(Path.Combine(vaultRoot, "rotate.dat")));
            });
        }
        await Check("Audit: main password cannot collide with decoy", async () =>
        {
            bool rejected = false;
            try { await vault.ChangeMasterPasswordAsync("Decoy-3#123"); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected); VerifyContents(vault);
        });
        await Check("Audit: decoy password cannot collide with main", async () =>
        {
            bool rejected = false;
            try { await vault.SetDecoyPasswordAsync("Main-3#123", "Decoy-3#123"); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected); VerifyContents(vault);
        });
        await Check("Audit: failed rotation preserves source and current password", async () =>
        {
            string file = Path.Combine(vaultRoot, "MediaVault", vault.VisibleItems[0].StoredFileName);
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool rejected = false;
                try { await vault.ChangeMasterPasswordAsync("Blocked#123"); }
                catch (IOException) { rejected = true; }
                Require(rejected);
            }
            VerifyContents(vault);
            Require(Directory.GetFiles(Path.Combine(vaultRoot, "MediaVault"), "*.svault").Length == 2);
            Require(!File.Exists(Path.Combine(vaultRoot, "rotate.dat")));
        });
        await Check("Audit: fallback catalog decrypts after repeated password changes", () =>
        {
            vault.Lock();
            File.WriteAllBytes(Path.Combine(vaultRoot, "system_catalog.dat"), [1, 2, 3]);
            vault = new VaultService(vaultRoot);
            Require(vault.ApplyUnlock(vault.VerifyPassword("Main-3#123")));
            VerifyContents(vault);
            return Task.CompletedTask;
        });
        await Check("Audit: collection callbacks run outside the vault state lock", () =>
        {
            bool inspected = false;
            vault.VisibleItems.CollectionChanged += (_, _) =>
            {
                Task<bool> read = Task.Run(() => vault.IsUnlocked);
                Require(read.Wait(TimeSpan.FromSeconds(2)));
                inspected = true;
            };
            vault.Lock(); Require(inspected);
            Require(vault.ApplyUnlock(vault.VerifyPassword("Main-3#123")));
            return Task.CompletedTask;
        });
        await Check("Audit: background lock revokes key immediately and marshals collection", () =>
        {
            var ui = new QueuedContext();
            vault.BindToUiContext(ui);
            int eventThread = 0;
            vault.VisibleItems.CollectionChanged += (_, _) => eventThread = Environment.CurrentManagedThreadId;
            var worker = new Thread(vault.Lock);
            worker.Start(); Require(worker.Join(TimeSpan.FromSeconds(5)));
            Require(!vault.IsUnlocked && vault.VisibleItems.Count == 1 && eventThread == 0);
            ui.Drain();
            Require(vault.VisibleItems.Count == 0 && eventThread == Environment.CurrentManagedThreadId);
            return Task.CompletedTask;
        });

        string sweepRoot = Path.Combine(root, "audit-sweep");
        var sweep = new VaultService(sweepRoot);
        await sweep.InitializeVaultAsync("Sweep#123");
        string media = Path.Combine(sweepRoot, "MediaVault");
        string orphan = Path.Combine(media, "recoverable.svault"), part = Path.Combine(media, "unfinished.part");
        File.WriteAllBytes(orphan, original); File.WriteAllText(part, "partial");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-1)); File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddDays(-1));
        await Check("Audit: sweep retains unreferenced encrypted media but removes stale partials", () =>
        {
            sweep.SweepOrphanedMedia(); Require(File.Exists(orphan) && !File.Exists(part));
            return Task.CompletedTask;
        });
        await Check("Audit: unreadable catalogs never sweep original media", () =>
        {
            foreach (string name in new[] { "system_catalog.dat", "system_catalog.dat.bak" })
                File.WriteAllBytes(Path.Combine(sweepRoot, name), [1, 2, 3]);
            sweep = new VaultService(sweepRoot); sweep.SweepOrphanedMedia();
            Require(sweep.IsCorrupted && File.ReadAllBytes(orphan).AsSpan().SequenceEqual(original));
            return Task.CompletedTask;
        });
        await Check("Audit: corrupt rotation journal retained, writes blocked, media readable", async () =>
        {
            string journal = Path.Combine(vaultRoot, "rotate.dat");
            File.WriteAllBytes(journal, [1, 2, 3]);
            var pending = new VaultService(vaultRoot);
            Require(pending.ApplyUnlock(pending.VerifyPassword("Main-3#123")));
            pending.SweepOrphanedMedia(); VerifyContents(pending);
            bool blocked = false;
            try { await pending.ImportFileAsync(source, false); }
            catch (InvalidOperationException) { blocked = true; }
            Require(blocked && File.Exists(journal));
            File.Delete(journal); pending.Lock();
        });
        await Check("Audit: legacy self-referencing rotation journal cannot delete source", () =>
        {
            var catalog = JsonSerializer.Deserialize<VaultConfig>(ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(vaultRoot, "system_catalog.dat")), null, DataProtectionScope.CurrentUser))!;
            string name = catalog.Items[0].StoredFileName;
            byte[] journal = JsonSerializer.SerializeToUtf8Bytes(new { IsDecoy = false, NewSaltHex = catalog.RealSaltHex,
                NewHashHex = catalog.RealHashHex, NewIterations = catalog.Iterations,
                Files = new[] { new { OldName = name, NewName = name } } });
            string path = Path.Combine(vaultRoot, "rotate.dat");
            File.WriteAllBytes(path, ProtectedData.Protect(journal, null, DataProtectionScope.CurrentUser));
            var pending = new VaultService(vaultRoot);
            Require(File.Exists(path) && pending.ApplyUnlock(pending.VerifyPassword("Main-3#123")));
            VerifyContents(pending); pending.Lock(); File.Delete(path);
            return Task.CompletedTask;
        });
        await Check("Audit: shred refuses hard links without changing either name", () =>
        {
            string link = Path.Combine(root, "audit-hardlink.bin");
            Require(CreateHardLinkW(link, source, IntPtr.Zero));
            try
            {
                Require(!CryptoService.ShredFile(link, 1) && File.Exists(link));
                Require(File.ReadAllBytes(source).AsSpan().SequenceEqual(original));
            }
            finally { File.Delete(link); }
            return Task.CompletedTask;
        });
        foreach (bool committed in new[] { false, true })
        {
            await Check("Audit: interrupted rotation recovers " + (committed ? "committed" : "uncommitted") + " generation", () =>
            {
                var catalog = JsonSerializer.Deserialize<VaultConfig>(ProtectedData.Unprotect(
                    File.ReadAllBytes(Path.Combine(vaultRoot, "system_catalog.dat")), null, DataProtectionScope.CurrentUser))!;
                string current = catalog.Items[0].StoredFileName, staged = Guid.NewGuid().ToString("N") + ".svault";
                string stagedPath = Path.Combine(vaultRoot, "MediaVault", staged);
                File.Copy(Path.Combine(vaultRoot, "MediaVault", current), stagedPath);
                byte[] journal = JsonSerializer.SerializeToUtf8Bytes(new { IsDecoy = false, NewSaltHex = catalog.RealSaltHex,
                    NewHashHex = committed ? catalog.RealHashHex : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    NewIterations = catalog.Iterations, Files = new[] { new { OldName = committed ? staged : current, NewName = committed ? current : staged } } });
                string path = Path.Combine(vaultRoot, "rotate.dat");
                File.WriteAllBytes(path, ProtectedData.Protect(journal, null, DataProtectionScope.CurrentUser));
                if (committed)
                {
                    using var held = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    _ = new VaultService(vaultRoot);
                    Require(File.Exists(path) && File.Exists(stagedPath));
                }
                var recovered = new VaultService(vaultRoot);
                Require(!File.Exists(path) && !File.Exists(stagedPath));
                Require(recovered.ApplyUnlock(recovered.VerifyPassword("Main-3#123")));
                VerifyContents(recovered); recovered.Lock();
                return Task.CompletedTask;
            });
        }
        foreach (bool committed in new[] { false, true })
        {
            await Check("Audit: fallback rotation " + (committed ? "recovers the confirmed generation" : "retains both ambiguous generations"), async () =>
            {
                string primary = Path.Combine(vaultRoot, "system_catalog.dat");
                byte[] saved = File.ReadAllBytes(primary);
                File.WriteAllBytes(primary + ".bak", saved);
                var catalog = JsonSerializer.Deserialize<VaultConfig>(ProtectedData.Unprotect(saved, null, DataProtectionScope.CurrentUser))!;
                string current = catalog.Items.Single().StoredFileName;
                string alternate = Guid.NewGuid().ToString("N") + ".svault";
                string alternatePath = Path.Combine(vaultRoot, "MediaVault", alternate);
                File.Copy(Path.Combine(vaultRoot, "MediaVault", current), alternatePath);
                var data = new { IsDecoy = false, NewSaltHex = catalog.RealSaltHex, NewIterations = catalog.Iterations,
                    NewHashHex = committed ? catalog.RealHashHex : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Files = new[] { new { OldName = committed ? alternate : current, NewName = committed ? current : alternate } } };
                string path = Path.Combine(vaultRoot, "rotate.dat");
                File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(data), null, DataProtectionScope.CurrentUser));
                File.WriteAllBytes(primary, [1, 2, 3]);
                try
                {
                    var recovered = new VaultService(vaultRoot);
                    Require(recovered.ApplyUnlock(recovered.VerifyPassword("Main-3#123")));
                    VerifyContents(recovered);
                    Require(File.Exists(path) == !committed && File.Exists(alternatePath) == !committed);
                    if (committed) await recovered.DeleteItemAsync(recovered.VisibleItems.Single());
                    else
                    {
                        string target = Path.Combine(root, "ambiguous-export.bin");
                        await recovered.ExportItemAsync(recovered.VisibleItems.Single(), target);
                        Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(original));
                    }
                }
                finally
                {
                    File.Delete(path);
                    File.Delete(alternatePath);
                    if (!committed) File.WriteAllBytes(primary, saved);
                }
            });
        }
        await Check("Audit: first decoy creation also rejects the main password", async () =>
        {
            var empty = new VaultService(Path.Combine(root, "audit-no-decoy"));
            await empty.InitializeVaultAsync("MainOnly#123");
            bool rejected = false;
            try { await empty.SetDecoyPasswordAsync("MainOnly#123"); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && !empty.HasDecoyVault); empty.Lock();
        });
        await Check("Audit: overwrite failure never falls back to normal deletion", () =>
        {
            using (var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                Require(!CryptoService.ShredFile(source, 1) && File.Exists(source));
            Require(File.ReadAllBytes(source).AsSpan().SequenceEqual(original));
            return Task.CompletedTask;
        });
        await Check("Audit: safe shred removes a singly linked regular file", () =>
        {
            string single = Path.Combine(root, "audit-shred.bin"); File.WriteAllBytes(single, original);
            Require(SafeShred.OverwriteAndDelete(single, 2) && !File.Exists(single));
            return Task.CompletedTask;
        });
        await Check("Audit: recursive cleanup skips nested junctions and junction roots", () =>
        {
            string scan = Path.Combine(root, "audit-clean"), outside = Path.Combine(root, "audit-outside");
            Directory.CreateDirectory(Path.Combine(scan, "level1", "level2")); Directory.CreateDirectory(outside);
            string ordinary = Path.Combine(scan, "hidden.bin"), sentinel = Path.Combine(outside, "keep.bin");
            File.WriteAllText(ordinary, "eligible"); File.SetAttributes(ordinary, FileAttributes.Hidden);
            File.WriteAllBytes(sentinel, original);
            string junction = Path.Combine(scan, "level1", "level2", "junction");
            var launch = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            launch.ArgumentList.Add("-NoProfile"); launch.ArgumentList.Add("-NonInteractive");
            launch.ArgumentList.Add("-EncodedCommand");
            launch.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "New-Item -ItemType Junction -Path $env:STEALTH_TEST_LINK -Target $env:STEALTH_TEST_TARGET -ErrorAction Stop | Out-Null")));
            launch.Environment["STEALTH_TEST_LINK"] = junction; launch.Environment["STEALTH_TEST_TARGET"] = outside;
            using var process = Process.Start(launch)!; process.WaitForExit(); Require(process.ExitCode == 0);
            try
            {
                var found = SafeFileSystem.EnumerateFiles(scan, true, out int failures);
                Require(found.SequenceEqual(new[] { ordinary }) && failures == 0);
                Require(SafeFileSystem.EnumerateFiles(junction, true, out _).Count == 0);
                Require(!CryptoService.ShredFile(Path.Combine(junction, "keep.bin"), 1));
                Require(File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(original));
            }
            finally { Directory.Delete(junction, false); }
            return Task.CompletedTask;
        });
        await Check("Audit: failed migration removes its unpublished stage only", () =>
        {
            string legacy = Path.Combine(root, "audit-legacy"), target = Path.Combine(root, "audit-migrated");
            Directory.CreateDirectory(Path.Combine(legacy, "MediaVault"));
            string blocked = Path.Combine(legacy, "MediaVault", "locked.svault"); File.WriteAllBytes(blocked, original);
            using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false;
                try { AppPaths.TryCopyLegacyVault(legacy, target); } catch (IOException) { failed = true; }
                Require(failed && !Directory.Exists(target) && Directory.GetDirectories(root, "audit-migrated.migration-*").Length == 0);
            }
            Require(File.ReadAllBytes(blocked).AsSpan().SequenceEqual(original));
            return Task.CompletedTask;
        });
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        internal void Drain() { while (_queue.TryDequeue(out var item)) item.Callback(item.State); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
}
