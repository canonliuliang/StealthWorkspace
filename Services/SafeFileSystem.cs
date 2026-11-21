using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StealthWorkspace.Services;

/// <summary>Allow known in-place file providers and reject redirections or unrecognized tags.</summary>
internal static class SafeFileSystem
{
    /// <summary>Resolve existing ancestors through handles, including short names and DOS drive aliases.</summary>
    internal static string ResolvePath(string path)
    {
        string current = WorkspaceFileName.FullPath(path);
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
        using var handle = CreateFileW(WorkspaceFileName.ForWin32(current), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
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

    internal static void EnsureWorkspaceDirectory(string directory)
    {
        string full = WorkspaceFileName.FullPath(directory);
        string ancestor = full;
        while (true)
        {
            try { _ = File.GetAttributes(ancestor); break; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("无法确定工作区目录。", ex); }
        }
        if (!IsPlainWorkspacePath(ancestor)) throw new IOException("工作区目录包含重定向或无法访问。");
        Directory.CreateDirectory(full);
        if (!IsPlainWorkspacePath(full)) throw new IOException("工作区目录已改变或无法访问。");
    }

    internal static bool IsPlainWorkspacePath(string path) => IsPlainPath(path, workspace: true);

    internal static bool IsPlainPath(string path, bool workspace = false)
    {
        try
        {
            string? current = workspace ? WorkspaceFileName.FullPath(path) : Path.GetFullPath(path);
            while (current != null)
            {
                if (HasLinkReparsePoint(current, workspace)) return false;
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            }
            return true;
        }
        catch { return false; }
    }

    internal static bool HasLinkReparsePoint(string path, bool workspace = false)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        using var handle = CreateFileW(workspace ? WorkspaceFileName.ForWin32(path) : path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        return !IsSafeInPlaceReparsePoint(handle);
    }

    // Removing the read-only flag changes every hard link to the same file. On Windows 11
    // unlink just this name and leave the attributes of all surviving names untouched.
    internal static void DeleteReadOnlyFile(string path, bool workspace = false)
    {
        using var handle = CreateFileW(workspace ? WorkspaceFileName.ForWin32(path) : path, 0x00010000 /* DELETE */, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!IsPlainPath(path, workspace)) throw new IOException("文件位置已改变，清理已跳过。");
        uint flags = 0x01 | 0x10; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE
        if (!SetFileInformationByHandle(handle, 21 /* FileDispositionInfoEx */, ref flags, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool IsSafeInPlaceReparseTag(uint tag) =>
        // Surrogate bit (0x20000000) marks symlinks, junctions and mount points that redirect traversal.
        // Non-surrogate Cloud Files (OneDrive Known Folder Move: 0x9000*01A), WOF (0x80000017) and Dedup (0x80000013) are in-place files/folders.
        (tag & 0x20000000u) == 0 && ((tag & 0xFFFF0FFFu) == 0x9000001Au || tag is 0x80000017u or 0x80000013u);

    internal static bool IsSafeInPlaceReparsePoint(SafeFileHandle handle) =>
        !handle.IsInvalid && GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info,
            (uint)Marshal.SizeOf<FileAttributeTagInfo>()) && IsSafeInPlaceReparseTag(info.ReparseTag);

    internal static List<string> EnumerateFiles(string root, bool recursive, out int failures,
        Func<string, bool>? skipDirectory = null, CancellationToken cancellationToken = default, bool workspace = false)
    {
        failures = 0;
        var files = new List<string>();
        if (!Directory.Exists(root)) return files;
        if (!IsPlainPath(root, workspace)) { failures++; return files; }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPlainPath(directory, workspace)) { failures++; continue; }
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && HasLinkReparsePoint(entry, workspace)) continue;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int fileInformationClass,
        out FileAttributeTagInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass,
        ref uint flags, uint bufferSize);
}
