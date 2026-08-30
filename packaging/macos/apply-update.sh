#!/usr/bin/env bash
set -euo pipefail
umask 077
export LC_ALL=C

archive=""
expected_hash=""
version=""
runtime=""
wait_pid=""
lock_token=""
target_bundle=""

usage() {
    printf 'Usage: %s --archive PATH --sha256 HASH --version N.N.N[.N] --runtime <osx-x64|osx-arm64> --wait-pid PID --target-bundle PATH\n' "$0" >&2
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --archive)
            archive="${2:-}"
            shift 2
            ;;
        --sha256)
            expected_hash="${2:-}"
            shift 2
            ;;
        --version)
            version="${2:-}"
            shift 2
            ;;
        --runtime)
            runtime="${2:-}"
            shift 2
            ;;
        --wait-pid)
            wait_pid="${2:-}"
            shift 2
            ;;
        --lock-token)
            lock_token="${2:-}"
            shift 2
            ;;
        --target-bundle)
            target_bundle="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    printf 'The macOS updater can only run on macOS.\n' >&2
    exit 2
fi
if [[ "$archive" != /* || "$archive" == "/" ]]; then
    printf 'Update archive must be an absolute file path.\n' >&2
    exit 2
fi
expected_hash=$(printf '%s' "$expected_hash" | tr '[:upper:]' '[:lower:]')
if [[ ! "$expected_hash" =~ ^[0-9a-f]{64}$ ]]; then
    printf 'Expected SHA-256 must contain exactly 64 hexadecimal characters.\n' >&2
    exit 2
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    printf 'Update version must be N.N.N or N.N.N.N.\n' >&2
    exit 2
fi
if [[ "$runtime" != "osx-x64" && "$runtime" != "osx-arm64" ]]; then
    printf 'macOS updater only accepts osx-x64 or osx-arm64 packages.\n' >&2
    exit 2
fi
if [[ ! "$wait_pid" =~ ^[1-9][0-9]*$ || "$wait_pid" -eq "$$" ]]; then
    printf 'Wait PID must be a different positive process id.\n' >&2
    exit 2
fi
if [[ -n "$lock_token" && ! "$lock_token" =~ ^[0-9a-f]{32}$ ]]; then
    printf 'Update lock token must contain exactly 32 lowercase hexadecimal characters.\n' >&2
    exit 2
fi
if [[ "$target_bundle" != /* || "$target_bundle" == "/" ||
      "$(basename "$target_bundle")" != "mySQLPunk.app" ]]; then
    printf 'Target bundle must be an absolute mySQLPunk.app path.\n' >&2
    exit 2
fi
if [[ ! -d "$target_bundle" || -L "$target_bundle" ]]; then
    printf 'The current mySQLPunk.app bundle does not exist or is a symbolic link.\n' >&2
    exit 2
fi

state_root="${MYSQLPUNK_MACOS_STATE_ROOT:-$HOME/Library/Application Support/mySQLPunk/updates}"
target_parent=$(dirname "$target_bundle")
for path in "$state_root" "$target_parent" "$target_bundle"; do
    if [[ "$path" != /* || "$path" == "/" ]]; then
        printf 'Update paths must be absolute and cannot be root: %s\n' "$path" >&2
        exit 2
    fi
done

health_seconds="${MYSQLPUNK_MACOS_HEALTH_SECONDS:-5}"
if [[ ! "$health_seconds" =~ ^[1-9][0-9]*$ || "$health_seconds" -gt 30 ]]; then
    printf 'macOS update health duration must be between 1 and 30 seconds.\n' >&2
    exit 2
fi

for command_path in /usr/bin/ditto /usr/bin/unzip /usr/bin/shasum /usr/bin/codesign /usr/bin/lipo /usr/bin/plutil /usr/libexec/PlistBuddy; do
    if [[ ! -x "$command_path" ]]; then
        printf 'Required macOS update command is unavailable: %s\n' "$command_path" >&2
        exit 2
    fi
done

mkdir -p "$state_root"
chmod 0700 "$state_root" 2>/dev/null || true
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
log_path="$state_root/apply-$version-$timestamp-$$.log"
result_path="$state_root/last-apply-result"
lock_path="$state_root/apply.lock"
exec >> "$log_path" 2>&1

printf 'Starting mySQLPunk update to %s (%s).\n' "$version" "$runtime"
printf 'Archive: %s\nTarget bundle: %s\n' "$archive" "$target_bundle"

work_root=""
backup_bundle=""
modifications_started=0
wait_completed=0
committed=0
new_pid=""
failure_message="Update apply failed before completion."
lock_owned=0

write_lock_owner() {
    lock_temp="$state_root/.apply.lock.$$"
    printf '%s\n' "token=$lock_token" "pid=$$" > "$lock_temp"
    chmod 0600 "$lock_temp"
    mv -f -- "$lock_temp" "$lock_path"
}

acquire_or_claim_lock() {
    if [[ -n "$lock_token" ]]; then
        if [[ ! -f "$lock_path" ]] || ! grep -Fxq "token=$lock_token" "$lock_path"; then
            printf 'macOS update lock reservation is missing or owned by another updater.\n' >&2
            exit 3
        fi
        lock_owned=1
        write_lock_owner
        return
    fi

    lock_token=$(printf '%s' "$$-$timestamp-$RANDOM-$archive" | /usr/bin/shasum -a 256 | awk '{ print substr($1, 1, 32) }')
    if ! (set -o noclobber; printf '%s\n' "token=$lock_token" "pid=$$" > "$lock_path") 2>/dev/null; then
        owner_pid=$(sed -n 's/^pid=\([1-9][0-9]*\)$/\1/p' "$lock_path" 2>/dev/null | head -n 1)
        if [[ -z "$owner_pid" ]]; then
            printf 'The existing macOS update lock has no verifiable owner.\n' >&2
            exit 3
        fi
        if kill -0 "$owner_pid" 2>/dev/null; then
            printf 'Another mySQLPunk updater already owns the macOS update lock.\n' >&2
            exit 3
        fi
        rm -f -- "$lock_path"
        if ! (set -o noclobber; printf '%s\n' "token=$lock_token" "pid=$$" > "$lock_path") 2>/dev/null; then
            printf 'Could not acquire the macOS update lock.\n' >&2
            exit 3
        fi
    fi
    lock_owned=1
    chmod 0600 "$lock_path"
}

write_failure_result() {
    status="$1"
    message="$2"
    result_temp="$state_root/.last-apply-result.$$"
    printf '%s\n' \
        "status=$status" \
        "version=$version" \
        "runtime=$runtime" \
        "message=$message" \
        "log=$log_path" > "$result_temp"
    chmod 0600 "$result_temp"
    mv -f -- "$result_temp" "$result_path"
}

restore_previous_installation() {
    printf 'Rolling back the previous macOS app bundle.\n'
    if [[ -n "$new_pid" ]] && kill -0 "$new_pid" 2>/dev/null; then
        kill "$new_pid" 2>/dev/null || true
    fi

    rm -rf -- "$target_bundle"
    if [[ -n "$backup_bundle" && -d "$backup_bundle" ]]; then
        mv -- "$backup_bundle" "$target_bundle"
    fi
}

cleanup() {
    exit_code=$?
    set +e
    if [[ "$committed" -ne 1 ]]; then
        if [[ "$modifications_started" -eq 1 || ( -n "$backup_bundle" && -d "$backup_bundle" ) ]]; then
            restore_previous_installation
            write_failure_result "rollback" "$failure_message"
        elif [[ "$wait_completed" -eq 1 ]]; then
            write_failure_result "failed" "$failure_message"
        fi

        if [[ "$wait_completed" -eq 1 && -x "$target_bundle/Contents/MacOS/mySQLPunk" ]]; then
            printf 'Restarting the previous mySQLPunk app bundle.\n'
            "$target_bundle/Contents/MacOS/mySQLPunk" >/dev/null 2>&1 &
        fi
    fi

    if [[ -n "$backup_bundle" && -d "$backup_bundle" && "$committed" -eq 1 ]]; then
        rm -rf -- "$backup_bundle"
    fi
    if [[ -n "$work_root" && -d "$work_root" ]]; then
        rm -rf -- "$work_root"
    fi
    if [[ "$lock_owned" -eq 1 && -f "$lock_path" ]] && grep -Fxq "token=$lock_token" "$lock_path"; then
        rm -f -- "$lock_path"
    fi
    trap - EXIT
    exit "$exit_code"
}
trap cleanup EXIT

acquire_or_claim_lock

for _ in $(seq 1 1200); do
    if ! kill -0 "$wait_pid" 2>/dev/null; then
        wait_completed=1
        break
    fi
    sleep 0.1
done
if [[ "$wait_completed" -ne 1 ]]; then
    failure_message="Timed out waiting for the current mySQLPunk process to exit."
    printf '%s\n' "$failure_message" >&2
    exit 10
fi

if [[ ! -f "$archive" ]]; then
    failure_message="The verified update archive no longer exists."
    printf '%s\n' "$failure_message" >&2
    exit 11
fi

machine_arch=$(uname -m)
if [[ ( "$runtime" == "osx-x64" && "$machine_arch" != "x86_64" ) ||
      ( "$runtime" == "osx-arm64" && "$machine_arch" != "arm64" ) ]]; then
    failure_message="The update package architecture does not match this Mac."
    printf '%s Runtime: %s; machine: %s.\n' "$failure_message" "$runtime" "$machine_arch" >&2
    exit 12
fi

work_root=$(mktemp -d "$target_parent/.mysqlpunk-update-$version.XXXXXX")
private_archive="$work_root/package.app.zip"
cp -- "$archive" "$private_archive"
actual_hash=$(/usr/bin/shasum -a 256 "$private_archive" | awk '{ print tolower($1) }')
if [[ "$actual_hash" != "$expected_hash" ]]; then
    failure_message="The copied update archive failed SHA-256 verification."
    printf '%s Expected %s, got %s.\n' "$failure_message" "$expected_hash" "$actual_hash" >&2
    exit 13
fi

entries_path="$work_root/entries.txt"
/usr/bin/unzip -Z1 "$private_archive" > "$entries_path"
if [[ ! -s "$entries_path" ]]; then
    failure_message="The update archive is empty."
    printf '%s\n' "$failure_message" >&2
    exit 14
fi
entry_count=0
while IFS= read -r entry; do
    entry_count=$((entry_count + 1))
    if [[ -z "$entry" || "$entry" == /* || "$entry" == ".." || "$entry" == ../* ||
          "$entry" == *"/.." || "$entry" == *"/../"* ]]; then
        failure_message="The update archive contains an unexpected or unsafe path."
        printf '%s Entry: %s\n' "$failure_message" "$entry" >&2
        exit 15
    fi
    if [[ "$entry" != "mySQLPunk.app" && "$entry" != "mySQLPunk.app/" &&
          "$entry" != "mySQLPunk.app/"* &&
          "$entry" != "__MACOSX" && "$entry" != "__MACOSX/" &&
          "$entry" != "__MACOSX/._mySQLPunk.app" &&
          "$entry" != "__MACOSX/mySQLPunk.app" &&
          "$entry" != "__MACOSX/mySQLPunk.app/" &&
          "$entry" != "__MACOSX/mySQLPunk.app/"* ]]; then
        failure_message="The update archive contains an unexpected top-level path."
        printf '%s Entry: %s\n' "$failure_message" "$entry" >&2
        exit 15
    fi
    if [[ "$entry_count" -gt 20000 ]]; then
        failure_message="The update archive exceeds the safe entry limit."
        printf '%s\n' "$failure_message" >&2
        exit 16
    fi
done < "$entries_path"

listing_path="$work_root/listing.txt"
/usr/bin/unzip -Z -l "$private_archive" > "$listing_path"
listing_count=0
listed_uncompressed_bytes=0
while IFS= read -r listing; do
    entry_type=${listing:0:1}
    if [[ "$entry_type" != "-" && "$entry_type" != "d" ]]; then
        if [[ "$entry_type" == "b" || "$entry_type" == "c" || "$entry_type" == "l" ||
              "$entry_type" == "p" || "$entry_type" == "s" ]]; then
            failure_message="The update archive contains a link or unsupported entry type."
            printf '%s Listing: %s\n' "$failure_message" "$listing" >&2
            exit 16
        fi
        continue
    fi
    read -r _ _ _ entry_size _ <<< "$listing"
    if [[ ! "$entry_size" =~ ^[0-9]+$ ]]; then
        failure_message="The update archive listing has an invalid entry size."
        printf '%s Listing: %s\n' "$failure_message" "$listing" >&2
        exit 16
    fi
    listing_count=$((listing_count + 1))
    listed_uncompressed_bytes=$((listed_uncompressed_bytes + entry_size))
    if [[ "$listing_count" -gt 20000 || "$listed_uncompressed_bytes" -gt 1073741824 ]]; then
        failure_message="The update archive exceeds the safe extraction limit."
        printf '%s\n' "$failure_message" >&2
        exit 16
    fi
done < "$listing_path"
if [[ "$listing_count" -ne "$entry_count" ]]; then
    failure_message="The update archive listing contains an unrecognized entry type."
    printf '%s Expected %s entries, recognized %s.\n' "$failure_message" "$entry_count" "$listing_count" >&2
    exit 16
fi

extract_root="$work_root/extracted"
mkdir "$extract_root"
/usr/bin/ditto -x -k "$private_archive" "$extract_root"
rm -rf -- "$extract_root/__MACOSX"
new_bundle="$extract_root/mySQLPunk.app"
if [[ ! -d "$new_bundle" || -L "$new_bundle" ||
      ! -f "$new_bundle/Contents/Info.plist" || -L "$new_bundle/Contents/Info.plist" ||
      ! -x "$new_bundle/Contents/MacOS/mySQLPunk" || -L "$new_bundle/Contents/MacOS/mySQLPunk" ||
      ! -x "$new_bundle/Contents/MacOS/apply-update.sh" || -L "$new_bundle/Contents/MacOS/apply-update.sh" ]]; then
    failure_message="The update archive does not contain the expected executable app bundle."
    printf '%s\n' "$failure_message" >&2
    exit 17
fi
if find "$new_bundle" -type l -print -quit | grep -q .; then
    failure_message="The update archive contains a symbolic link."
    printf '%s\n' "$failure_message" >&2
    exit 17
fi
if find "$extract_root" -mindepth 1 -maxdepth 1 ! -name mySQLPunk.app -print -quit | grep -q .; then
    failure_message="The update archive contains an unexpected top-level item."
    printf '%s\n' "$failure_message" >&2
    exit 17
fi

file_count=0
total_uncompressed_bytes=0
while IFS= read -r -d '' file_path; do
    file_size=$(stat -f '%z' "$file_path")
    if [[ ! "$file_size" =~ ^[0-9]+$ ]]; then
        failure_message="The extracted app contains a file with an invalid size."
        printf '%s File: %s\n' "$failure_message" "$file_path" >&2
        exit 18
    fi
    file_count=$((file_count + 1))
    total_uncompressed_bytes=$((total_uncompressed_bytes + file_size))
    if [[ "$file_count" -gt 20000 || "$total_uncompressed_bytes" -gt 1073741824 ]]; then
        failure_message="The extracted app exceeds the safe size limit."
        printf '%s\n' "$failure_message" >&2
        exit 18
    fi
done < <(find "$new_bundle" -type f -print0)

info_plist="$new_bundle/Contents/Info.plist"
/usr/bin/plutil -lint "$info_plist" >/dev/null
plist_value() {
    /usr/libexec/PlistBuddy -c "Print :$1" "$info_plist"
}
short_version=$(printf '%s' "$version" | awk -F. '{ print $1 "." $2 "." $3 }')
bundle_version=$(printf '%s' "$version" | tr -d '.')
if [[ "$(plist_value CFBundleExecutable)" != "mySQLPunk" ||
      "$(plist_value CFBundleIdentifier)" != "tw.fcu.gis.mysqlpunk" ||
      "$(plist_value CFBundleShortVersionString)" != "$short_version" ||
      "$(plist_value CFBundleVersion)" != "$bundle_version" ||
      "$(plist_value MySQLPunkRuntimeIdentifier)" != "$runtime" ]]; then
    failure_message="The app bundle metadata does not match the requested update."
    printf '%s\n' "$failure_message" >&2
    exit 19
fi

expected_arch="arm64"
if [[ "$runtime" == "osx-x64" ]]; then
    expected_arch="x86_64"
fi
/usr/bin/lipo "$new_bundle/Contents/MacOS/mySQLPunk" -verify_arch "$expected_arch"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$new_bundle"

backup_bundle="$target_parent/.mySQLPunk-backup-$lock_token.app"
if [[ -e "$backup_bundle" ]]; then
    failure_message="The macOS update rollback target already exists."
    printf '%s\n' "$failure_message" >&2
    exit 20
fi

mv -- "$target_bundle" "$backup_bundle"
modifications_started=1
failure_message="The new macOS app bundle could not replace the previous installation."
mv -- "$new_bundle" "$target_bundle"

rm -f -- "$result_path"
failure_message="The updated macOS application exited during its startup health check."
"$target_bundle/Contents/MacOS/mySQLPunk" >/dev/null 2>&1 &
new_pid=$!
sleep "$health_seconds"
if ! kill -0 "$new_pid" 2>/dev/null; then
    set +e
    wait "$new_pid"
    new_exit=$?
    set -e
    printf '%s Exit code: %s.\n' "$failure_message" "$new_exit" >&2
    exit 21
fi

committed=1
printf 'mySQLPunk %s (%s) remained healthy during the startup check.\n' "$version" "$runtime"
