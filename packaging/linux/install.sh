#!/usr/bin/env bash
set -euo pipefail

version="@VERSION@"
runtime="@RUNTIME@"
script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_base="${MYSQLPUNK_INSTALL_BASE:-$data_home/mySQLPunk}"
bin_root="${MYSQLPUNK_BIN_DIR:-$HOME/.local/bin}"
applications_root="${MYSQLPUNK_APPLICATIONS_DIR:-$data_home/applications}"

for path in "$install_base" "$bin_root" "$applications_root"; do
    if [[ "$path" != /* || "$path" == "/" ]]; then
        printf 'Install paths must be absolute and cannot be root: %s\n' "$path" >&2
        exit 2
    fi
done

source_app="$script_root/app"
if [[ ! -x "$source_app/mySQLPunk" ]]; then
    printf 'Package is missing the executable app host: %s\n' "$source_app/mySQLPunk" >&2
    exit 3
fi

mkdir -p "$install_base" "$bin_root" "$applications_root"
wrapper_path="$bin_root/mysqlpunk"
desktop_path="$applications_root/mysqlpunk.desktop"
target_root="$install_base/$version"
if [[ -d "$wrapper_path" || -d "$desktop_path" ]]; then
    printf 'Install destination collides with an existing directory.\n' >&2
    exit 4
fi

transaction_root=$(mktemp -d "$install_base/.transaction-$version.XXXXXX")
staging_root="$transaction_root/staging"
wrapper_temp="$bin_root/.mysqlpunk-wrapper.$$"
desktop_temp="$applications_root/.mysqlpunk.desktop.$$"
committed=0
had_target=0
had_wrapper=0
had_desktop=0
target_switched=0
wrapper_switched=0
desktop_switched=0
cleanup() {
    exit_code=$?
    set +e
    if [[ "$committed" -ne 1 ]]; then
        if [[ "$target_switched" -eq 1 || "$had_target" -eq 1 ]]; then
            rm -rf -- "$target_root"
        fi
        if [[ "$wrapper_switched" -eq 1 || "$had_wrapper" -eq 1 ]]; then
            rm -f -- "$wrapper_path"
        fi
        if [[ "$desktop_switched" -eq 1 || "$had_desktop" -eq 1 ]]; then
            rm -f -- "$desktop_path"
        fi
        if [[ "$had_target" -eq 1 && -e "$transaction_root/target" ]]; then
            mv -- "$transaction_root/target" "$target_root"
        fi
        if [[ "$had_wrapper" -eq 1 && -e "$transaction_root/wrapper" ]]; then
            mv -- "$transaction_root/wrapper" "$wrapper_path"
        fi
        if [[ "$had_desktop" -eq 1 && -e "$transaction_root/desktop" ]]; then
            mv -- "$transaction_root/desktop" "$desktop_path"
        fi
    fi
    rm -rf -- "$staging_root" "$transaction_root"
    rm -f -- "$wrapper_temp" "$desktop_temp"
    trap - EXIT
    exit "$exit_code"
}
trap cleanup EXIT

mkdir "$staging_root"
cp -a "$source_app/." "$staging_root/"
chmod +x "$staging_root/mySQLPunk"

printf '%s\n' \
    '#!/usr/bin/env bash' \
    "# mySQLPunk $version $runtime" \
    "exec \"$target_root/mySQLPunk\" \"\$@\"" > "$wrapper_temp"
chmod +x "$wrapper_temp"

printf '%s\n' \
    '[Desktop Entry]' \
    'Type=Application' \
    'Name=mySQLPunk' \
    'Comment=Cross-platform database workbench' \
    "Exec=\"$target_root/mySQLPunk\" %f" \
    'Icon=utilities-terminal' \
    'Terminal=false' \
    'Categories=Development;Database;' \
    'MimeType=application/sql;text/x-sql;' \
    'StartupNotify=true' \
    "X-mySQLPunk-Version=$version" > "$desktop_temp"
chmod 0644 "$desktop_temp"

if [[ -e "$target_root" ]]; then
    mv -- "$target_root" "$transaction_root/target"
    had_target=1
fi
if [[ -e "$wrapper_path" ]]; then
    mv -- "$wrapper_path" "$transaction_root/wrapper"
    had_wrapper=1
fi
if [[ -e "$desktop_path" ]]; then
    mv -- "$desktop_path" "$transaction_root/desktop"
    had_desktop=1
fi

mv -- "$staging_root" "$target_root"
target_switched=1
mv -- "$wrapper_temp" "$wrapper_path"
wrapper_switched=1
mv -- "$desktop_temp" "$desktop_path"
desktop_switched=1
committed=1
rm -rf -- "$transaction_root"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications_root" >/dev/null 2>&1 || true
fi

printf 'Installed mySQLPunk %s (%s).\nRun: %s\n' "$version" "$runtime" "$wrapper_path"
