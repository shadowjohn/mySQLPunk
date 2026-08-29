#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" ]]; then
    printf 'Usage: %s path/to/mySQLPunk-*-linux-*.tar.gz\n' "$0" >&2
    exit 2
fi

asset=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
work_root=$(mktemp -d)
cleanup() {
    rm -rf -- "$work_root"
}
trap cleanup EXIT

while IFS= read -r entry; do
    if [[ "$entry" == /* || "$entry" == "../"* || "$entry" == *"/../"* ]]; then
        printf 'Archive contains an unsafe path: %s\n' "$entry" >&2
        exit 3
    fi
done < <(tar -tzf "$asset")

tar -xzf "$asset" -C "$work_root"
package_root=$(find "$work_root" -mindepth 1 -maxdepth 1 -type d -name 'mySQLPunk-*' -print -quit)
if [[ -z "$package_root" || ! -x "$package_root/install.sh" || ! -x "$package_root/uninstall.sh" ]]; then
    printf 'Archive does not contain executable install and uninstall scripts.\n' >&2
    exit 4
fi

export XDG_DATA_HOME="$work_root/xdg-data"
export XDG_CONFIG_HOME="$work_root/xdg-config"
export MYSQLPUNK_INSTALL_BASE="$work_root/install"
export MYSQLPUNK_BIN_DIR="$work_root/bin"
export MYSQLPUNK_APPLICATIONS_DIR="$work_root/applications"
mkdir -p "$XDG_CONFIG_HOME"

"$package_root/install.sh"
if [[ ! -x "$MYSQLPUNK_BIN_DIR/mysqlpunk" || ! -f "$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop" ]]; then
    printf 'Install smoke test did not create the launcher and desktop entry.\n' >&2
    exit 5
fi

if [[ "${MYSQLPUNK_PACKAGE_RUN_UI:-0}" == "1" ]]; then
    if ! command -v xvfb-run >/dev/null 2>&1; then
        printf 'MYSQLPUNK_PACKAGE_RUN_UI=1 requires xvfb-run.\n' >&2
        exit 6
    fi
    set +e
    timeout 6s xvfb-run -a "$MYSQLPUNK_BIN_DIR/mysqlpunk" > "$work_root/ui.log" 2>&1
    exit_code=$?
    set -e
    if [[ "$exit_code" -ne 124 ]]; then
        printf 'Installed UI did not remain running during startup smoke test (exit %s).\n' "$exit_code" >&2
        cat "$work_root/ui.log" >&2
        exit 7
    fi
fi

"$package_root/uninstall.sh"
if [[ -e "$MYSQLPUNK_BIN_DIR/mysqlpunk" || -e "$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop" || -d "$MYSQLPUNK_INSTALL_BASE" ]]; then
    printf 'Uninstall smoke test left installed files behind.\n' >&2
    exit 8
fi

printf 'Linux package install/start/uninstall smoke test passed: %s\n' "$asset"
