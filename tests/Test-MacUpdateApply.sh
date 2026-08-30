#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
    printf 'macOS update apply smoke test only runs on macOS.\n'
    exit 0
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
apply_script="$repo_root/packaging/macos/apply-update.sh"
real_package="${1:-}"
if [[ -n "$real_package" && ! -f "$real_package" ]]; then
    printf 'Optional real macOS package does not exist: %s\n' "$real_package" >&2
    exit 2
fi

for command_name in clang codesign ditto plutil lipo; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Required macOS test command is unavailable: %s\n' "$command_name" >&2
        exit 2
    fi
done

machine_arch=$(uname -m)
case "$machine_arch" in
    x86_64)
        runtime="osx-x64"
        ;;
    arm64)
        runtime="osx-arm64"
        ;;
    *)
        printf 'Unsupported macOS test architecture: %s\n' "$machine_arch" >&2
        exit 2
        ;;
esac

work_root=$(mktemp -d)
launched_pid=""
cleanup() {
    if [[ -n "$launched_pid" ]] && kill -0 "$launched_pid" 2>/dev/null; then
        kill "$launched_pid" 2>/dev/null || true
    fi
    rm -rf -- "$work_root"
}
trap cleanup EXIT

export MYSQLPUNK_MACOS_STATE_ROOT="$work_root/state"
export MYSQLPUNK_MACOS_HEALTH_SECONDS=1
export MYSQLPUNK_TEST_STARTED="$work_root/started-version"
export MYSQLPUNK_TEST_PID="$work_root/launched-pid"
target_bundle="$work_root/install with spaces/mySQLPunk.app"
mkdir -p "$(dirname "$target_bundle")"

make_bundle() {
    version="$1"
    behavior="$2"
    destination="$3"
    bundle="$work_root/build-$version-$behavior/mySQLPunk.app"
    contents="$bundle/Contents"
    mkdir -p "$contents/MacOS" "$contents/Resources"

    fail_start=0
    if [[ "$behavior" == "fail" ]]; then
        fail_start=1
    fi
    clang \
        -DVERSION_STRING="\"$version\"" \
        -DFAIL_START="$fail_start" \
        -x c -o "$contents/MacOS/mySQLPunk" - <<'EOF'
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(void) {
    const char *started = getenv("MYSQLPUNK_TEST_STARTED");
    const char *pid_path = getenv("MYSQLPUNK_TEST_PID");
    if (started != NULL) {
        FILE *file = fopen(started, "w");
        if (file != NULL) {
            fprintf(file, "%s%s", VERSION_STRING, FAIL_START ? "-failed" : "");
            fclose(file);
        }
    }
    if (pid_path != NULL) {
        FILE *file = fopen(pid_path, "w");
        if (file != NULL) {
            fprintf(file, "%d", getpid());
            fclose(file);
        }
    }
    if (FAIL_START) {
        return 42;
    }
    sleep(30);
    return 0;
}
EOF

    cp "$apply_script" "$contents/MacOS/apply-update.sh"
    chmod +x "$contents/MacOS/mySQLPunk" "$contents/MacOS/apply-update.sh"
    short_version=$(printf '%s' "$version" | awk -F. '{ print $1 "." $2 "." $3 }')
    bundle_version=$(printf '%s' "$version" | tr -d '.')
    sed \
        -e "s/@SHORT_VERSION@/$short_version/g" \
        -e "s/@BUNDLE_VERSION@/$bundle_version/g" \
        -e "s/@RUNTIME@/$runtime/g" \
        "$repo_root/packaging/macos/Info.plist.in" > "$contents/Info.plist"

    codesign --force --deep --sign - "$bundle"
    if [[ "$behavior" == "unsafe-link" ]]; then
        ln -s /tmp "$contents/Resources/unsafe-link"
    fi

    if [[ "$destination" == *.zip ]]; then
        ditto -c -k --sequesterRsrc --keepParent "$bundle" "$destination"
    else
        rm -rf -- "$destination"
        ditto "$bundle" "$destination"
    fi
}

hash_file() {
    shasum -a 256 "$1" | awk '{ print tolower($1) }'
}

wait_for_short_process() {
    sleep 0.2 &
    printf '%s\n' "$!"
}

stop_launched_app() {
    if [[ -f "$MYSQLPUNK_TEST_PID" ]]; then
        launched_pid=$(cat "$MYSQLPUNK_TEST_PID")
        if kill -0 "$launched_pid" 2>/dev/null; then
            kill "$launched_pid" 2>/dev/null || true
        fi
        rm -f -- "$MYSQLPUNK_TEST_PID"
    fi
    launched_pid=""
}

wait_for_restarted_version() {
    expected="$1"
    for _ in $(seq 1 50); do
        if [[ -f "$MYSQLPUNK_TEST_STARTED" && "$(cat "$MYSQLPUNK_TEST_STARTED")" == "$expected" &&
              -f "$MYSQLPUNK_TEST_PID" ]] && kill -0 "$(cat "$MYSQLPUNK_TEST_PID")" 2>/dev/null; then
            return 0
        fi
        sleep 0.1
    done
    return 1
}

make_bundle "1.0.0.1" "healthy" "$target_bundle"

healthy_archive="$work_root/mySQLPunk-1.0.0.2-$runtime.app.zip"
make_bundle "1.0.0.2" "healthy" "$healthy_archive"
wait_pid=$(wait_for_short_process)
reserved_lock_token="0123456789abcdef0123456789abcdef"
mkdir -p "$MYSQLPUNK_MACOS_STATE_ROOT"
printf '%s\n' "token=$reserved_lock_token" "pid=$$" > "$MYSQLPUNK_MACOS_STATE_ROOT/apply.lock"
"$apply_script" \
    --archive "$healthy_archive" \
    --sha256 "$(hash_file "$healthy_archive")" \
    --version "1.0.0.2" \
    --runtime "$runtime" \
    --wait-pid "$wait_pid" \
    --target-bundle "$target_bundle" \
    --lock-token "$reserved_lock_token"

if ! wait_for_restarted_version "1.0.0.2" ||
   [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$target_bundle/Contents/Info.plist")" != "1002" ]]; then
    printf 'Healthy macOS update was not installed and launched correctly.\n' >&2
    exit 3
fi
stop_launched_app

failing_archive="$work_root/mySQLPunk-1.0.0.3-$runtime.app.zip"
make_bundle "1.0.0.3" "fail" "$failing_archive"
wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$failing_archive" \
    --sha256 "$(hash_file "$failing_archive")" \
    --version "1.0.0.3" \
    --runtime "$runtime" \
    --wait-pid "$wait_pid" \
    --target-bundle "$target_bundle"
apply_exit=$?
set -e
if [[ "$apply_exit" -eq 0 ]] || ! wait_for_restarted_version "1.0.0.2"; then
    printf 'Failing macOS update did not roll back and relaunch version 1.0.0.2.\n' >&2
    exit 4
fi
result_path="$MYSQLPUNK_MACOS_STATE_ROOT/last-apply-result"
if [[ ! -f "$result_path" ]] || ! grep -Fxq 'status=rollback' "$result_path" ||
   ! grep -Fxq 'version=1.0.0.3' "$result_path" ||
   ! grep -Fxq "runtime=$runtime" "$result_path"; then
    printf 'Failing macOS update did not persist a rollback result.\n' >&2
    exit 5
fi
stop_launched_app

wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$healthy_archive" \
    --sha256 "$(printf '0%.0s' {1..64})" \
    --version "1.0.0.2" \
    --runtime "$runtime" \
    --wait-pid "$wait_pid" \
    --target-bundle "$target_bundle"
hash_exit=$?
set -e
if [[ "$hash_exit" -eq 0 || ! -f "$result_path" ]] ||
   ! grep -Fxq 'status=failed' "$result_path" ||
   ! wait_for_restarted_version "1.0.0.2"; then
    printf 'Apply-time SHA-256 mismatch did not preserve and relaunch the current macOS app.\n' >&2
    exit 6
fi
stop_launched_app

unsafe_archive="$work_root/mySQLPunk-1.0.0.4-$runtime.app.zip"
make_bundle "1.0.0.4" "unsafe-link" "$unsafe_archive"
wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$unsafe_archive" \
    --sha256 "$(hash_file "$unsafe_archive")" \
    --version "1.0.0.4" \
    --runtime "$runtime" \
    --wait-pid "$wait_pid" \
    --target-bundle "$target_bundle"
unsafe_exit=$?
set -e
if [[ "$unsafe_exit" -eq 0 ]] || ! wait_for_restarted_version "1.0.0.2"; then
    printf 'macOS archive symbolic link was not rejected before replacement.\n' >&2
    exit 7
fi
stop_launched_app

if [[ -n "$real_package" ]]; then
    real_package=$(cd "$(dirname "$real_package")" && pwd)/$(basename "$real_package")
    real_name=$(basename "$real_package")
    if [[ ! "$real_name" =~ ^mySQLPunk-([0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?)-$runtime\.app\.zip$ ]]; then
        printf 'Real update package does not match this Mac: %s\n' "$real_name" >&2
        exit 8
    fi
    real_version="${BASH_REMATCH[1]}"
    wait_pid=$(wait_for_short_process)
    "$apply_script" \
        --archive "$real_package" \
        --sha256 "$(hash_file "$real_package")" \
        --version "$real_version" \
        --runtime "$runtime" \
        --wait-pid "$wait_pid" \
        --target-bundle "$target_bundle"
    real_executable="$target_bundle/Contents/MacOS/mySQLPunk"
    launched_pid=$(pgrep -f "^$real_executable" | head -n 1 || true)
    if [[ -z "$launched_pid" ]] || ! kill -0 "$launched_pid" 2>/dev/null ||
       [[ "$(/usr/libexec/PlistBuddy -c 'Print :MySQLPunkRuntimeIdentifier' "$target_bundle/Contents/Info.plist")" != "$runtime" ]]; then
        printf 'Real macOS package did not complete safe apply and startup health check.\n' >&2
        exit 9
    fi
fi

if [[ -e "$MYSQLPUNK_MACOS_STATE_ROOT/apply.lock" ]]; then
    printf 'macOS updater left its exclusive lock behind.\n' >&2
    exit 10
fi

printf 'macOS safe update apply and startup rollback smoke test passed.\n'
