using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StealthWorkspace.Models;

public class VaultItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string MediaType { get; set; } = "Video"; // "Video", "Image", "Document"
    public long FileSizeBytes { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public string Tags { get; set; } = string.Empty;
    public bool IsDecoy { get; set; }

    [JsonIgnore]
    public string DisplayDuration { get; private set; } = "";

    internal void SetVideoDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        string text = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
        if (text == DisplayDuration) return;
        DisplayDuration = text;
        PropertyChanged?.Invoke(this, new(nameof(DisplayDuration)));
    }

    internal void NotifyNameChanged() => PropertyChanged?.Invoke(this, new(nameof(OriginalFileName)));

    [JsonIgnore]
    public string MimeType => (FileExtension ?? "").ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".ts" => "video/mp2t",
        ".flv" => "video/x-flv",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        _ => "video/mp4"
    };

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
