namespace StealthWorkspace.Services;

/// <summary>Pass normalized filesystem paths to the native handles used by the vault and browser.</summary>
internal static class WorkspaceFileName
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    internal static string FullPath(string path)
    {
        string ordinary = path.Replace('/', '\\');
        foreach (Range range in ordinary.AsSpan().Split('\\'))
        {
            ReadOnlySpan<char> segment = ordinary.AsSpan()[range];
            if (!segment.IsEmpty && segment[^1] is ('.' or ' ') && !segment.SequenceEqual(".") && !segment.SequenceEqual(".."))
                throw new IOException("工作区文件名称末尾包含点或空格。");
        }
        if (ordinary.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            ordinary.StartsWith(@"\\.\", StringComparison.Ordinal) || ordinary.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new IOException("设备路径不能用作工作区文件。");
        string full = Path.GetFullPath(ordinary);
        bool unc = full.StartsWith(@"\\", StringComparison.Ordinal);
        string[] parts = full[(unc ? 2 : 3)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (unc && (parts.Length < 2 || parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) ||
            parts[1].Equals("mailslot", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("该网络路径不能用作工作区文件。");
        foreach (string part in parts)
        {
            // Extended syntax would otherwise change the DOS interpretation of these names.
            if (part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(InvalidNameChars) >= 0 || IsDeviceName(part))
                throw new IOException("工作区文件名称无效。");
        }
        return full;
    }

    private static bool IsDeviceName(string part)
    {
        int dot = part.IndexOf('.');
        ReadOnlySpan<char> stem = (dot < 0 ? part.AsSpan() : part.AsSpan(0, dot)).TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && "0123456789¹²³".Contains(stem[3]) &&
                (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    internal static string ForWin32(string path)
    {
        string full = FullPath(path);
        // .NET handles normal paths itself. Only legacy native calls need this spelling.
        // Older directory operations reserve twelve characters of MAX_PATH.
        if (full.Length < 248) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
