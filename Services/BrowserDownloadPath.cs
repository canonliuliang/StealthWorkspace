namespace StealthWorkspace.Services;

/// <summary>Reserves names before WebView2 creates a file, including simultaneous downloads.</summary>
internal sealed class BrowserDownloadPath : IDisposable
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);
    internal string FilePath { get; }
    private BrowserDownloadPath(string path) => FilePath = path;

    internal static BrowserDownloadPath Reserve(string directory, string suggestedName)
    {
        directory = Path.GetFullPath(directory);
        string name = SanitizeName(suggestedName);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        lock (Gate)
        {
            for (int suffix = 0; ; suffix++)
            {
                string path = Path.Combine(directory, suffix == 0 ? name : $"{stem} ({suffix}){extension}");
                if (Reserved.Contains(path) || File.Exists(path) || Directory.Exists(path)) continue;
                Reserved.Add(path);
                return new BrowserDownloadPath(path);
            }
        }
    }

    internal static string SanitizeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        foreach (char direction in "\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069") name = name.Replace(direction, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name)) name = "download.bin";
        string extension = Truncate(Path.GetExtension(name), 20);
        string stem = Truncate(Path.GetFileNameWithoutExtension(name), 120 - extension.Length);
        string device = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && device[..3] is ("COM" or "LPT") && device[3] is (>= '1' and <= '9') or '¹' or '²' or '³'))
            stem = "_" + stem;
        return stem + extension;
    }

    private static string Truncate(string part, int limit)
    {
        if (part.Length <= limit) return part;
        return part[..(char.IsHighSurrogate(part[limit - 1]) ? limit - 1 : limit)];
    }

    public void Dispose() { lock (Gate) Reserved.Remove(FilePath); }
}
