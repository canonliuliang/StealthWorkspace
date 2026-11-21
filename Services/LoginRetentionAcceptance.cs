namespace StealthWorkspace.Services;

// Port and a random run ID are provided only by tools/Test-LoginRetention.mjs.
// The test cannot navigate or send state to a user-supplied host.
internal static class LoginRetentionAcceptance
{
    internal static bool Requested => SmokeTest.UiSmoke && Argument("--login-retention-test") != null;
    internal static string Phase => Argument("--login-retention-test") ?? "";
    internal static bool ExerciseClearAll => Environment.GetCommandLineArgs().Contains("--login-check-all");
    internal static string RunId => Guid.TryParseExact(Argument("--login-test-id"), "N", out var id)
        ? id.ToString("N") : throw new ArgumentException("Missing a synthetic login test identifier.");

    private static string? Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static Uri Page(bool secondSite, string action)
    {
        if (!Requested || !AppPaths.HasDataRootOverride ||
            !int.TryParse(Argument("--login-test-port"), out int port) || port is < 1024 or > 65535 ||
            action is not ("login" or "read" or "logout")) throw new InvalidOperationException("Invalid isolated login fixture.");
        return new UriBuilder("http", secondSite ? "localhost" : "127.0.0.1", port,
            $"/{RunId}/{action}/{Phase}/{Guid.NewGuid():N}").Uri;
    }
}
