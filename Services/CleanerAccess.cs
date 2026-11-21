using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StealthWorkspace.Services;

internal static class CleanerAccess
{
    // The shell can start a locally installed executable at low integrity even for
    // a standard user. Determine the running process's level, not group membership.
    private static readonly Lazy<bool> LowIntegrity = new(ReadLowIntegrity);
    internal static bool IsRestricted => LowIntegrity.Value;

    internal static bool RequiresStandardDesktop(CleanerCategory category) => category is
        CleanerCategory.ThumbnailCache or CleanerCategory.RecentAndJumpList or
        CleanerCategory.ExplorerSearchAndRunHistory or CleanerCategory.MediaAndRemoteHistory or
        CleanerCategory.CrashDumpsAndWer or CleanerCategory.RecycleBin;

    private static bool ReadLowIntegrity()
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        using (token)
        {
            const int integrityLevel = 25;
            _ = GetTokenInformation(token, integrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0 || length > 4096) return false;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, integrityLevel, buffer, length, out _)) return false;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count > 0 && Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))) < 0x2000;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    // Check delete access to a temporary file without a delete disposition or any writes.
    internal static int GetTempFileDeleteError(string path)
    {
        using var handle = CreateFileW(path, 0x10000 /* DELETE */, 7, IntPtr.Zero, 3,
            0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, int access, int share, IntPtr security, int creation, int flags, IntPtr template);
}
