#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mysql_container="mysqlpunk-cross-mysql-test"
mariadb_container="mysqlpunk-cross-mariadb-test"
postgres_container="mysqlpunk-cross-postgres-test"
sqlserver_container="mysqlpunk-cross-sqlserver-test"
mysql_image="${MYSQLPUNK_MYSQL_IMAGE:-mysql:8.0}"
mariadb_image="${MYSQLPUNK_MARIADB_IMAGE:-mariadb:11.4}"
postgres_image="${MYSQLPUNK_POSTGRES_IMAGE:-postgres:16-alpine}"
sqlserver_image="${MYSQLPUNK_SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
test_password="MySQLPunk_test_2026!"

if docker inspect "$mysql_container" >/dev/null 2>&1 ||
   docker inspect "$mariadb_container" >/dev/null 2>&1 ||
   docker inspect "$postgres_container" >/dev/null 2>&1 ||
   docker inspect "$sqlserver_container" >/dev/null 2>&1; then
    echo "Cross-platform test container name is already in use." >&2
    exit 2
fi

cleanup() {
    docker rm -f "$mysql_container" "$mariadb_container" "$postgres_container" "$sqlserver_container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run -d --rm \
    --name "$mysql_container" \
    -e MYSQL_ROOT_PASSWORD="$test_password" \
    -e MYSQL_ROOT_HOST=% \
    -p 127.0.0.1::3306 \
    "$mysql_image" >/dev/null

docker run -d --rm \
    --name "$mariadb_container" \
    -e MARIADB_ROOT_PASSWORD="$test_password" \
    -e MARIADB_ROOT_HOST=% \
    -p 127.0.0.1::3306 \
    "$mariadb_image" >/dev/null

docker run -d --rm \
    --name "$postgres_container" \
    -e POSTGRES_PASSWORD="$test_password" \
    -p 127.0.0.1::5432 \
    "$postgres_image" >/dev/null

docker run -d --rm \
    --name "$sqlserver_container" \
    -e ACCEPT_EULA=Y \
    -e MSSQL_SA_PASSWORD="$test_password" \
    -p 127.0.0.1::1433 \
    "$sqlserver_image" >/dev/null

mysql_ready=0
mariadb_ready=0
postgres_ready=0
sqlserver_ready=0
for _ in $(seq 1 120); do
    if [[ "$mysql_ready" -eq 0 ]] &&
       docker exec --env MYSQL_PWD="$test_password" "$mysql_container" sh -c '
           if command -v mysqladmin >/dev/null 2>&1; then
               exec mysqladmin ping -h 127.0.0.1 -uroot --silent
           fi
           exec mariadb-admin ping -h 127.0.0.1 -uroot --silent
       ' >/dev/null 2>&1; then
        mysql_ready=1
    fi
    if [[ "$mariadb_ready" -eq 0 ]] &&
       docker exec --env MYSQL_PWD="$test_password" "$mariadb_container" \
           mariadb-admin ping -h 127.0.0.1 -uroot --silent >/dev/null 2>&1; then
        mariadb_ready=1
    fi
    if [[ "$postgres_ready" -eq 0 ]] &&
       docker exec "$postgres_container" pg_isready -U postgres >/dev/null 2>&1; then
        postgres_ready=1
    fi
    if [[ "$sqlserver_ready" -eq 0 ]] &&
       docker logs "$sqlserver_container" 2>&1 | grep -F "SQL Server is now ready for client connections" >/dev/null; then
        sqlserver_ready=1
    fi
    if [[ "$mysql_ready" -eq 1 && "$mariadb_ready" -eq 1 && "$postgres_ready" -eq 1 && "$sqlserver_ready" -eq 1 ]]; then
        break
    fi
    sleep 1
done

if [[ "$mysql_ready" -ne 1 || "$mariadb_ready" -ne 1 || "$postgres_ready" -ne 1 || "$sqlserver_ready" -ne 1 ]]; then
    echo "Database containers did not become ready: MySQL=$mysql_ready MariaDB=$mariadb_ready PostgreSQL=$postgres_ready SQLServer=$sqlserver_ready" >&2
    exit 3
fi

mysql_port=$(docker port "$mysql_container" 3306/tcp | sed 's/.*://')
mariadb_port=$(docker port "$mariadb_container" 3306/tcp | sed 's/.*://')
postgres_port=$(docker port "$postgres_container" 5432/tcp | sed 's/.*://')
sqlserver_port=$(docker port "$sqlserver_container" 1433/tcp | sed 's/.*://')

cd "$repo_root"
MYSQLPUNK_LIVE_TESTS=1 \
MYSQLPUNK_MYSQL_HOST=127.0.0.1 \
MYSQLPUNK_MYSQL_PORT="$mysql_port" \
MYSQLPUNK_MYSQL_USER=root \
MYSQLPUNK_MYSQL_PASSWORD="$test_password" \
MYSQLPUNK_MARIADB_HOST=127.0.0.1 \
MYSQLPUNK_MARIADB_PORT="$mariadb_port" \
MYSQLPUNK_MARIADB_USER=root \
MYSQLPUNK_MARIADB_PASSWORD="$test_password" \
MYSQLPUNK_POSTGRES_HOST=127.0.0.1 \
MYSQLPUNK_POSTGRES_PORT="$postgres_port" \
MYSQLPUNK_POSTGRES_USER=postgres \
MYSQLPUNK_POSTGRES_PASSWORD="$test_password" \
MYSQLPUNK_SQLSERVER_HOST=127.0.0.1 \
MYSQLPUNK_SQLSERVER_PORT="$sqlserver_port" \
MYSQLPUNK_SQLSERVER_USER=sa \
MYSQLPUNK_SQLSERVER_PASSWORD="$test_password" \
dotnet run \
    --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj \
    -c Release
