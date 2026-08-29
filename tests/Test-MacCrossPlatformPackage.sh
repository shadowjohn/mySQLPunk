#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
    printf 'Usage: %s path/to/mySQLPunk-*-osx-*.app.zip [...]\n' "$0" >&2
    exit 2
fi

for command_name in ditto plutil codesign lipo; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Required macOS command is unavailable: %s\n' "$command_name" >&2
        exit 3
    fi
done

work_root=$(mktemp -d)
cleanup() {
    rm -rf -- "$work_root"
}
trap cleanup EXIT

for input_path in "$@"; do
    if [[ ! -f "$input_path" ]]; then
        printf 'macOS package does not exist: %s\n' "$input_path" >&2
        exit 4
    fi

    archive=$(cd "$(dirname "$input_path")" && pwd)/$(basename "$input_path")
    archive_name=$(basename "$archive")
    if [[ ! "$archive_name" =~ ^mySQLPunk-([0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?)-(osx-(x64|arm64))\.app\.zip$ ]]; then
        printf 'Unexpected macOS package name: %s\n' "$archive_name" >&2
        exit 5
    fi

    version="${BASH_REMATCH[1]}"
    runtime="${BASH_REMATCH[3]}"
    architecture="${BASH_REMATCH[4]}"
    if [[ "$architecture" == "x64" ]]; then
        architecture="x86_64"
    fi

    extract_root="$work_root/$runtime"
    mkdir -p "$extract_root"
    ditto -x -k "$archive" "$extract_root"

    app_bundle="$extract_root/mySQLPunk.app"
    info_plist="$app_bundle/Contents/Info.plist"
    executable="$app_bundle/Contents/MacOS/mySQLPunk"
    if [[ ! -d "$app_bundle" || ! -f "$info_plist" || ! -x "$executable" ]]; then
        printf 'Archive is missing the expected executable app bundle: %s\n' "$archive_name" >&2
        exit 6
    fi

    plutil -lint "$info_plist" >/dev/null
    plist_value() {
        /usr/libexec/PlistBuddy -c "Print :$1" "$info_plist"
    }

    short_version=$(printf '%s' "$version" | awk -F. '{ print $1 "." $2 "." $3 }')
    bundle_version=$(printf '%s' "$version" | tr -d '.')
    [[ "$(plist_value CFBundleExecutable)" == "mySQLPunk" ]]
    [[ "$(plist_value CFBundleIdentifier)" == "tw.fcu.gis.mysqlpunk" ]]
    [[ "$(plist_value CFBundleShortVersionString)" == "$short_version" ]]
    [[ "$(plist_value CFBundleVersion)" == "$bundle_version" ]]
    [[ "$(plist_value MySQLPunkRuntimeIdentifier)" == "$runtime" ]]

    lipo "$executable" -verify_arch "$architecture"
    codesign --verify --deep --strict --verbose=2 "$app_bundle"
    printf 'macOS app bundle verification passed: %s\n' "$archive_name"
done
