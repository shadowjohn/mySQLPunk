#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mysql_container="mysqlpunk-cross-mysql-test"
postgres_container="mysqlpunk-cross-postgres-test"
mysql_image="${MYSQLPUNK_MYSQL_IMAGE:-mysql:8.0}"
postgres_image="${MYSQLPUNK_POSTGRES_IMAGE:-postgres:16-alpine}"
test_password="mysqlpunk-test-only"

if docker inspect "$mysql_container" >/dev/null 2>&1 ||
   docker inspect "$postgres_container" >/dev/null 2>&1; then
    echo "Cross-platform test container name is already in use." >&2
    exit 2
fi

cleanup() {
    docker rm -f "$mysql_container" "$postgres_container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run -d --rm \
    --name "$mysql_container" \
    -e MYSQL_ROOT_PASSWORD="$test_password" \
    -e MYSQL_ROOT_HOST=% \
    -p 127.0.0.1::3306 \
    "$mysql_image" >/dev/null

docker run -d --rm \
    --name "$postgres_container" \
    -e POSTGRES_PASSWORD="$test_password" \
    -p 127.0.0.1::5432 \
    "$postgres_image" >/dev/null

mysql_ready=0
postgres_ready=0
for _ in $(seq 1 45); do
    if [[ "$mysql_ready" -eq 0 ]] &&
       docker exec "$mysql_container" mysqladmin ping -h 127.0.0.1 -p"$test_password" --silent >/dev/null 2>&1; then
        mysql_ready=1
    fi
    if [[ "$postgres_ready" -eq 0 ]] &&
       docker exec "$postgres_container" pg_isready -U postgres >/dev/null 2>&1; then
        postgres_ready=1
    fi
    if [[ "$mysql_ready" -eq 1 && "$postgres_ready" -eq 1 ]]; then
        break
    fi
    sleep 1
done

if [[ "$mysql_ready" -ne 1 || "$postgres_ready" -ne 1 ]]; then
    echo "Database containers did not become ready." >&2
    exit 3
fi

mysql_port=$(docker port "$mysql_container" 3306/tcp | sed 's/.*://')
postgres_port=$(docker port "$postgres_container" 5432/tcp | sed 's/.*://')

cd "$repo_root"
MYSQLPUNK_LIVE_TESTS=1 \
MYSQLPUNK_MYSQL_HOST=127.0.0.1 \
MYSQLPUNK_MYSQL_PORT="$mysql_port" \
MYSQLPUNK_MYSQL_USER=root \
MYSQLPUNK_MYSQL_PASSWORD="$test_password" \
MYSQLPUNK_POSTGRES_HOST=127.0.0.1 \
MYSQLPUNK_POSTGRES_PORT="$postgres_port" \
MYSQLPUNK_POSTGRES_USER=postgres \
MYSQLPUNK_POSTGRES_PASSWORD="$test_password" \
dotnet run \
    --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj \
    -c Release
