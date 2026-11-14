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
        name = name.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name)) name = "download.bin";
        string extension = Path.GetExtension(name);
        if (extension.Length > 20) extension = extension[..20];
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length > 120 - extension.Length) stem = stem[..(120 - extension.Length)];
        string device = stem.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9'))
            stem = "_" + stem;
        return stem + extension;
    }

    public void Dispose() { lock (Gate) Reserved.Remove(FilePath); }
}
