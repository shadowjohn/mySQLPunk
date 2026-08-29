#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'Linux update apply smoke test only runs on Linux.\n'
    exit 0
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
apply_script="$repo_root/packaging/linux/apply-update.sh"
installer_template="$repo_root/packaging/linux/install.sh"
real_package="${1:-}"
if [[ -n "$real_package" && ! -f "$real_package" ]]; then
    printf 'Optional real Linux x64 package does not exist: %s\n' "$real_package" >&2
    exit 2
fi
work_root=$(mktemp -d)
launched_pid=""
cleanup() {
    if [[ -n "$launched_pid" ]] && kill -0 "$launched_pid" 2>/dev/null; then
        kill "$launched_pid" 2>/dev/null || true
    fi
    rm -rf -- "$work_root"
}
trap cleanup EXIT

export XDG_DATA_HOME="$work_root/xdg-data"
export XDG_STATE_HOME="$work_root/xdg-state"
export MYSQLPUNK_INSTALL_BASE="$work_root/install"
export MYSQLPUNK_BIN_DIR="$work_root/bin"
export MYSQLPUNK_APPLICATIONS_DIR="$work_root/applications"
export MYSQLPUNK_TEST_STARTED="$work_root/started-version"
export MYSQLPUNK_TEST_PID="$work_root/launched-pid"

make_package() {
    version="$1"
    app_behavior="$2"
    runtime="linux-x64"
    package_name="mySQLPunk-$version-$runtime"
    package_root="$work_root/build-$version/$package_name"
    mkdir -p "$package_root/app"
    sed "s/@VERSION@/$version/g; s/@RUNTIME@/$runtime/g" \
        "$installer_template" > "$package_root/install.sh"
    cp "$apply_script" "$package_root/app/apply-update.sh"
    if [[ "$app_behavior" == "healthy" || "$app_behavior" == "unsafe-link" ]]; then
        sed "s/@VERSION@/$version/g" > "$package_root/app/mySQLPunk" <<'EOF'
#!/usr/bin/env bash
printf '%s' '@VERSION@' > "$MYSQLPUNK_TEST_STARTED"
printf '%s' "$$" > "$MYSQLPUNK_TEST_PID"
exec sleep 30
EOF
    else
        sed "s/@VERSION@/$version/g" > "$package_root/app/mySQLPunk" <<'EOF'
#!/usr/bin/env bash
printf '%s' '@VERSION@-failed' > "$MYSQLPUNK_TEST_STARTED"
exit 42
EOF
    fi
    chmod +x "$package_root/install.sh" "$package_root/app/apply-update.sh" "$package_root/app/mySQLPunk"
    if [[ "$app_behavior" == "unsafe-link" ]]; then
        ln -s /tmp "$package_root/app/unsafe-link"
    fi
    archive="$work_root/$package_name.tar.gz"
    tar -C "$work_root/build-$version" -czf "$archive" "$package_name"
    printf '%s\n' "$archive"
}

hash_file() {
    sha256sum "$1" | awk '{ print tolower($1) }'
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

current_archive=$(make_package "1.0.0.1" "healthy")
current_root="$work_root/current"
mkdir "$current_root"
tar -xzf "$current_archive" -C "$current_root"
"$current_root/mySQLPunk-1.0.0.1-linux-x64/install.sh"

healthy_archive=$(make_package "1.0.0.2" "healthy")
wait_pid=$(wait_for_short_process)
reserved_lock_token="0123456789abcdef0123456789abcdef"
mkdir -p "$XDG_STATE_HOME/mySQLPunk/updates"
printf '%s\n' "token=$reserved_lock_token" "pid=$$" > "$XDG_STATE_HOME/mySQLPunk/updates/apply.lock"
"$apply_script" \
    --archive "$healthy_archive" \
    --sha256 "$(hash_file "$healthy_archive")" \
    --version "1.0.0.2" \
    --runtime "linux-x64" \
    --wait-pid "$wait_pid" \
    --lock-token "$reserved_lock_token"

if [[ "$(cat "$MYSQLPUNK_TEST_STARTED")" != "1.0.0.2" ||
      ! -x "$MYSQLPUNK_INSTALL_BASE/1.0.0.2/mySQLPunk" ||
      ! -d "$MYSQLPUNK_INSTALL_BASE/1.0.0.1" ||
      ! -x "$MYSQLPUNK_BIN_DIR/mysqlpunk" ]]; then
    printf 'Healthy update was not installed and launched correctly.\n' >&2
    exit 3
fi
stop_launched_app

failing_archive=$(make_package "1.0.0.3" "fail")
wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$failing_archive" \
    --sha256 "$(hash_file "$failing_archive")" \
    --version "1.0.0.3" \
    --runtime "linux-x64" \
    --wait-pid "$wait_pid"
apply_exit=$?
set -e
if [[ "$apply_exit" -eq 0 ]]; then
    printf 'Failing update unexpectedly passed its startup health check.\n' >&2
    exit 4
fi

for _ in $(seq 1 50); do
    if [[ -f "$MYSQLPUNK_TEST_PID" ]] && kill -0 "$(cat "$MYSQLPUNK_TEST_PID")" 2>/dev/null; then
        break
    fi
    sleep 0.1
done
launched_pid=$(cat "$MYSQLPUNK_TEST_PID")
if [[ "$(cat "$MYSQLPUNK_TEST_STARTED")" != "1.0.0.2" ||
      ! -x "$MYSQLPUNK_INSTALL_BASE/1.0.0.2/mySQLPunk" ||
      -e "$MYSQLPUNK_INSTALL_BASE/1.0.0.3" ||
      "$(sed -n '2p' "$MYSQLPUNK_BIN_DIR/mysqlpunk")" != "# mySQLPunk 1.0.0.2 linux-x64" ]]; then
    printf 'Failed update did not restore and relaunch version 1.0.0.2.\n' >&2
    exit 5
fi
result_path="$XDG_STATE_HOME/mySQLPunk/updates/last-apply-result"
if [[ ! -f "$result_path" ]] || ! grep -Fxq 'status=rollback' "$result_path" ||
   ! grep -Fxq 'version=1.0.0.3' "$result_path"; then
    printf 'Failed update did not persist a rollback result for the UI.\n' >&2
    exit 6
fi

stop_launched_app
wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$healthy_archive" \
    --sha256 "$(printf '0%.0s' {1..64})" \
    --version "1.0.0.2" \
    --runtime "linux-x64" \
    --wait-pid "$wait_pid"
hash_exit=$?
set -e
if [[ "$hash_exit" -eq 0 || ! -f "$result_path" ||
      "$(sed -n '2p' "$MYSQLPUNK_BIN_DIR/mysqlpunk")" != "# mySQLPunk 1.0.0.2 linux-x64" ]] ||
   ! grep -Fxq 'status=failed' "$result_path"; then
    printf 'Apply-time SHA-256 mismatch did not fail closed and preserve the current launcher.\n' >&2
    exit 7
fi

stop_launched_app
unsafe_archive=$(make_package "1.0.0.4" "unsafe-link")
wait_pid=$(wait_for_short_process)
set +e
"$apply_script" \
    --archive "$unsafe_archive" \
    --sha256 "$(hash_file "$unsafe_archive")" \
    --version "1.0.0.4" \
    --runtime "linux-x64" \
    --wait-pid "$wait_pid"
unsafe_exit=$?
set -e
if [[ "$unsafe_exit" -eq 0 || -e "$MYSQLPUNK_INSTALL_BASE/1.0.0.4" ||
      "$(sed -n '2p' "$MYSQLPUNK_BIN_DIR/mysqlpunk")" != "# mySQLPunk 1.0.0.2 linux-x64" ]]; then
    printf 'Archive link entry was not rejected before installation.\n' >&2
    exit 8
fi
stop_launched_app

if [[ -n "$real_package" ]]; then
    if ! command -v xvfb-run >/dev/null 2>&1; then
        printf 'Real package update apply requires xvfb-run.\n' >&2
        exit 9
    fi
    real_package=$(cd "$(dirname "$real_package")" && pwd)/$(basename "$real_package")
    real_name=$(basename "$real_package")
    if [[ ! "$real_name" =~ ^mySQLPunk-([0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?)-linux-x64\.tar\.gz$ ]]; then
        printf 'Real update package name is not a Linux x64 release asset: %s\n' "$real_name" >&2
        exit 9
    fi
    real_version="${BASH_REMATCH[1]}"
    wait_pid=$(wait_for_short_process)
    xvfb-run -a "$apply_script" \
        --archive "$real_package" \
        --sha256 "$(hash_file "$real_package")" \
        --version "$real_version" \
        --runtime "linux-x64" \
        --wait-pid "$wait_pid"
    if [[ ! -x "$MYSQLPUNK_INSTALL_BASE/$real_version/mySQLPunk" ||
          "$(sed -n '2p' "$MYSQLPUNK_BIN_DIR/mysqlpunk")" != "# mySQLPunk $real_version linux-x64" ||
          -e "$result_path" ]]; then
        printf 'Real self-contained package did not complete safe apply and UI startup health check.\n' >&2
        exit 10
    fi
fi
if [[ -e "$XDG_STATE_HOME/mySQLPunk/updates/apply.lock" ]]; then
    printf 'Linux updater left its exclusive lock behind.\n' >&2
    exit 11
fi

printf 'Linux safe update apply and startup rollback smoke test passed.\n'
