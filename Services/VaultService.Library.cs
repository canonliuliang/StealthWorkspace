using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    internal static bool IsValidDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') ||
            name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string device = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT"))
                && "123456789¹²³".Contains(device[3]));
    }

    internal async Task RenameItemAsync(VaultItem item, string name, CancellationToken token = default)
    {
        if (!IsValidDisplayName(name)) throw new ArgumentException("请输入有效的文件名。");
        long epoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync(token);
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            RequireUnlockEpoch(epoch);
            item = GetCurrentItemLocked(item);
            if (!name.EndsWith(item.FileExtension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请保留原格式的扩展名。");
            string oldName = item.OriginalFileName;
            item.OriginalFileName = name;
            try { SaveConfigOrThrow(); }
            catch { item.OriginalFileName = oldName; throw; }
        }
        OnUiContext(item.NotifyNameChanged);
    }

    private static void PublishUniqueExport(string temporary, string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        string stem = Path.GetFileNameWithoutExtension(destination);
        string extension = Path.GetExtension(destination);
        string candidate = destination;
        for (int index = 2; ; index++)
        {
            try { File.Move(temporary, candidate); return; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                string suffix = $" ({index})";
                int maxStem = 255 - extension.Length - suffix.Length;
                if (maxStem < 1) throw new IOException("文件扩展名过长，无法为同名文件生成副本。");
                candidate = Path.Combine(directory, stem[..Math.Min(stem.Length, maxStem)] + suffix + extension);
            }
        }
    }
}
