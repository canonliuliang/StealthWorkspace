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
foreach ($source in @('Services\BrowserAddress.cs', 'Services\BrowserAddressSuggestions.cs', 'Services\SettingsService.cs')) {
    if (!(Test-Path -LiteralPath (Join-Path $baseline $source) -PathType Leaf)) { throw "Missing baseline source: $source" }
}
$settingsText = [IO.File]::ReadAllText((Join-Path $baseline 'Services\SettingsService.cs'))
if ($settingsText -notmatch 'public\s+(ImmutableList|List)<BookmarkItem>\s+Bookmarks') {
    throw 'Unknown baseline bookmark collection. Verify its production type before measuring.'
}
$baselineStorage = $Matches[1]
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
$filterSource = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Services\BookmarkFilter.cs'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Compile Include="$addressSource" Link="Current.BrowserAddress.cs" />
    <Compile Include="$suggestionSource" Link="Current.BrowserAddressSuggestions.cs" />
    <Compile Include="$modelSource" Link="BrowserAddressSuggestion.cs" />
    <Compile Include="$filterSource" Link="BookmarkFilter.cs" />
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $output 'Performance.csproj'), $project, $utf8)
$program = @'
using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using StealthWorkspace.Models;
using StealthWorkspace.Services;

internal static class Program
{
    private static readonly Func<string, string> Search = q => "https://search.invalid/?q=" + Uri.EscapeDataString(q);
    private static IReadOnlyList<BrowserAddressSuggestion> Legacy(string q, IEnumerable<BookmarkItem> b, HistorySessionItem[] h) =>
        Baseline.Services.BrowserAddressSuggestions.Get(q, b, h, Search);
    private static IReadOnlyList<BrowserAddressSuggestion> Current(string q, IEnumerable<BookmarkItem> b, HistorySessionItem[] h) =>
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
        var typing = new List<object>();
        var flyout = new List<object>();
        foreach (int count in new[] { 200, 10_000, 50_000 })
        {
            var bookmarks = Enumerable.Range(0, count).Select(i => new BookmarkItem {
                Title = "Synthetic bookmark " + i, Url = "https://fixture.test/Collection/" + i + "?item=Image#Page"
            }).ToArray();
            var snapshot = bookmarks.ToImmutableList();
            IReadOnlyList<BookmarkItem> original = args[1] == "List" ? bookmarks.ToList() : snapshot;
            // This is the r15 flyout predicate and eager array copied on each TextChanged.
            BookmarkItem[] OriginalFilter(string query) => original.Where(item => query.Length == 0 ||
                item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var (scenario, keys) in new[] {
                ("open", new[] { "" }),
                ("bookmarks by title", new[] { "", "5", "50", "500", "5000" }),
                ("edit search", new[] { "", "rare", "rare2", "rare", "image", "IMAGE", "" })
            })
            {
                IReadOnlyList<BookmarkItem> Sequence(bool baseline)
                {
                    var filter = baseline ? null : new BookmarkFilter();
                    IReadOnlyList<BookmarkItem> rows = [];
                    foreach (var key in keys) rows = baseline ? OriginalFilter(key) : filter!.Filter(snapshot, key);
                    return rows;
                }
                var check = new BookmarkFilter();
                foreach (var key in keys)
                    if (!check.Filter(snapshot, key).SequenceEqual(OriginalFilter(key))) throw new InvalidOperationException("Flyout results changed");
                var result = Measure(() => Sequence(true), () => Sequence(false));
                flyout.Add(new { bookmarks = count, scenario, result });
                Console.WriteLine($"Flyout {scenario} / {count} bookmarks: {result}");
            }
            foreach (string phrase in new[] { "technical report 2026", "bookmark " + (count - 1) })
            {
                string[] prefixes = Enumerable.Range(1, phrase.Length).Select(n => phrase[..n]).ToArray();
                IReadOnlyList<BrowserAddressSuggestion> Sequence(bool baseline)
                {
                    var edit = !baseline && count >= 256 ? new BrowserAddressSuggestions.EditSession() : null;
                    IReadOnlyList<BrowserAddressSuggestion> rows = [];
                    foreach (var prefix in prefixes)
                        rows = baseline ? Baseline.Services.BrowserAddressSuggestions.Get(prefix, original, history, Search) :
                            BrowserAddressSuggestions.Get(prefix, snapshot, history, Search, edit);
                    return rows;
                }

                var edit = new BrowserAddressSuggestions.EditSession();
                foreach (var prefix in prefixes)
                {
                    var expected = Baseline.Services.BrowserAddressSuggestions.Get(prefix, original, history, Search);
                    var actual = BrowserAddressSuggestions.Get(prefix, snapshot, history, Search, edit);
                    if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                        throw new InvalidOperationException("Typing changed results for synthetic input: " + prefix);
                    checkedQueries++;
                }
                var result = Measure(() => Sequence(true), () => Sequence(false));
                typing.Add(new { bookmarks = count, phrase, keys = prefixes.Length, result.samples, result.baselineMedianMs,
                    result.currentMedianMs, result.baselineP95Ms, result.currentP95Ms, result.baselineAllocatedBytes, result.currentAllocatedBytes });
                Console.WriteLine($"Typing {prefixes.Length} keys / {count} bookmarks / {phrase}: {result}");
            }
            foreach (string query in new[] { "no-such-local-entry", "bookmark " + (count - 1) })
            {
                Equivalent(query, bookmarks);
                Func<IReadOnlyList<BrowserAddressSuggestion>> before = () => Legacy(query, original, history);
                Func<IReadOnlyList<BrowserAddressSuggestion>> after = () => Current(query, snapshot, history);
                var result = Measure(before, after);
                measurements.Add(new { bookmarks = count, query, result.samples, result.baselineMedianMs,
                    result.currentMedianMs, result.baselineP95Ms, result.currentP95Ms, result.baselineAllocatedBytes, result.currentAllocatedBytes });
                Console.WriteLine($"{count,6} bookmarks / {query}: {result}");
            }
        }
        var report = new {
            utc = DateTimeOffset.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            scope = "Synthetic CPU/allocation microbenchmark of local suggestions and bookmark filtering, excluding XAML and WebView.",
            baselineBookmarkCollection = args[1], checkedQueries, checkedAddressPairs = checkedPairs, measurements, typing, flyout
        };
        File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checkedQueries} full suggestion equivalence cases; {checkedPairs} address identity pairs.");
    }

    private sealed record Measurement(int samples, double baselineMedianMs, double currentMedianMs,
        double baselineP95Ms, double currentP95Ms, long baselineAllocatedBytes, long currentAllocatedBytes)
    {
        public override string ToString() =>
            $"median {baselineMedianMs:F4} -> {currentMedianMs:F4} ms, p95 {baselineP95Ms:F4} -> {currentP95Ms:F4} ms, allocated {baselineAllocatedBytes:N0} -> {currentAllocatedBytes:N0} bytes";
    }

    private static Measurement Measure<T>(Func<T> before, Func<T> after)
    {
        for (int warm = 0; warm < 12; warm++) { before(); after(); }
        var oldTimes = new List<double>(31);
        var newTimes = new List<double>(31);
        long oldBytes = 0, newBytes = 0;
        for (int sample = 0; sample < 31; sample++)
        {
            if (sample % 2 == 0) { Run(before, oldTimes, ref oldBytes); Run(after, newTimes, ref newBytes); }
            else { Run(after, newTimes, ref newBytes); Run(before, oldTimes, ref oldBytes); }
        }
        oldTimes.Sort();
        newTimes.Sort();
        return new(31, oldTimes[15], newTimes[15], oldTimes[29], newTimes[29], oldBytes / 31, newBytes / 31);
    }

    private static void Run<T>(Func<T> action, List<double> times, ref long bytes)
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
& dotnet run --project (Join-Path $output 'Performance.csproj') -c Release -- $result $baselineStorage |
    Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Browser performance harness failed: $LASTEXITCODE" }
Write-Output "Results: $result"
