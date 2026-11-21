namespace StealthWorkspace.Services;

internal static class WorkspaceSessionSelfTest
{
    internal static async Task RunAsync(Action<bool, string, string> report)
    {
        if (!AppPaths.HasDataRootOverride) throw new InvalidOperationException("Isolated data root required.");
        var settings = SettingsService.Instance;
        settings.SetBookmarkWorkspace(false);
        string main = AppPaths.IsolatedDownloadsRoot;
        Directory.CreateDirectory(main);
        string mainFile = Path.Combine(main, "keep.txt");
        await File.WriteAllTextAsync(mainFile, "main");
        string theme = settings.Data.AppTheme;
        try
        {
            settings.SaveSettings();
            byte[] saved = await File.ReadAllBytesAsync(AppPaths.SettingsPath);
            settings.SetBookmarkWorkspace(true);
            settings.Data.AppTheme = theme == "Dark" ? "Light" : "Dark";
            bool temporary = settings.SaveSettings();
            string first = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(first);
            string file = Path.Combine(first, "private.txt");
            await File.WriteAllTextAsync(file, "old session");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = AppPaths.FinishBrowserSessionAsync(closed.Task, settings.ActiveBookmarkSession!.DownloadDirectoryName);
            settings.SetBookmarkWorkspace(null);
            byte[] after = await File.ReadAllBytesAsync(AppPaths.SettingsPath);
            report(temporary && settings.Data.AppTheme == theme && saved.SequenceEqual(after),
                "Leaving temporary workspace restores preferences and preserves encrypted main settings", "");
            settings.SetBookmarkWorkspace(true);
            string second = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(second);
            string nextFile = Path.Combine(second, "private.txt");
            await File.WriteAllTextAsync(nextFile, "new session");
            bool kept = File.Exists(file);
            closed.SetResult();
            await cleanup;
            report(first != main && first != second && kept && !Directory.Exists(first) && File.Exists(nextFile) &&
                await File.ReadAllTextAsync(mainFile) == "main",
                "Releasing old downloads cannot clear main files or downloads from a newer unlock", "");
            await AppPaths.FinishBrowserSessionAsync(Task.CompletedTask, settings.ActiveBookmarkSession!.DownloadDirectoryName);
        }
        finally { settings.SetBookmarkWorkspace(null); }
    }
}
