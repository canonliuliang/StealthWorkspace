using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace StealthWorkspace.Services;

/// <summary>
/// Minimal, privacy-respecting diagnostic log.
///
/// Every line is DPAPI protected (current user) below the app data root.
/// Callers must supply static operation names, not private paths or URLs.
/// Exception messages and source-file paths are omitted. Logging must never throw.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StealthWorkspace.Log.v1");
    private const long MaxLogBytes = 512 * 1024;
    private const int MaxQueuedRecords = 512;
    private sealed record Entry(DateTime Time, string Path, string Level, string Message, TaskCompletionSource<bool>? Barrier = null);
    private static readonly Channel<Entry> Queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private static readonly Task Writer = Task.Run(WriteLoopAsync);
    private static int _queuedRecords, _droppedRecords;
    private static bool _initialized;

    public static string LogFilePath => AppPaths.LogPath;

    public static void Initialize()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            try
            {
                AppPaths.EnsureDataRoot();
                _initialized = true;
            }
            catch
            {
                // logging disabled
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Warn(string context, Exception exception) => WriteFailure("WARN", context, exception);

    public static void Error(string context, Exception? exception = null)
    {
        WriteFailure("ERROR", context, exception);
    }

    private static void WriteFailure(string level, string context, Exception? exception)
    {
        try { Write(level, exception == null ? context : DescribeFailure(context, exception)); }
        catch { Write(level, context + " | exception details unavailable"); }
    }

    private static string DescribeFailure(string context, Exception exception)
    {
        // StackTrace strings can embed build-machine paths via portable PDBs.
        // Preserve method names for diagnosis without file/line information.
        var frames = new System.Diagnostics.StackTrace(exception, false).GetFrames();
        string methods = frames == null ? "" : string.Join(" <- ", frames.Take(12).Select(frame =>
        {
            var method = frame.GetMethod();
            return method == null ? "?" : $"{method.DeclaringType?.FullName}.{method.Name}";
        }));
        return $"{context} | {exception.GetType().Name} | 0x{exception.HResult:X8} | {methods}";
    }

    /// <summary>Decrypts and returns the whole log content. Only used by diagnostics tooling.</summary>
    public static string ReadAll()
    {
        try
        {
            FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            string[] lines;
            lock (Gate)
            {
                if (!File.Exists(AppPaths.LogPath)) return string.Empty;
                lines = File.ReadAllLines(AppPaths.LogPath);
            }
            var builder = new StringBuilder();
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    byte[] cipher = Convert.FromBase64String(line);
                    byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                    builder.AppendLine(Encoding.UTF8.GetString(plain));
                }
                catch
                {
                    // ignore broken line
                }
            }
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            // No encryption, file writes, retries or sleeps on the UI thread.
            // Bound queued plaintext diagnostics even if the disk stalls.
            if (System.Threading.Interlocked.Increment(ref _queuedRecords) > MaxQueuedRecords)
            {
                System.Threading.Interlocked.Decrement(ref _queuedRecords);
                System.Threading.Interlocked.Increment(ref _droppedRecords);
                return;
            }
            var entry = new Entry(DateTime.Now, AppPaths.LogPath, level, Sanitize(message));
            if (!Queue.Writer.TryWrite(entry)) System.Threading.Interlocked.Decrement(ref _queuedRecords);
        }
        catch { System.Threading.Interlocked.Decrement(ref _queuedRecords); }
    }

    internal static async Task<bool> FlushAsync(TimeSpan timeout)
    {
        try
        {
            // Barriers are never dropped by the bounded record policy. They
            // acknowledge that every earlier accepted record has been attempted.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Queue.Writer.TryWrite(new Entry(default, "", "", "", completion))) return false;
            await completion.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static async Task WriteLoopAsync()
    {
        var batch = new List<Entry>(32);
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (batch.Count < 32 && Queue.Reader.TryRead(out var entry))
            {
                if (entry.Barrier != null)
                {
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                    entry.Barrier.TrySetResult(true);
                }
                else
                {
                    System.Threading.Interlocked.Decrement(ref _queuedRecords);
                    batch.Add(entry);
                }
            }
            await WriteBatchAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private static async Task WriteBatchAsync(List<Entry> batch)
    {
        if (batch.Count == 0) return;
        foreach (var group in batch.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var output = new StringBuilder();
                void EncryptLine(DateTime time, string level, string message)
                {
                    string line = $"{time:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    byte[] plain = Encoding.UTF8.GetBytes(line);
                    try { output.AppendLine(Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser))); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                int dropped = System.Threading.Interlocked.Exchange(ref _droppedRecords, 0);
                if (dropped != 0) EncryptLine(DateTime.Now, "WARN", $"Diagnostic queue full; omitted {dropped} records");
                foreach (var entry in group) EncryptLine(entry.Time, entry.Level, entry.Message);
                for (int retry = 0; retry < 2; retry++)
                {
                    try
                    {
                        lock (Gate)
                        {
                            string? directory = Path.GetDirectoryName(group.Key);
                            if (directory != null && !Directory.Exists(directory)) { Directory.CreateDirectory(directory); AppPaths.Hide(directory); }
                            RotateIfNeeded(group.Key);
                            File.AppendAllText(group.Key, output.ToString(), Encoding.ASCII);
                        }
                        break;
                    }
                    catch (IOException) when (retry == 0) { await Task.Delay(50).ConfigureAwait(false); }
                }
            }
            catch { /* Diagnostic failure must never interrupt the app. */ }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length > 8192) value = value[..8192] + "…";
        // Newlines would break the one-line-per-entry format.
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
