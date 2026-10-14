#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
project="$repo_root/tests/DesktopProfileOperationHarness/DesktopProfileOperationHarness.csproj"
command -v xvfb-run >/dev/null
dotnet build "$project" -c Release
fixture_root=$(mktemp -d "${TMPDIR:-/tmp}/mysqlpunk-profile-operations.XXXXXX")
trap 'rm -rf -- "$fixture_root"' EXIT
mkdir -p "$fixture_root/home" "$fixture_root/config" "$fixture_root/data" "$fixture_root/cache"
HOME="$fixture_root/home" XDG_CONFIG_HOME="$fixture_root/config" XDG_DATA_HOME="$fixture_root/data" XDG_CACHE_HOME="$fixture_root/cache" \
    MYSQLPUNK_PROFILE_TEST_ROOT="$fixture_root" timeout 60s xvfb-run -a \
    dotnet "$repo_root/tests/DesktopProfileOperationHarness/bin/Release/net8.0/DesktopProfileOperationHarness.dll"
