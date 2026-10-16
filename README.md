# VbcsPatch

Workaround for [dotnet/roslyn#86006](https://github.com/dotnet/roslyn/issues/86006): lock contention in the C#
compiler server (`VBCSCompiler`) when it compiles many projects in parallel.

VbcsPatch is a .NET [startup hook](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md)
that swaps two internal Roslyn methods for contention-free versions at runtime, using
[MonoMod](https://github.com/MonoMod/MonoMod) detours. No SDK binaries are modified, and one command removes it.
It is meant for developer machines, where one compiler server lives for hours and serves many builds.

> **Unofficial.** Not affiliated with Microsoft or the .NET Foundation. It patches compiler internals at runtime; see
> [Caveats](#caveats).

## What it patches

| Method | Problem | Replacement |
|---|---|---|
| `TokenMap.AddItem` | One lock per module, taken for every new symbol reference (65% of contention events in the issue's trace). | 1024-way striped lock by item hash, `Interlocked` token reservation, lock-free publish-then-copy array growth. |
| `AnalyzerExecutor.OnOperationBlockActionsExecuted` | `ConcurrentDictionary.Count` (takes *all* locks) once per operation block. | Lock-free `ContainsKey` probe, `TryRemove` only when needed. |

The `TokenMap` replacement keeps the field layout and the invariants the unpatched readers rely on: a token is visible
in `_itemIdentityToToken` only after `_items[token]` is written, tokens are dense, and `_items.Length >= _count` once
all adds have completed.

## Results

24-core Windows machine, SDK 11.0.100-rc.2. Other builds were running at the same time, so wall times are noisy.

| | baseline | patched |
|---|---|---|
| Lock wait per iteration (harness, 8 × 1,500 classes) | 46 – 224 s | 12 – 30 s |
| Lock wait per iteration (harness, CFG analyzer, 600 classes) | 86 / 12 / 25 s | 11 / 4 / 5 s |
| Real compiler server, 8 concurrent `csc` clients (`test/e2e.ps1`) | 8.08 s | 7.20 s |

Outputs are byte-identical to the unpatched compiler in every run (SDK 10 and 11, Windows and Linux), with identical
analyzer diagnostics. Verify mode confirmed that all 622,256 tokens across 8 maps were assigned by the patch, i.e. the
original `AddItem` never ran, neither as ReadyToRun code nor after tier-1 recompilation.

## Platforms

| | Status |
|---|---|
| **Windows** | Tested on x64 with SDK 10.0.402 (Roslyn 5.9, .NET 10) and SDK 11.0.100-rc.2 (Roslyn 5.12, .NET 11). |
| **Linux (macOS)** | Tested on Linux x64 (`mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2`). macOS uses the same code path but is untested. |

arm64 is untested. Requires a compiler server on Roslyn 5.x / .NET 10 or 11; building needs the .NET 10 SDK (the test
harness needs .NET 11).

## Install

The installer is a PowerShell script, and **PowerShell runs on Linux and macOS too** (PowerShell 7, `pwsh`). Only the
installer needs it; the patched compiler server doesn't. Run it from a clone of this repo, as your normal user (not
admin, not `sudo`), when no build is running. Then restart the compiler server so the next build starts a patched one.

### Windows

Windows PowerShell 5.1 is built in; PowerShell 7 works too.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
dotnet build-server shutdown --vbcscompiler
```

With the SDK in `C:\Program Files\dotnet`, a UAC prompt appears for the runtimeconfig edit. Files go to
`%LOCALAPPDATA%\VbcsPatch`.

### Linux

Install PowerShell 7 first, e.g. `sudo snap install powershell --classic` on Ubuntu; for other distros see
[Installing PowerShell on Linux](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux).

```bash
pwsh ./install.ps1
dotnet build-server shutdown --vbcscompiler
```

With the SDK in `/usr/share/dotnet` or `/usr/lib/dotnet`, the script asks for your `sudo` password for the
runtimeconfig edit only; with it in a folder you own (e.g. `~/.dotnet`), no `sudo` is needed. A .NET SDK installed as
a snap is read-only and can't be patched. Files go to `~/.local/share/VbcsPatch`.

### macOS (untested)

```bash
brew install --cask powershell
pwsh ./install.ps1
dotnet build-server shutdown --vbcscompiler
```

The official .NET installer puts the SDK in `/usr/local/share/dotnet`, so the script asks for your `sudo` password for
the runtimeconfig edit. Files go to `~/Library/Application Support/VbcsPatch`.

### What the installer does

It builds `dist/`, copies it to the folder above, and adds `STARTUP_HOOKS` to each SDK's
`Roslyn/bincore/VBCSCompiler.runtimeconfig.json` (keeping a `.vbcspatch-backup`). The patched server logs one line to
`vbcspatch.log` in that folder:

```
VbcsPatch: .NET 11.0.0, Roslyn 5.12.0-...; TokenMap.AddItem: patched; AnalyzerExecutor.OnOperationBlockActionsExecuted: patched (413 ms)
```

Other options (prefix with `pwsh` on Linux/macOS, or `powershell -ExecutionPolicy Bypass -File` on Windows):

```powershell
./install.ps1 -SdkVersion 11.0.100   # only these SDKs (comma-separated)
./install.ps1 -Status                # which SDKs have the hook, last log lines
./install.ps1 -Uninstall             # remove the hook
```

Re-run the installer after an SDK update.

## Configuration

Environment variables, inherited by the server from the build that starts it:

| Variable | Effect |
|---|---|
| `VBCSPATCH_DISABLE=1` | Do nothing. |
| `VBCSPATCH_TOKENMAP=0` / `VBCSPATCH_ANALYZER=0` | Skip one patch. |
| `VBCSPATCH_VERIFY=1` | Check `TokenMap` invariants (tests only; leaks memory). |
| `VBCSPATCH_LOG=path` | Log file location. |

## Caveats

- If a newer Roslyn changes a target method or field, that patch is skipped and the log says `FAILED`.
- If the hook DLL goes missing, the server fails to start and `csc` silently compiles in-process (slow builds).
  Run `./install.ps1 -Uninstall` before deleting the install folder.
- Uses the prerelease `MonoMod.RuntimeDetour 25.4.0-prerelease.1`; current Harmony releases reject .NET 11.
- The patch assumes no inlined copy of the original `AddItem` exists (true on the tested builds). Otherwise the log
  shows `WARNING: TokenMap item was added outside the patched AddItem`.

## Development

| Path | |
|---|---|
| `src/VbcsPatch.Hook` | Startup hook (netstandard2.0); loads the patch only into `VBCSCompiler`. |
| `src/VbcsPatch` | The patches, compiled against a publicized `Microsoft.CodeAnalysis` reference. |
| `test/Harness` | In-process benchmark: N concurrent compilations on the SDK's Roslyn. |
| `test/e2e.ps1` | Compares unpatched vs. patched real compiler servers on a private copy of `Roslyn/bincore`. |
| `test/linux-install-test.sh` | Install/uninstall test inside the .NET SDK container. |

```powershell
dotnet build src/VbcsPatch -c Release -o dist
dotnet run --project test/Harness -c Release -- bench [--patch] --parallel 8 --classes 1500 --iterations 4 --analyzers
./test/e2e.ps1                       # on Windows keep -WorkDir short (MAX_PATH)
```

```bash
docker run --rm -v "$PWD:/repo:ro" mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2 bash /repo/test/linux-install-test.sh
```

## License

[MIT](LICENSE)
