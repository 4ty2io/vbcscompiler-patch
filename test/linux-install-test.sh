#!/usr/bin/env bash
# Exercises install.ps1 on Linux inside mcr.microsoft.com/dotnet/sdk (runs as root, has pwsh, has an 'app' user, no sudo):
#   1. as root: install into the image's /usr/share/dotnet, build a project with the compiler server, check the
#      server logged the patch, uninstall, check the runtimeconfig is back to the original;
#   2. as the non-root 'app' user: the runtimeconfig isn't writable and sudo is missing, so the script must stop
#      with a clear message instead of half-installing.
# Usage (from the repo root):
#   docker run --rm -v "$PWD:/repo:ro" mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2 bash /repo/test/linux-install-test.sh
set -euo pipefail

mkdir -p /work && cd /repo && tar cf - --exclude=bin --exclude=obj --exclude=dist --exclude=.git . | (cd /work && tar xf -)
cd /work
sdkdir="$(dotnet --list-sdks | head -1 | sed -E 's/^(\S+) \[(.+)\]$/\2\/\1/')"
rc="$sdkdir/Roslyn/bincore/VBCSCompiler.runtimeconfig.json"
cp "$rc" /tmp/original.json

echo "=== install as root"
pwsh -NoProfile -File ./install.ps1
grep -q STARTUP_HOOKS "$rc" && echo "runtimeconfig has STARTUP_HOOKS"

echo "=== build with the compiler server"
mkdir -p /tmp/app && cd /tmp/app && dotnet new classlib -o . --force >/dev/null
dotnet build -nologo -v:q -p:UseSharedCompilation=true 2>&1 | grep -E "error|Build succeeded"
dotnet build-server shutdown --vbcscompiler >/dev/null
cat ~/.local/share/VbcsPatch/vbcspatch.log
cd /work

echo "=== uninstall as root"
pwsh -NoProfile -File ./install.ps1 -Uninstall | tail -2
pwsh -NoProfile -Command "if ((Get-Content '$rc' -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 20) -ne (Get-Content /tmp/original.json -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 20)) { throw 'runtimeconfig not restored' } else { 'runtimeconfig restored' }"

echo "=== install as non-root user without sudo (must refuse cleanly)"
cp -r /work /tmp/userwork && chown -R app:app /tmp/userwork
if su app -s /bin/bash -c "cd /tmp/userwork && HOME=/tmp/apphome pwsh -NoProfile -File ./install.ps1" 2>&1 | tail -3; then :; fi
grep -q STARTUP_HOOKS "$rc" && echo "FAIL: runtimeconfig was modified" || echo "runtimeconfig untouched"
