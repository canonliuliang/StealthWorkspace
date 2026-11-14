param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(3,30)][int]$Iterations = 7
)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$assembly = Join-Path $appRoot 'StealthWorkspace.dll'
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Application assembly is missing.' }
$report = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $report) { throw 'Choose a new report path; existing measurements are preserved.' }
$probeRoot = Join-Path (Split-Path $report -Parent) ('vault-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $probeRoot 'Probe.csproj') -Encoding utf8
@'
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

string appPath = Path.GetFullPath(args[0]), output = args[1], fixture = args[2];
int iterations = int.Parse(args[3]);
string directory = Path.GetDirectoryName(appPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) => {
    string dependency = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);
app.GetType("StealthWorkspace.Services.AppPaths")!.GetMethod("SetDataRootOverride")!.Invoke(null, [Path.Combine(fixture, "diagnostics")]);
Type type = app.GetType("StealthWorkspace.Services.VaultService")!;
object vault = Activator.CreateInstance(type, [Path.Combine(fixture, "vault")])!;
MethodInfo verify = type.GetMethod("VerifyPassword")!, lockVault = type.GetMethod("Lock")!;
PropertyInfo state = type.GetProperty("IsUnlocked")!;
await (Task)type.GetMethod("InitializeVaultAsync")!.Invoke(vault, ["ContentionFixture#123", null, null])!;
lockVault.Invoke(vault, null);
void Forget(object? result) => result?.GetType().GetMethod("ForgetKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(result, null);
Forget(verify.Invoke(vault, ["ContentionFixture#123"])); // JIT and PBKDF2 warm-up.
for (int i = 0; i < 20; i++) { _ = state.GetValue(vault); lockVault.Invoke(vault, null); }
var samples = new List<object>();
foreach (string operation in new[] { "IsUnlocked", "Lock" })
for (int sample = 0; sample < iterations; sample++)
{
    using var started = new ManualResetEventSlim();
    object? result = null; Exception? failure = null; double verificationMs = 0;
    var worker = new Thread(() => {
        var watch = Stopwatch.StartNew(); started.Set();
        try { result = verify.Invoke(vault, ["ContentionFixture#123"]); }
        catch (Exception ex) { failure = ex; }
        finally { verificationMs = watch.Elapsed.TotalMilliseconds; }
    }) { IsBackground = true };
    worker.Start(); started.Wait(); Thread.Sleep(2);
    bool verifyingAtProbe = worker.IsAlive;
    var probe = Stopwatch.StartNew();
    if (operation == "Lock") lockVault.Invoke(vault, null); else _ = state.GetValue(vault);
    double elapsed = probe.Elapsed.TotalMilliseconds;
    worker.Join(); Forget(result);
    if (failure != null) throw failure;
    if (!verifyingAtProbe) throw new InvalidOperationException("Verification completed before the probe; duration: " + verificationMs + "ms, status: " + result?.GetType().GetProperty("Status")?.GetValue(result) + ".");
    samples.Add(new { Operation = operation, Sample = sample + 1, OperationMs = elapsed, VerificationMs = verificationMs });
}
File.WriteAllText(output, JsonSerializer.Serialize(new {
    Assembly = appPath, AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(appPath))),
    CreatedUtc = DateTime.UtcNow, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    Iterations = iterations, PasswordIterations = 600000,
    Method = "Warm PBKDF2; dedicated verification thread; probe IsUnlocked or Lock after 2ms; isolated synthetic vault.", Samples = samples
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);
'@ | Set-Content -LiteralPath (Join-Path $probeRoot 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $probeRoot 'Probe.csproj') -c Release -- $assembly $report (Join-Path $probeRoot 'fixture') $Iterations
if ($LASTEXITCODE -ne 0) { throw 'Contention probe failed.' }
$measurements = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
foreach ($operation in 'IsUnlocked','Lock') {
    $values = @($measurements.Samples | Where-Object Operation -eq $operation | Sort-Object OperationMs)
    Write-Output ('{0}: median {1:N3} ms, max {2:N3} ms ({3} samples)' -f $operation, $values[[int][Math]::Floor($values.Count / 2)].OperationMs, $values[-1].OperationMs, $values.Count)
}

