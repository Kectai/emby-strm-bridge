#!/usr/bin/env sh
set -eu

project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"

export DOTNET_CLI_HOME="$project_root/.local/dotnet-home"
export NUGET_PACKAGES="$project_root/.local/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$project_root/.local/nuget/http-cache"
export STRM_BRIDGE_TEST_ROOT="$project_root/.local/test-work"
export TMPDIR="$project_root/.local/tmp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" \
  "$STRM_BRIDGE_TEST_ROOT" "$TMPDIR" "$project_root/.local/test-results"

dotnet restore Emby.StrmBridge.slnx --disable-build-servers
dotnet build Emby.StrmBridge.slnx --no-restore --configuration Release \
  --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet format Emby.StrmBridge.slnx --no-restore --verify-no-changes --severity warn
dotnet test tests/Emby.StrmBridge.Tests/Emby.StrmBridge.Tests.csproj \
  --no-build --no-restore --configuration Release \
  --disable-build-servers \
  --results-directory "$project_root/.local/test-results"
node --test tests/subtitle-ui.test.cjs
"$project_root/scripts/check-privacy.sh"

if git -C "$project_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git -C "$project_root" diff --check -- .
fi
