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
if [[ -z "$package_root" || ! -x "$package_root/install.sh" || ! -x "$package_root/uninstall.sh" ||
      ! -x "$package_root/app/apply-update.sh" ]]; then
    printf 'Archive does not contain executable install, uninstall and update scripts.\n' >&2
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

target_root=$(find "$MYSQLPUNK_INSTALL_BASE" -mindepth 1 -maxdepth 1 -type d ! -name '.*' -print -quit)
if [[ -z "$target_root" ]]; then
    printf 'Install smoke test did not create a versioned app directory.\n' >&2
    exit 6
fi
printf 'preserve-old-target' > "$target_root/rollback-marker"
printf 'preserve-old-wrapper\n' > "$MYSQLPUNK_BIN_DIR/mysqlpunk"
chmod +x "$MYSQLPUNK_BIN_DIR/mysqlpunk"
printf 'preserve-old-desktop\n' > "$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop"

fake_bin="$work_root/fake-bin"
mkdir -p "$fake_bin"
real_mv=$(command -v mv)
sed \
    -e "s|@REAL_MV@|$real_mv|g" \
    -e "s|@FAIL_DESTINATION@|$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop|g" \
    > "$fake_bin/mv" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
destination="${*: -1}"
source_path="${*: -2:1}"
if [[ "$destination" == "@FAIL_DESTINATION@" && "$source_path" == */.mysqlpunk.desktop.* ]]; then
    exit 97
fi
exec "@REAL_MV@" "$@"
EOF
chmod +x "$fake_bin/mv"

set +e
PATH="$fake_bin:$PATH" "$package_root/install.sh" > "$work_root/rollback.log" 2>&1
rollback_exit=$?
set -e
if [[ "$rollback_exit" -eq 0 ]]; then
    printf 'Transactional install fault injection unexpectedly succeeded.\n' >&2
    exit 7
fi
if [[ "$(cat "$target_root/rollback-marker" 2>/dev/null || true)" != "preserve-old-target" ||
      "$(cat "$MYSQLPUNK_BIN_DIR/mysqlpunk" 2>/dev/null || true)" != "preserve-old-wrapper" ||
      "$(cat "$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop" 2>/dev/null || true)" != "preserve-old-desktop" ]]; then
    printf 'Transactional install did not restore the previous app, launcher and desktop entry.\n' >&2
    cat "$work_root/rollback.log" >&2
    exit 8
fi
if find "$MYSQLPUNK_INSTALL_BASE" "$MYSQLPUNK_BIN_DIR" "$MYSQLPUNK_APPLICATIONS_DIR" \
    -mindepth 1 \( -name '.install-*' -o -name '.transaction-*' -o -name '.mysqlpunk-*' \) \
    -print -quit | grep -q .; then
    printf 'Transactional install rollback left staging files behind.\n' >&2
    exit 9
fi

"$package_root/install.sh"

if [[ "${MYSQLPUNK_PACKAGE_RUN_UI:-0}" == "1" ]]; then
    if ! command -v xvfb-run >/dev/null 2>&1; then
        printf 'MYSQLPUNK_PACKAGE_RUN_UI=1 requires xvfb-run.\n' >&2
        exit 10
    fi
    set +e
    timeout 6s xvfb-run -a "$MYSQLPUNK_BIN_DIR/mysqlpunk" > "$work_root/ui.log" 2>&1
    exit_code=$?
    set -e
    if [[ "$exit_code" -ne 124 ]]; then
        printf 'Installed UI did not remain running during startup smoke test (exit %s).\n' "$exit_code" >&2
        cat "$work_root/ui.log" >&2
        exit 11
    fi
fi

"$package_root/uninstall.sh"
if [[ -e "$MYSQLPUNK_BIN_DIR/mysqlpunk" || -e "$MYSQLPUNK_APPLICATIONS_DIR/mysqlpunk.desktop" || -d "$MYSQLPUNK_INSTALL_BASE" ]]; then
    printf 'Uninstall smoke test left installed files behind.\n' >&2
    exit 12
fi

printf 'Linux package install/start/uninstall smoke test passed: %s\n' "$asset"
