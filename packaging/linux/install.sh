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
staging_root=$(mktemp -d "$install_base/.install-$version.XXXXXX")
backup_root=""
cleanup() {
    if [[ -d "$staging_root" ]]; then
        rm -rf -- "$staging_root"
    fi
}
trap cleanup EXIT

cp -a "$source_app/." "$staging_root/"
chmod +x "$staging_root/mySQLPunk"
target_root="$install_base/$version"
if [[ -e "$target_root" ]]; then
    backup_root="$install_base/.backup-$version-$$"
    mv -- "$target_root" "$backup_root"
fi

if ! mv -- "$staging_root" "$target_root"; then
    if [[ -n "$backup_root" && -e "$backup_root" ]]; then
        mv -- "$backup_root" "$target_root"
    fi
    exit 4
fi

if [[ -n "$backup_root" && -e "$backup_root" ]]; then
    rm -rf -- "$backup_root"
fi

wrapper_path="$bin_root/mysqlpunk"
wrapper_temp="$bin_root/.mysqlpunk-wrapper.$$"
printf '%s\n' \
    '#!/usr/bin/env bash' \
    "# mySQLPunk $version $runtime" \
    "exec \"$target_root/mySQLPunk\" \"\$@\"" > "$wrapper_temp"
chmod +x "$wrapper_temp"
mv -f -- "$wrapper_temp" "$wrapper_path"

desktop_path="$applications_root/mysqlpunk.desktop"
desktop_temp="$applications_root/.mysqlpunk.desktop.$$"
printf '%s\n' \
    '[Desktop Entry]' \
    'Type=Application' \
    'Name=mySQLPunk' \
    'Comment=Cross-platform database workbench' \
    "Exec=\"$target_root/mySQLPunk\"" \
    'Icon=utilities-terminal' \
    'Terminal=false' \
    'Categories=Development;Database;' \
    "X-mySQLPunk-Version=$version" > "$desktop_temp"
chmod 0644 "$desktop_temp"
mv -f -- "$desktop_temp" "$desktop_path"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications_root" >/dev/null 2>&1 || true
fi

printf 'Installed mySQLPunk %s (%s).\nRun: %s\n' "$version" "$runtime" "$wrapper_path"
