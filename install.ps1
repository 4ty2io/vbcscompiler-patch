<#
.SYNOPSIS
  Installs / removes the VbcsPatch startup hook for the C# compiler server (VBCSCompiler).

.DESCRIPTION
  1. Builds dist/ if needed (or with -Build) and copies the hook + patch binaries to -InstallDir
     (default: %LOCALAPPDATA%\VbcsPatch on Windows, ~/.local/share/VbcsPatch on Linux,
     ~/Library/Application Support/VbcsPatch on macOS).
  2. Adds "STARTUP_HOOKS": "<InstallDir>/VbcsPatch.Hook.dll" to
     <dotnet>/sdk/<version>/Roslyn/bincore/VBCSCompiler.runtimeconfig.json for each selected SDK,
     so only the compiler server loads it. A backup of the original file is kept next to it.
  If those files aren't writable (Program Files, /usr/share/dotnet, /usr/lib/dotnet), only step 2 is re-run
  elevated: a UAC prompt on Windows, sudo elsewhere. Don't run the whole script as admin/root.

  The running compiler server keeps the old code until it restarts:
      dotnet build-server shutdown --vbcscompiler
  (do that when no build is running; the next build starts a patched server).

.EXAMPLE
  ./install.ps1                       # all SDKs >= 10 of the dotnet on PATH
  ./install.ps1 -SdkVersion 11.0.100
  ./install.ps1 -Status
  ./install.ps1 -Uninstall
#>
param(
    [string[]] $SdkVersion,
    [string] $InstallDir = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VbcsPatch'),
    [string] $SourceDir = (Join-Path $PSScriptRoot 'dist'),
    # Defaults to the dotnet on PATH, resolved through `dotnet --list-sdks` (on Linux `dotnet` is usually a symlink).
    [string] $DotnetRoot,
    [switch] $Build,
    [switch] $Uninstall,
    [switch] $Status,
    # internal: the elevated half
    [switch] $EditRuntimeConfigs,
    [string] $HookPath,
    [switch] $RemoveHook
)

$ErrorActionPreference = 'Stop'
$onWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'

# "-SdkVersion a,b" arrives as one string when the script re-launches itself.
$SdkVersion = @($SdkVersion | Where-Object { $_ } | ForEach-Object { $_ -split ',' })

if (-not $DotnetRoot) {
    $sdkLine = dotnet --list-sdks | Select-Object -First 1
    if ($sdkLine -notmatch '\[(.+)\]\s*$') { throw "Could not locate the .NET SDKs ('dotnet --list-sdks' printed: $sdkLine)" }
    $DotnetRoot = Split-Path $Matches[1]
}
$dotnetRoot = $DotnetRoot

function Get-RuntimeConfigPath($sdkDir) {
    [IO.Path]::Combine($sdkDir.FullName, 'Roslyn', 'bincore', 'VBCSCompiler.runtimeconfig.json')
}

function Get-TargetConfigs {
    $sdks = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object { Test-Path (Get-RuntimeConfigPath $_) }
    if ($SdkVersion.Count) {
        $sdks = $sdks | Where-Object Name -in $SdkVersion
    }
    else {
        # Only Roslyn 5.x servers on .NET 10/11 have been verified.
        $sdks = $sdks | Where-Object { [int]($_.Name.Split('.')[0]) -ge 10 }
    }
    $sdks | ForEach-Object { Get-RuntimeConfigPath $_ }
}

function Write-Json([string] $path, $object) {
    [IO.File]::WriteAllText($path, ($object | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

function Set-Hook([string] $configPath, [string] $hook) {
    $backup = "$configPath.vbcspatch-backup"
    if (-not (Test-Path $backup)) { Copy-Item $configPath $backup }

    $json = Get-Content $configPath -Raw | ConvertFrom-Json
    $props = $json.runtimeOptions.configProperties
    if ($hook) {
        $props | Add-Member -Force -NotePropertyName STARTUP_HOOKS -NotePropertyValue $hook
    }
    elseif ($props.PSObject.Properties['STARTUP_HOOKS']) {
        $props.PSObject.Properties.Remove('STARTUP_HOOKS')
    }
    Write-Json $configPath $json
}

function Test-Writable([string] $path) {
    try { [IO.File]::Open($path, 'Open', 'ReadWrite', 'ReadWrite').Dispose(); $true } catch { $false }
}

# --- elevated half -------------------------------------------------------------------------------
if ($EditRuntimeConfigs) {
    $hook = if ($RemoveHook) { '' } else { $HookPath }
    if (-not $RemoveHook -and -not $hook) { throw "-EditRuntimeConfigs needs -HookPath or -RemoveHook" }
    foreach ($config in Get-TargetConfigs) {
        Set-Hook $config $hook
        "{0}: {1}" -f $config, $(if ($hook) { "hook registered" } else { "hook removed" })
    }
    return
}

# --- status ------------------------------------------------------------------------------------
if ($Status) {
    foreach ($config in Get-TargetConfigs) {
        $hook = (Get-Content $config -Raw | ConvertFrom-Json).runtimeOptions.configProperties.STARTUP_HOOKS
        "sdk {0}: {1}" -f (Get-Item $config).Directory.Parent.Parent.Name, $(if ($hook) { $hook } else { '(not installed)' })
    }
    $log = Join-Path $InstallDir 'vbcspatch.log'
    if (Test-Path $log) { ''; "last log lines ($log):"; Get-Content $log -Tail 5 }
    return
}

# --- normal (non-elevated) flow ------------------------------------------------------------------
if ($env:SUDO_USER -and -not $PSBoundParameters.ContainsKey('InstallDir')) {
    throw "Don't run this script with sudo: it would install into root's home, which the compiler server can't read. Run it as yourself; it uses sudo only for the runtimeconfig edit."
}

$hook = Join-Path $InstallDir 'VbcsPatch.Hook.dll'

if (-not $Uninstall) {
    if ($Build -or -not (Test-Path (Join-Path $SourceDir 'VbcsPatch.Hook.dll'))) {
        "building $SourceDir ..."
        dotnet build ([IO.Path]::Combine($PSScriptRoot, 'src', 'VbcsPatch', 'VbcsPatch.csproj')) -c Release -o $SourceDir -nologo -v:q
        if ($LASTEXITCODE) { throw "build failed" }
    }
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    try {
        Copy-Item (Join-Path $SourceDir '*.dll') $InstallDir -Force
        Copy-Item (Join-Path $SourceDir '*.pdb') $InstallDir -Force -ErrorAction SilentlyContinue
    }
    catch {
        throw "Could not overwrite files in $InstallDir (a compiler server is probably still using them). Run 'dotnet build-server shutdown --vbcscompiler' when no build is running, then retry. $_"
    }
    "binaries copied to $InstallDir"
}

$configs = @(Get-TargetConfigs)
if (-not $configs) { throw "No matching SDKs found under $(Join-Path $dotnetRoot 'sdk')" }

$editParams = @{ EditRuntimeConfigs = $true; DotnetRoot = $dotnetRoot }
if ($Uninstall) { $editParams.RemoveHook = $true } else { $editParams.HookPath = $hook }
if ($SdkVersion.Count) { $editParams.SdkVersion = $SdkVersion }

# The same parameters as command-line arguments, for the elevated child process.
$editArgs = @('-EditRuntimeConfigs', '-DotnetRoot', $dotnetRoot)
$editArgs += if ($Uninstall) { @('-RemoveHook') } else { @('-HookPath', $hook) }
if ($SdkVersion.Count) { $editArgs += @('-SdkVersion', ($SdkVersion -join ',')) }

if (-not ($configs | Where-Object { -not (Test-Writable $_) })) {
    & $PSCommandPath @editParams
}
elseif ($onWindows) {
    "elevating to edit $($configs.Count) VBCSCompiler.runtimeconfig.json file(s) under $dotnetRoot ..."
    $shell = (Get-Process -Id $PID).Path
    $quoted = $editArgs | ForEach-Object { if ($_ -like '-*') { $_ } else { "`"$_`"" } }
    $p = Start-Process $shell -Verb RunAs -Wait -PassThru -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + $quoted)
    if ($p.ExitCode -ne 0) { throw "elevated step failed (exit code $($p.ExitCode))" }
}
else {
    if (-not (Get-Command sudo -ErrorAction SilentlyContinue)) {
        throw "The runtimeconfig files under $dotnetRoot are not writable and sudo is not available. As root, run: pwsh -File $PSCommandPath $($editArgs -join ' ')"
    }
    "using sudo to edit $($configs.Count) VBCSCompiler.runtimeconfig.json file(s) under $dotnetRoot ..."
    & sudo (Get-Process -Id $PID).Path -NoProfile -File $PSCommandPath @editArgs
    if ($LASTEXITCODE) { throw "sudo step failed (exit code $LASTEXITCODE)" }
}

''
& $PSCommandPath -Status -SdkVersion $SdkVersion -InstallDir $InstallDir -DotnetRoot $dotnetRoot
''
if ($Uninstall) {
    "Uninstalled. After 'dotnet build-server shutdown --vbcscompiler' you can delete $InstallDir."
}
else {
    "Installed. Restart the compiler server when no build is running:"
    "    dotnet build-server shutdown --vbcscompiler"
    "The next build's server logs a 'VbcsPatch: ... patched' line to $(Join-Path $InstallDir 'vbcspatch.log')"
}
