param(
    [Parameter(Mandatory)][string]$BaselineRoot,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$baseline = (Resolve-Path -LiteralPath $BaselineRoot).Path
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts\browser-performance-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; stale measurements must not be overwritten.' }
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$artifactPrefix = (Join-Path $repoRoot 'artifacts').TrimEnd('\') + '\'
if ($output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    !$output.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inside the repository, generated benchmark source must stay under artifacts so it cannot enter the app build.'
}
foreach ($source in @('Services\BrowserAddress.cs', 'Services\BrowserAddressSuggestions.cs')) {
    if (!(Test-Path -LiteralPath (Join-Path $baseline $source) -PathType Leaf)) { throw "Missing baseline source: $source" }
}
New-Item -ItemType Directory -Path $output | Out-Null
$sourceHashes = foreach ($source in @('Services\BrowserAddress.cs', 'Services\BrowserAddressSuggestions.cs')) {
    [PSCustomObject]@{
        Source = $source
        BaselineSha256 = (Get-FileHash -LiteralPath (Join-Path $baseline $source) -Algorithm SHA256).Hash
        CurrentSha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot $source) -Algorithm SHA256).Hash
    }
}
$sourceHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-hashes.json') -Encoding utf8

# Compile the original and current implementations in the same process. No app,
# WebView, user profile, DPAPI settings, network service or package is involved.
# DTO shapes below match the properties used by these algorithms; the production
# BrowserAddressSuggestion and both current algorithms are compiled directly.
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($name in @('BrowserAddress', 'BrowserAddressSuggestions')) {
    $source = [IO.File]::ReadAllText((Join-Path $baseline "Services\$name.cs"))
    $source = "using StealthWorkspace.Services;`n" + $source.Replace('namespace StealthWorkspace.Services;', 'namespace Baseline.Services;')
    [IO.File]::WriteAllText((Join-Path $output "Baseline.$name.cs"), $source, $utf8)
}
$addressSource = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Services\BrowserAddress.cs'))
$suggestionSource = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Services\BrowserAddressSuggestions.cs'))
$modelSource = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Models\BrowserAddressSuggestion.cs'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Compile Include="$addressSource" Link="Current.BrowserAddress.cs" />
    <Compile Include="$suggestionSource" Link="Current.BrowserAddressSuggestions.cs" />
    <Compile Include="$modelSource" Link="BrowserAddressSuggestion.cs" />
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $output 'Performance.csproj'), $project, $utf8)
$program = @'
using System.Diagnostics;
using System.Text.Json;
using StealthWorkspace.Models;
using StealthWorkspace.Services;

internal static class Program
{
    private static readonly Func<string, string> Search = q => "https://search.invalid/?q=" + Uri.EscapeDataString(q);
    private static IReadOnlyList<BrowserAddressSuggestion> Legacy(string q, BookmarkItem[] b, HistorySessionItem[] h) =>
        Baseline.Services.BrowserAddressSuggestions.Get(q, b, h, Search);
    private static IReadOnlyList<BrowserAddressSuggestion> Current(string q, BookmarkItem[] b, HistorySessionItem[] h) =>
        BrowserAddressSuggestions.Get(q, b, h, Search);

    private static void Main(string[] args)
    {
        var history = new[] {
            new HistorySessionItem { Title = "最近查看", Url = "https://example.test/gallery" },
            new HistorySessionItem { Title = "History", Url = "https://example.test/Comic" }
        };
        var edges = new[] {
            new BookmarkItem { Title = "图片索引", Url = "https://example.test/gallery" },
            new BookmarkItem { Title = "unsafe", Url = "javascript:alert(1)" },
            new BookmarkItem { Title = "Upper", Url = "HTTPS://EXAMPLE.TEST:443/Comic?id=ABC" },
            new BookmarkItem { Title = "Lower", Url = "https://example.test/comic?id=abc" },
            new BookmarkItem { Title = "Malformed", Url = "https://" },
            new BookmarkItem { Title = "", Url = "https://example.test/empty-name" }
        };
        int checkedQueries = 0;
        void Equivalent(string query, BookmarkItem[] bookmarks)
        {
            string before = JsonSerializer.Serialize(Legacy(query, bookmarks, history));
            string after = JsonSerializer.Serialize(Current(query, bookmarks, history));
            if (before != after) throw new InvalidOperationException("Suggestions changed for synthetic query: " + query);
            checkedQueries++;
        }
        foreach (string query in new[] { "", "9", "最近", "gallery", "Comic", "unsafe", "https://example.test", "no-match", "  图片  ", "javascript:alert(1)" })
            Equivalent(query, edges);
        string[] addresses = {
            "HTTPS://EXAMPLE.TEST:443/Comic?id=ABC", "https://example.test/Comic?id=ABC",
            "https://example.test/Comic?id=abc", "https://example.test/comic?id=ABC",
            "https://bücher.test/path#A", "https://xn--bcher-kva.test/path#A", "https://bücher.test/path#a",
            "https://user:pass@example.test/path", "https://other:pass@example.test/path", "not a URI"
        };
        int checkedPairs = 0;
        foreach (string a in addresses)
        foreach (string b in addresses)
        {
            bool expected = Baseline.Services.BrowserAddress.SamePage(a, b);
            if (BrowserAddress.SamePage(a, b) != expected ||
                (Uri.TryCreate(a, UriKind.Absolute, out var parsed) && BrowserAddress.SamePage(parsed, b) != expected))
                throw new InvalidOperationException("Bookmark address identity changed");
            checkedPairs++;
        }

        var measurements = new List<object>();
        foreach (int count in new[] { 200, 10_000, 50_000 })
        {
            var bookmarks = Enumerable.Range(0, count).Select(i => new BookmarkItem {
                Title = "Synthetic bookmark " + i, Url = "https://fixture.test/Collection/" + i + "?item=Image#Page"
            }).ToArray();
            foreach (string query in new[] { "no-such-local-entry", "bookmark " + (count - 1) })
            {
                Equivalent(query, bookmarks);
                Func<IReadOnlyList<BrowserAddressSuggestion>> before = () => Legacy(query, bookmarks, history);
                Func<IReadOnlyList<BrowserAddressSuggestion>> after = () => Current(query, bookmarks, history);
                for (int warm = 0; warm < 12; warm++) { before(); after(); }
                var oldTimes = new List<double>(); var newTimes = new List<double>();
                long oldBytes = 0, newBytes = 0;
                for (int sample = 0; sample < 31; sample++)
                {
                    // Alternate order so one implementation does not always get
                    // the first sample after a collection/scheduler interruption.
                    if (sample % 2 == 0) { Run(before, oldTimes, ref oldBytes); Run(after, newTimes, ref newBytes); }
                    else { Run(after, newTimes, ref newBytes); Run(before, oldTimes, ref oldBytes); }
                }
                oldTimes.Sort(); newTimes.Sort();
                double oldMedian = oldTimes[15], newMedian = newTimes[15];
                measurements.Add(new {
                    bookmarks = count, query, samples = 31,
                    baselineMedianMs = oldMedian, currentMedianMs = newMedian,
                    baselineP95Ms = oldTimes[29], currentP95Ms = newTimes[29],
                    baselineAllocatedBytes = oldBytes / 31, currentAllocatedBytes = newBytes / 31
                });
                Console.WriteLine($"{count,6} bookmarks / {query}: median {oldMedian:F3} -> {newMedian:F3} ms; p95 {oldTimes[29]:F3} -> {newTimes[29]:F3} ms; allocated {oldBytes / 31:N0} -> {newBytes / 31:N0} bytes");
            }
        }
        var report = new {
            utc = DateTimeOffset.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            scope = "Synthetic CPU/allocation microbenchmark of local suggestions. Not browser frame rate or whole-app latency.",
            checkedQueries, checkedAddressPairs = checkedPairs, measurements
        };
        File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checkedQueries} full suggestion equivalence cases; {checkedPairs} address identity pairs.");
    }

    private static void Run(Func<IReadOnlyList<BrowserAddressSuggestion>> action, List<double> times, ref long bytes)
    {
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        var result = action();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bytes += GC.GetAllocatedBytesForCurrentThread() - allocation;
        times.Add(elapsed);
        GC.KeepAlive(result);
    }
}

namespace StealthWorkspace.Services
{
    public class BookmarkItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
namespace StealthWorkspace.Models
{
    public class HistorySessionItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Program.cs'), $program, $utf8)
$result = Join-Path $output 'results.json'
& dotnet run --project (Join-Path $output 'Performance.csproj') -c Release -- $result |
    Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Browser performance harness failed: $LASTEXITCODE" }
Write-Output "Results: $result"
