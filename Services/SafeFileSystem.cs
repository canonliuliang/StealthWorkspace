using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StealthWorkspace.Services;

/// <summary>Fail-closed traversal for cleanup: never follow a directory or file reparse point.</summary>
internal static class SafeFileSystem
{
    /// <summary>Resolve existing ancestors through handles, including short names and DOS drive aliases.</summary>
    internal static string ResolvePath(string path)
    {
        string current = Path.GetFullPath(path);
        var missing = new Stack<string>();
        while (true)
        {
            try { _ = File.GetAttributes(current); break; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                string trimmed = Path.TrimEndingDirectorySeparator(current);
                string? parent = Path.GetDirectoryName(trimmed);
                if (parent == null) throw new IOException("无法确认文件的实际位置。", ex);
                missing.Push(Path.GetFileName(trimmed));
                current = parent;
            }
        }
        // Follow directory/file links for boundary comparison only. NT volume names
        // also normalize SUBST drives without requiring a Mount Manager drive letter.
        using var handle = CreateFileW(current, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法确认文件的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0x2);
        if (length >= buffer.Capacity)
        {
            if (length > 32768) throw new PathTooLongException();
            buffer.Capacity = checked((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0x2);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("无法确认文件的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        string resolved = buffer.ToString().TrimEnd(Path.DirectorySeparatorChar);
        while (missing.TryPop(out string? name)) resolved += Path.DirectorySeparatorChar + name;
        return resolved;
    }

    internal static bool IsPlainPath(string path)
    {
        try
        {
            string? current = Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            }
            return true;
        }
        catch { return false; }
    }

    internal static List<string> EnumerateFiles(string root, bool recursive, out int failures,
        Func<string, bool>? skipDirectory = null, CancellationToken cancellationToken = default)
    {
        failures = 0;
        var files = new List<string>();
        if (!Directory.Exists(root)) return files;
        if (!IsPlainPath(root)) { failures++; return files; }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPlainPath(directory)) { failures++; continue; }
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (recursive && skipDirectory?.Invoke(entry) != true) pending.Push(entry);
                        }
                        else files.Add(entry);
                    }
                    catch (IOException) { failures++; }
                    catch (UnauthorizedAccessException) { failures++; }
                }
            }
            catch (IOException) { failures++; }
            catch (UnauthorizedAccessException) { failures++; }
        }
        return files;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
