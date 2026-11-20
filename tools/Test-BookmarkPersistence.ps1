param(
    [Parameter(Mandatory)][string]$BaselineRoot,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$baseline = (Resolve-Path -LiteralPath $BaselineRoot).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts\bookmark-edits-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
if ($output.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
    !$output.StartsWith((Join-Path $repo 'artifacts') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inside the repository keep generated source under artifacts.'
}
New-Item -ItemType Directory -Path $output | Out-Null
# Compile the production settings and identity sources with their real serializer,
# DPAPI and File.Replace. Only logging and the unused default path are substituted.
$hashes = foreach ($name in @('SettingsService', 'BrowserAddress')) {
    foreach ($kind in @('Baseline', 'Candidate')) {
        $sourceRoot = if ($kind -eq 'Baseline') { $baseline } else { $repo }
        $file = Join-Path $sourceRoot "Services\$name.cs"
        $code = [IO.File]::ReadAllText($file).Replace('namespace StealthWorkspace.Services;', "namespace $kind;")
        [IO.File]::WriteAllText((Join-Path $output "$kind.$name.cs"), $code)
        [PSCustomObject]@{ implementation=$kind; source=$name; sha256=(Get-FileHash -LiteralPath $file).Hash }
    }
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.15" /></ItemGroup>
</Project>
'@
$program = @'
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

internal static class AppPaths
{
    internal static string EnsureDataRoot() => throw new InvalidOperationException("Explicit synthetic root required");
    internal const bool HasDataRootOverride = true;
}
internal static class AppLog
{
    internal static void Warn(string message) { }
    internal static void Error(string message, Exception? ex) => Console.Error.WriteLine(message);
}
internal static class Program
{
    private static string Url(int i) => "https://fixture.invalid/Books/" + i + "?section=Read#Title";
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Bookmark verification failed"); }
    private static void Main(string[] args)
    {
        var reports = new List<object>();
        foreach (int count in new[] { 200, 10_000, 50_000 })
        {
            string path = Path.Combine(args[0], "synthetic-" + count);
            var old = new Baseline.SettingsService(Path.Combine(path, "before"), null);
            var candidate = new Candidate.SettingsService(Path.Combine(path, "after"), null);
            var created = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var beforeRows = Enumerable.Range(0, count).Select(i => new Baseline.BookmarkItem { Title = "Title " + i, Url = Url(i), CreatedAt = created }).ToImmutableList();
            var afterRows = Enumerable.Range(0, count).Select(i => new Candidate.BookmarkItem { Title = "Title " + i, Url = Url(i), CreatedAt = created }).ToImmutableList();
            old.Data.Bookmarks = beforeRows;
            candidate.Data.Bookmarks = afterRows;
            string last = Url(count - 1);
            int sequence = 0;
            var cold = Measure(
                () => { old.Data.Bookmarks = beforeRows.SetItem(0, new Baseline.BookmarkItem { Title = "Fresh", Url = Url(0), CreatedAt = created }); Require(old.IsBookmarked(last)); },
                () => { candidate.Data.Bookmarks = afterRows.SetItem(0, new Candidate.BookmarkItem { Title = "Fresh", Url = Url(0), CreatedAt = created }); Require(candidate.IsBookmarked(last)); });
            reports.Add(new { count, operation = "fresh collection, first star lookup", measurements = cold });
            old.SettingsChanged += () => Require(old.IsBookmarked(last));
            candidate.SettingsChanged += () => Require(candidate.IsBookmarked(last));
            Require(old.SaveSettings() && candidate.SaveSettings());
            var rename = Measure(
                () => Require(old.RenameBookmark(last, "Renamed " + (++sequence))),
                () => Require(candidate.RenameBookmark(last, "Renamed " + (++sequence))));
            reports.Add(new { count, operation = "rename last, DPAPI save and synchronous star refresh", measurements = rename });
            string extra = Url(count + 1);
            var edits = Measure(
                () => { Require(old.AddBookmark("New", extra)); Require(old.RemoveBookmark(extra)); },
                () => { Require(candidate.AddBookmark("New", extra)); Require(candidate.RemoveBookmark(extra)); });
            reports.Add(new { count, operation = "add and remove, two DPAPI writes and two star refreshes", measurements = edits });
            Require(new Baseline.SettingsService(Path.Combine(path, "before"), null).Data.Bookmarks.Count == count);
            Require(new Candidate.SettingsService(Path.Combine(path, "after"), null).CurrentBookmarks.Count == count);
        }
        string json = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            reports }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(args[0], "results.json"), json);
        foreach (var row in reports) Console.WriteLine(JsonSerializer.Serialize(row));
    }
    private record Sample(double ms, long bytes);
    private static object Measure(Action before, Action after)
    {
        for (int i = 0; i < 2; i++) { before(); after(); }
        var oldSamples = new List<Sample>();
        var newSamples = new List<Sample>();
        for (int i = 0; i < 9; i++)
            if (i % 2 == 0) { oldSamples.Add(Run(before)); newSamples.Add(Run(after)); }
            else { newSamples.Add(Run(after)); oldSamples.Add(Run(before)); }
        static object Summary(List<Sample> data) => new {
            medianMs = data.OrderBy(s => s.ms).ElementAt(4).ms, minMs = data.Min(s => s.ms),
            maxMs = data.Max(s => s.ms), meanAllocatedBytes = (long)data.Average(s => s.bytes), data };
        return new { baseline = Summary(oldSamples), candidate = Summary(newSamples) };
    }
    private static Sample Run(Action action)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        action();
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new(ms, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Persistence.csproj'), $project)
[IO.File]::WriteAllText((Join-Path $output 'Program.cs'), $program)
& dotnet run --project (Join-Path $output 'Persistence.csproj') -c Release -- $output
if ($LASTEXITCODE -ne 0) { throw "Bookmark persistence benchmark failed: $LASTEXITCODE" }
Write-Output "Results: $(Join-Path $output 'results.json')"
