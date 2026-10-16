<#
.SYNOPSIS
  End-to-end check of the startup hook against a real VBCSCompiler, without touching the installed SDK.

.DESCRIPTION
  Copies the SDK's Roslyn/bincore to a work folder (a different folder means a different compiler-server pipe,
  so your normal builds are not affected), registers the hook in the copy's VBCSCompiler.runtimeconfig.json,
  then compiles the same generated source with several concurrent `csc -shared` clients, once against an
  unpatched server (VBCSPATCH_DISABLE=1) and once against a patched one, and compares:
    * that the patched server actually loaded the patch (log line with the server's PID)
    * byte-identical deterministic outputs
    * wall time
  Works on Windows and Linux (PowerShell 7).

  On Windows keep -WorkDir short: csc cannot launch a VBCSCompiler.exe whose path exceeds MAX_PATH and silently
  falls back to compiling in-process, which makes both runs meaningless (the script fails if no server starts).

.EXAMPLE
  dotnet build src/VbcsPatch -c Release -o dist
  ./test/e2e.ps1
  ./test/e2e.ps1 -SdkVersion 10.0.402 -Classes 1500 -Analyzers
#>
param(
    [string] $SdkVersion = (dotnet --version),
    [string] $HookPath = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..', 'dist', 'VbcsPatch.Hook.dll')),
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'vbcspatch-e2e'),
    [string] $Source,
    [int] $Classes = 800,
    [int] $Parallel = 8,
    [int] $Rounds = 2,
    [switch] $Analyzers
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $HookPath)) { throw "$HookPath not found. Run: dotnet build src/VbcsPatch -c Release -o dist" }
$onWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'

# `dotnet` on PATH is usually a symlink on Linux; ask it where its SDKs are instead.
if ((dotnet --list-sdks | Select-Object -First 1) -notmatch '\[(.+)\]\s*$') { throw "could not locate the .NET SDKs" }
$dotnetRoot = Split-Path $Matches[1]
$sdkDir = [IO.Path]::Combine($dotnetRoot, 'sdk', $SdkVersion)
$workSdkDir = Join-Path $WorkDir $SdkVersion
$bincore = Join-Path $workSdkDir 'bincore'
New-Item -ItemType Directory -Force $workSdkDir | Out-Null

if (-not $Source) {
    $Source = Join-Path $WorkDir "gen-$Classes.cs"
    if (-not (Test-Path $Source)) {
        dotnet run --project (Join-Path $PSScriptRoot 'Harness') -c Release -- gen --classes $Classes --out $Source
        if ($LASTEXITCODE) { throw "source generation failed" }
    }
}

if (-not (Test-Path $bincore)) {
    Copy-Item -Recurse ([IO.Path]::Combine($sdkDir, 'Roslyn', 'bincore')) $bincore
}

# Register the hook in the *copy* only.
$rcPath = Join-Path $bincore 'VBCSCompiler.runtimeconfig.json'
$rc = Get-Content $rcPath -Raw | ConvertFrom-Json
$rc.runtimeOptions.configProperties | Add-Member -Force -NotePropertyName STARTUP_HOOKS -NotePropertyValue $HookPath
$rc | ConvertTo-Json -Depth 10 | Set-Content $rcPath -Encoding utf8
$runtimeVersion = $rc.runtimeOptions.framework.version

# Reference assemblies matching the server's runtime major version.
$major = $runtimeVersion.Split('.')[0]
$refDir = Get-ChildItem ([IO.Path]::Combine($dotnetRoot, 'packs', 'Microsoft.NETCore.App.Ref')) -Directory |
    Where-Object Name -like "$major.*" | Sort-Object Name | Select-Object -Last 1 |
    ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'ref') -Directory | Select-Object -First 1 }
$rsp = Join-Path $workSdkDir 'refs.rsp'
$lines = @('-noconfig', '-nologo', '-deterministic', '-optimize+', '-target:library', '-nowarn:CS8632')
$lines += Get-ChildItem $refDir.FullName -Filter *.dll | ForEach-Object { "-r:`"$($_.FullName)`"" }
if ($Analyzers) {
    # Expensive: these are dataflow (CFG-based) rules. Exercises AnalyzerExecutor's CFG cache.
    $analyzerDir = [IO.Path]::Combine($sdkDir, 'Sdks', 'Microsoft.NET.Sdk', 'analyzers')
    $lines += "-analyzer:`"$(Join-Path $analyzerDir 'Microsoft.CodeAnalysis.NetAnalyzers.dll')`""
    $lines += "-analyzer:`"$(Join-Path $analyzerDir 'Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll')`""
    $globalConfig = Join-Path $workSdkDir '.globalconfig'
    @(
        'is_global = true',
        'dotnet_diagnostic.CA1508.severity = warning',
        'dotnet_diagnostic.CA2000.severity = warning',
        'dotnet_diagnostic.CA1822.severity = warning'
    ) | Set-Content $globalConfig
    $lines += "-analyzerconfig:`"$globalConfig`""
}
$lines | Set-Content $rsp

function Get-TestServer {
    # Windows: VBCSCompiler.exe (apphost) or dotnet.exe; Linux: VBCSCompiler (apphost) or `dotnet exec .../VBCSCompiler.dll`.
    $procs = if ($onWindows) {
        Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='VBCSCompiler.exe'" |
            ForEach-Object { [pscustomobject]@{ ProcessId = $_.ProcessId; CommandLine = $_.CommandLine } }
    }
    else {
        Get-Process | ForEach-Object { [pscustomobject]@{ ProcessId = $_.Id; CommandLine = $_.CommandLine } }
    }
    $procs | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($bincore) -and $_.CommandLine.Contains('VBCSCompiler') }
}

function Stop-TestServer {
    Get-TestServer | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
}

function Invoke-Csc([string] $out) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($a in @((Join-Path $bincore 'csc.dll'), '-shared', "@$rsp", "-out:$out", $Source)) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    [System.Diagnostics.Process]::Start($psi)
}

$results = @{}
foreach ($mode in 'baseline', 'patched') {
    Stop-TestServer
    $env:VBCSPATCH_DISABLE = if ($mode -eq 'baseline') { '1' } else { $null }
    $env:VBCSPATCH_LOG = Join-Path $WorkDir 'vbcspatch.log'
    $outDir = Join-Path $workSdkDir "out-$mode"
    New-Item -ItemType Directory -Force $outDir | Out-Null

    # Warm-up compile starts the server with this environment.
    $p = Invoke-Csc (Join-Path $outDir 'warmup.dll'); $p.WaitForExit()
    $server = Get-TestServer
    if (-not $server) { throw "no compiler server started from $bincore (csc fell back to in-process compilation)" }

    $times = @()
    for ($round = 1; $round -le $Rounds; $round++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $procs = 1..$Parallel | ForEach-Object { Invoke-Csc (Join-Path $outDir "out$_.dll") }
        $outputs = $procs | ForEach-Object { $_.StandardOutput.ReadToEnd() }
        $procs | ForEach-Object { $_.WaitForExit() }
        $sw.Stop()
        $failed = @($procs | Where-Object ExitCode -ne 0)
        if ($failed.Count) { throw "$mode round ${round}: $($failed.Count) csc runs failed:`n$($outputs | Select-Object -First 1)" }
        $times += $sw.Elapsed.TotalSeconds
        "{0,-8} round {1}: {2,6:F2}s  ({3} x csc, server pid {4})" -f $mode, $round, $sw.Elapsed.TotalSeconds, $Parallel, ($server.ProcessId -join ',')
    }

    $hash = (Get-ChildItem $outDir -Filter 'out*.dll' | Sort-Object Name | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash) -join ''
    $results[$mode] = [pscustomobject]@{
        Times = $times
        Hash = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($hash)))).Hash.Substring(0, 16)
        ServerPid = ($server.ProcessId -join ',')
    }
    Stop-TestServer
}
$env:VBCSPATCH_DISABLE = $null

''
foreach ($mode in 'baseline', 'patched') {
    $r = $results[$mode]
    "{0,-8} outputs={1}  mean={2:F2}s  min={3:F2}s  server pid={4}" -f $mode, $r.Hash, ($r.Times | Measure-Object -Average).Average, ($r.Times | Measure-Object -Minimum).Minimum, $r.ServerPid
}
"identical outputs: $($results.baseline.Hash -eq $results.patched.Hash)"
''
'patch log for the patched server:'
Get-Content $env:VBCSPATCH_LOG -ErrorAction SilentlyContinue | Where-Object { $_.Contains("[pid $($results.patched.ServerPid)]") }
