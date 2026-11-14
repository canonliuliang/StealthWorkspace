using System;

namespace StealthWorkspace.Models;

public class VaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string MediaType { get; set; } = "Video"; // "Video", "Image", "Document"
    public long FileSizeBytes { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public string Tags { get; set; } = string.Empty;
    public bool IsDecoy { get; set; }

    public string DisplaySize
    {
        get
        {
            if (FileSizeBytes < 1024) return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024) return $"{FileSizeBytes / 1024.0:F1} KB";
            if (FileSizeBytes < 1024 * 1024 * 1024) return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public string FormattedDate => ImportedAt.ToString("yyyy-MM-dd HH:mm");

    public string DisplayIconGlyph => MediaType switch
    {
        "Video" => "\uE714", // Video icon
        "Image" => "\uEB9F", // Image icon
        _ => "\uE8A5"       // File icon
    };
}
