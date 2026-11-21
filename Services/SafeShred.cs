using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace StealthWorkspace.Services;

internal static class SafeShred
{
    internal readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow,
        uint SizeHigh, uint SizeLow, long LastWriteTime, long CreationTime);

    internal static FileIdentity CaptureIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return IdentityOf(info);
    }

    private static FileIdentity IdentityOf(FileInformation info) => new(info.VolumeSerialNumber,
        info.FileIndexHigh, info.FileIndexLow, info.FileSizeHigh, info.FileSizeLow,
        ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime,
        ((long)info.CreationTime.dwHighDateTime << 32) | (uint)info.CreationTime.dwLowDateTime);

    // Inspect metadata first (also works for read-only files), then reopen that same
    // object exclusively for writing. Never clear attributes through an unchecked path.
    internal static bool OverwriteAndDelete(string path, int passes, FileIdentity? expected = null, byte[]? expectedDigest = null,
        Func<bool>? authorize = null)
    {
        passes = Math.Clamp(passes, 1, 7);
        if (!SafeFileSystem.IsPlainPath(path)) return false;
        string fullPath = Path.GetFullPath(path);
        using var metadata = CreateFileW(fullPath, 0x80 | 0x100, 7, IntPtr.Zero,
            3, 0x00200000, IntPtr.Zero); // Pin the named object without following it.
        if (metadata.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(metadata, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (expected != null && expected.Value != IdentityOf(info)) return false;
        if (!IsSingleRegularFile(metadata, info))
        {
            AppLog.Warn("粉碎已跳过硬链接或重解析点，原件已保留");
            return false;
        }
        bool readOnly = (info.Attributes & FileAttributes.ReadOnly) != 0;
        bool completed = false;
        try
        {
            if (readOnly) SetAttributes(metadata, info.Attributes & ~FileAttributes.ReadOnly);
            uint access = 0x40000000 | 0x00010000; // WRITE_DATA and DELETE
            if (expectedDigest != null) access |= 0x80000000; // Read under the same exclusive handle for verification.
            using var handle = ReOpenFile(metadata, access, 0, 0x80000000);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandle(handle, out var writable)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!IsSingleRegularFile(handle, writable) || !SameFile(info, writable) || !SafeFileSystem.IsPlainPath(fullPath)) return false;
            if (expected != null && expected.Value != IdentityOf(writable)) return false;
            // Compare identity, not spelling: SUBST and 8.3 names can refer to this same file.
            // Zero requested access permits a metadata identity probe beside the exclusive writer.
            using (var probe = CreateFileW(fullPath, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (probe.IsInvalid || !GetFileInformationByHandle(probe, out var named)
                    || !IsSingleRegularFile(probe, named) || !SameFile(writable, named)) return false;
            }
            using var stream = new FileStream(handle, expectedDigest == null ? FileAccess.Write : FileAccess.ReadWrite, 64 * 1024, isAsync: false);
            if (expectedDigest != null)
            {
                byte[] actual = SHA256.HashData(stream);
                bool matches = CryptographicOperations.FixedTimeEquals(actual, expectedDigest);
                CryptographicOperations.ZeroMemory(actual);
                if (!matches) return false;
            }
            long fileLength = stream.Length;
            byte[] buffer = new byte[(int)Math.Min(Math.Max(fileLength, 1), 1024 * 1024)];
            try
            {
                // Hashing a large source may span a lock. Once an overwrite starts it must finish.
                if (authorize != null && !authorize()) return false;
                for (int pass = 0; pass < passes; pass++)
                {
                    if (pass == 0) Array.Clear(buffer);
                    else if (pass == 1) Array.Fill(buffer, (byte)0xFF);
                    else RandomNumberGenerator.Fill(buffer);
                    stream.Position = 0;
                    long remaining = fileLength;
                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(remaining, buffer.Length);
                        stream.Write(buffer, 0, count);
                        remaining -= count;
                    }
                    stream.Flush(true);
                }
                byte delete = 1; // FILE_DISPOSITION_INFO uses BOOLEAN (one byte).
                if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                completed = true;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        finally
        {
            if (readOnly && !completed)
            {
                try
                {
                    if (GetFileInformationByHandle(metadata, out var current))
                        SetAttributes(metadata, current.Attributes | FileAttributes.ReadOnly);
                }
                catch (Exception ex) { AppLog.Warn("未完成粉碎时恢复只读属性失败: " + ex.GetType().Name); }
            }
        }
    }

    private static bool IsSingleRegularFile(SafeFileHandle handle, FileInformation info) => info.NumberOfLinks == 1
        && (info.Attributes & FileAttributes.Directory) == 0
        && ((info.Attributes & FileAttributes.ReparsePoint) == 0 || SafeFileSystem.IsSafeInPlaceReparsePoint(handle));

    internal static bool IsHardLink(string path)
    {
        try
        {
            using var handle = CreateFileW(Path.GetFullPath(path), 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameFile(FileInformation left, FileInformation right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber && left.FileIndexHigh == right.FileIndexHigh && left.FileIndexLow == right.FileIndexLow;

    private static void SetAttributes(SafeFileHandle handle, FileAttributes attributes)
    {
        var basic = new FileBasicInfo { Attributes = attributes == 0 ? FileAttributes.Normal : attributes };
        if (!SetFileInformationByHandle(handle, 0, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal FileAttributes Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileBasicInfo information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
