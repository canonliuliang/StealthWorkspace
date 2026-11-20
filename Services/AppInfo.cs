using System;
using System.IO;
using System.Reflection;

namespace StealthWorkspace.Services;

/// <summary>
/// Build identity shown in the UI so it is always obvious whether a running window is
/// the latest fixed build (previously users could accidentally keep launching an old
/// publish folder and see already-fixed bugs).
/// </summary>
public static class AppInfo
{
    /// <summary>Bump this for every delivered fix batch.</summary>
    public const string DisplayVersion = "2026.11.21-r16.1-preview";

    public static string BuildStamp
    {
        get
        {
            try
            {
                string? location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    return $"{DisplayVersion} · {File.GetLastWriteTime(location):yyyy-MM-dd HH:mm}";
                }
            }
            catch
            {
                // fall through
            }
            return DisplayVersion;
        }
    }
}
