#!/usr/bin/env bash
set -euo pipefail

version="@VERSION@"
runtime="@RUNTIME@"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_base="${MYSQLPUNK_INSTALL_BASE:-$data_home/mySQLPunk}"
bin_root="${MYSQLPUNK_BIN_DIR:-$HOME/.local/bin}"
applications_root="${MYSQLPUNK_APPLICATIONS_DIR:-$data_home/applications}"
target_root="$install_base/$version"

for path in "$install_base" "$bin_root" "$applications_root" "$target_root"; do
    if [[ "$path" != /* || "$path" == "/" ]]; then
        printf 'Uninstall paths must be absolute and cannot be root: %s\n' "$path" >&2
        exit 2
    fi
done

wrapper_path="$bin_root/mysqlpunk"
if [[ -f "$wrapper_path" ]] && grep -Fq "# mySQLPunk $version $runtime" "$wrapper_path"; then
    rm -f -- "$wrapper_path"
fi

desktop_path="$applications_root/mysqlpunk.desktop"
if [[ -f "$desktop_path" ]] && grep -Fq "X-mySQLPunk-Version=$version" "$desktop_path"; then
    rm -f -- "$desktop_path"
fi

if [[ -d "$target_root" ]]; then
    rm -rf -- "$target_root"
fi
rmdir "$install_base" >/dev/null 2>&1 || true

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications_root" >/dev/null 2>&1 || true
fi

printf 'Removed mySQLPunk %s (%s). Connection profiles were preserved.\n' "$version" "$runtime"
