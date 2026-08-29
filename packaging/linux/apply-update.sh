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

usage() {
    printf 'Usage: %s --archive PATH --sha256 HASH --version N.N.N[.N] --runtime <linux-x64|linux-arm64> --wait-pid PID\n' "$0" >&2
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
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ "$archive" != /* || "$archive" == "/" ]]; then
    printf 'Update archive must be an absolute file path.\n' >&2
    exit 2
fi
expected_hash=${expected_hash,,}
if [[ ! "$expected_hash" =~ ^[0-9a-f]{64}$ ]]; then
    printf 'Expected SHA-256 must contain exactly 64 hexadecimal characters.\n' >&2
    exit 2
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    printf 'Update version must be N.N.N or N.N.N.N.\n' >&2
    exit 2
fi
if [[ "$runtime" != "linux-x64" && "$runtime" != "linux-arm64" ]]; then
    printf 'Linux updater only accepts linux-x64 or linux-arm64 packages.\n' >&2
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

state_home="${XDG_STATE_HOME:-$HOME/.local/state}"
state_root="$state_home/mySQLPunk/updates"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_base="${MYSQLPUNK_INSTALL_BASE:-$data_home/mySQLPunk}"
bin_root="${MYSQLPUNK_BIN_DIR:-$HOME/.local/bin}"
applications_root="${MYSQLPUNK_APPLICATIONS_DIR:-$data_home/applications}"
target_root="$install_base/$version"
wrapper_path="$bin_root/mysqlpunk"
desktop_path="$applications_root/mysqlpunk.desktop"

for path in "$state_root" "$install_base" "$bin_root" "$applications_root" "$target_root"; do
    if [[ "$path" != /* || "$path" == "/" ]]; then
        printf 'Update paths must be absolute and cannot be root: %s\n' "$path" >&2
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
printf 'Archive: %s\n' "$archive"

work_root=""
target_backup=""
had_wrapper=0
had_desktop=0
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
            printf 'Linux update lock reservation is missing or owned by another updater.\n' >&2
            exit 3
        fi
        lock_owned=1
        write_lock_owner
        return
    fi

    lock_token=$(printf '%s' "$$-$timestamp-$RANDOM-$archive" | sha256sum | awk '{ print substr($1, 1, 32) }')
    if ! (set -o noclobber; printf '%s\n' "token=$lock_token" "pid=$$" > "$lock_path") 2>/dev/null; then
        owner_pid=$(sed -n 's/^pid=\([1-9][0-9]*\)$/\1/p' "$lock_path" 2>/dev/null | head -n 1)
        if [[ -z "$owner_pid" ]]; then
            printf 'The existing Linux update lock has no verifiable owner.\n' >&2
            exit 3
        fi
        if kill -0 "$owner_pid" 2>/dev/null; then
            printf 'Another mySQLPunk updater already owns the Linux update lock.\n' >&2
            exit 3
        fi
        rm -f -- "$lock_path"
        if ! (set -o noclobber; printf '%s\n' "token=$lock_token" "pid=$$" > "$lock_path") 2>/dev/null; then
            printf 'Could not acquire the Linux update lock.\n' >&2
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
    printf 'Rolling back the previous installation.\n'
    if [[ -n "$new_pid" ]] && kill -0 "$new_pid" 2>/dev/null; then
        kill "$new_pid" 2>/dev/null || true
    fi

    rm -rf -- "$target_root"
    if [[ -n "$target_backup" && -e "$target_backup" ]]; then
        mv -- "$target_backup" "$target_root"
    fi

    rm -f -- "$wrapper_path" "$desktop_path"
    if [[ "$had_wrapper" -eq 1 && -e "$work_root/previous-wrapper" ]]; then
        cp -a -- "$work_root/previous-wrapper" "$wrapper_path.restore.$$"
        mv -f -- "$wrapper_path.restore.$$" "$wrapper_path"
    fi
    if [[ "$had_desktop" -eq 1 && -e "$work_root/previous-desktop" ]]; then
        cp -a -- "$work_root/previous-desktop" "$desktop_path.restore.$$"
        mv -f -- "$desktop_path.restore.$$" "$desktop_path"
    fi

    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$applications_root" >/dev/null 2>&1 || true
    fi
}

cleanup() {
    exit_code=$?
    set +e
    if [[ "$committed" -ne 1 ]]; then
        if [[ "$modifications_started" -eq 1 || ( -n "$target_backup" && -e "$target_backup" ) ]]; then
            restore_previous_installation
            write_failure_result "rollback" "$failure_message"
        elif [[ "$wait_completed" -eq 1 ]]; then
            write_failure_result "failed" "$failure_message"
        fi

        if [[ "$wait_completed" -eq 1 && -f "$wrapper_path" && -x "$wrapper_path" ]]; then
            printf 'Restarting the previous mySQLPunk installation.\n'
            "$wrapper_path" >/dev/null 2>&1 &
        fi
    fi

    if [[ -n "$target_backup" && -e "$target_backup" && "$committed" -eq 1 ]]; then
        rm -rf -- "$target_backup"
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
if [[ ( "$runtime" == "linux-x64" && "$machine_arch" != "x86_64" && "$machine_arch" != "amd64" ) ||
      ( "$runtime" == "linux-arm64" && "$machine_arch" != "aarch64" && "$machine_arch" != "arm64" ) ]]; then
    failure_message="The update package architecture does not match this Linux machine."
    printf '%s Runtime: %s; machine: %s.\n' "$failure_message" "$runtime" "$machine_arch" >&2
    exit 12
fi

work_root=$(mktemp -d "$state_root/.apply-$version.XXXXXX")
private_archive="$work_root/package.tar.gz"
cp -- "$archive" "$private_archive"
actual_hash=$(sha256sum "$private_archive" | awk '{ print tolower($1) }')
if [[ "$actual_hash" != "$expected_hash" ]]; then
    failure_message="The copied update archive failed SHA-256 verification."
    printf '%s Expected %s, got %s.\n' "$failure_message" "$expected_hash" "$actual_hash" >&2
    exit 13
fi

expected_root="mySQLPunk-$version-$runtime"
entries_path="$work_root/entries.txt"
listing_path="$work_root/listing.txt"
tar -tzf "$private_archive" > "$entries_path"
if [[ ! -s "$entries_path" ]]; then
    failure_message="The update archive is empty."
    printf '%s\n' "$failure_message" >&2
    exit 14
fi
while IFS= read -r entry; do
    if [[ -z "$entry" || "$entry" == /* || "$entry" == ".." || "$entry" == ../* ||
          "$entry" == *"/.." || "$entry" == *"/../"* ||
          ( "$entry" != "$expected_root" && "$entry" != "$expected_root/" && "$entry" != "$expected_root/"* ) ]]; then
        failure_message="The update archive contains an unexpected or unsafe path."
        printf '%s Entry: %s\n' "$failure_message" "$entry" >&2
        exit 15
    fi
done < "$entries_path"

tar -tvzf "$private_archive" > "$listing_path"
entry_count=0
total_uncompressed_bytes=0
while IFS= read -r listing; do
    entry_type=${listing:0:1}
    if [[ "$entry_type" != "-" && "$entry_type" != "d" ]]; then
        failure_message="The update archive contains a link or unsupported entry type."
        printf '%s Listing: %s\n' "$failure_message" "$listing" >&2
        exit 16
    fi
    read -r _ _ entry_size _ <<< "$listing"
    if [[ ! "$entry_size" =~ ^[0-9]+$ ]]; then
        failure_message="The update archive listing has an invalid entry size."
        printf '%s Listing: %s\n' "$failure_message" "$listing" >&2
        exit 16
    fi
    entry_count=$((entry_count + 1))
    total_uncompressed_bytes=$((total_uncompressed_bytes + entry_size))
    if [[ "$entry_count" -gt 20000 || "$total_uncompressed_bytes" -gt 1073741824 ]]; then
        failure_message="The update archive exceeds the safe extraction limit."
        printf '%s\n' "$failure_message" >&2
        exit 16
    fi
done < "$listing_path"

extract_root="$work_root/extracted"
mkdir "$extract_root"
tar --no-same-owner -xzf "$private_archive" -C "$extract_root"
package_root="$extract_root/$expected_root"
if [[ ! -d "$package_root" || -L "$package_root" ||
      ! -f "$package_root/install.sh" || -L "$package_root/install.sh" ||
      ! -x "$package_root/app/mySQLPunk" || -L "$package_root/app/mySQLPunk" ]]; then
    failure_message="The update archive does not contain the expected executable package layout."
    printf '%s\n' "$failure_message" >&2
    exit 17
fi
if ! grep -Fxq "version=\"$version\"" "$package_root/install.sh" ||
   ! grep -Fxq "runtime=\"$runtime\"" "$package_root/install.sh"; then
    failure_message="The embedded installer version or runtime does not match the requested update."
    printf '%s\n' "$failure_message" >&2
    exit 18
fi

mkdir -p "$install_base" "$bin_root" "$applications_root"
if [[ -d "$wrapper_path" || -d "$desktop_path" ]]; then
    failure_message="The update destination collides with an existing directory."
    printf '%s\n' "$failure_message" >&2
    exit 19
fi
if [[ -e "$wrapper_path" ]]; then
    cp -a -- "$wrapper_path" "$work_root/previous-wrapper"
    had_wrapper=1
fi
if [[ -e "$desktop_path" ]]; then
    cp -a -- "$desktop_path" "$work_root/previous-desktop"
    had_desktop=1
fi
if [[ -e "$target_root" ]]; then
    target_backup="$install_base/.apply-backup-$version-$$"
    if [[ -e "$target_backup" ]]; then
        failure_message="The update rollback target already exists."
        printf '%s\n' "$failure_message" >&2
        exit 20
    fi
    mv -- "$target_root" "$target_backup"
fi

modifications_started=1
failure_message="The packaged Linux installer failed; the previous installation was restored."
bash "$package_root/install.sh"

if [[ ! -x "$wrapper_path" ]]; then
    failure_message="The update installer did not create an executable launcher."
    printf '%s\n' "$failure_message" >&2
    exit 21
fi

rm -f -- "$result_path"
failure_message="The updated application exited during its startup health check."
"$wrapper_path" >/dev/null 2>&1 &
new_pid=$!
sleep 5
if ! kill -0 "$new_pid" 2>/dev/null; then
    set +e
    wait "$new_pid"
    new_exit=$?
    set -e
    printf '%s Exit code: %s.\n' "$failure_message" "$new_exit" >&2
    exit 22
fi

committed=1
printf 'mySQLPunk %s (%s) remained healthy during the startup check.\n' "$version" "$runtime"
