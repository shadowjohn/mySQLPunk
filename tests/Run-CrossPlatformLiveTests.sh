#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mysql_container="mysqlpunk-cross-mysql-test"
mariadb_container="mysqlpunk-cross-mariadb-test"
postgres_container="mysqlpunk-cross-postgres-test"
sqlserver_container="mysqlpunk-cross-sqlserver-test"
postgres_tls_container="mysqlpunk-cross-postgres-tls-test"
sqlserver_tls_container="mysqlpunk-cross-sqlserver-tls-test"
tidb_container="mysqlpunk-cross-tidb-test"
oceanbase_container="mysqlpunk-cross-oceanbase-test"
mysql_image="${MYSQLPUNK_MYSQL_IMAGE:-mysql:8.0}"
mariadb_image="${MYSQLPUNK_MARIADB_IMAGE:-mariadb:11.4}"
postgres_image="${MYSQLPUNK_POSTGRES_IMAGE:-postgres:16-alpine}"
sqlserver_image="${MYSQLPUNK_SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
tidb_image="${MYSQLPUNK_TIDB_IMAGE:-pingcap/tidb:v8.5.1}"
oceanbase_image="${MYSQLPUNK_OCEANBASE_IMAGE:-oceanbase/oceanbase-ce:latest}"
# OceanBase CE 需要約 3 GB 記憶體、開機數分鐘，預設不跑；設 MYSQLPUNK_LIVE_OCEANBASE=1 才加入矩陣。
with_oceanbase="${MYSQLPUNK_LIVE_OCEANBASE:-0}"
test_password="MySQLPunk_test_2026!"

if docker inspect "$mysql_container" >/dev/null 2>&1 ||
   docker inspect "$mariadb_container" >/dev/null 2>&1 ||
   docker inspect "$postgres_container" >/dev/null 2>&1 ||
   docker inspect "$postgres_tls_container" >/dev/null 2>&1 ||
   docker inspect "$sqlserver_tls_container" >/dev/null 2>&1 ||
   docker inspect "$tidb_container" >/dev/null 2>&1 ||
   docker inspect "$oceanbase_container" >/dev/null 2>&1 ||
   docker inspect "$sqlserver_container" >/dev/null 2>&1; then
    echo "Cross-platform test container name is already in use." >&2
    exit 2
fi

tls_directory=$(mktemp -d)
cleanup() {
    docker rm -f "$mysql_container" "$mariadb_container" "$postgres_container" "$postgres_tls_container" "$sqlserver_container" "$sqlserver_tls_container" "$tidb_container" "$oceanbase_container" >/dev/null 2>&1 || true
    rm -rf "$tls_directory"
}
trap cleanup EXIT

# Throwaway CA + server certificate (SAN 127.0.0.1) so PostgreSQL can be verified with VerifyFull.
mkdir -p "$tls_directory/pg" "$tls_directory/pg-init"
openssl req -x509 -newkey rsa:2048 -nodes -days 2 -sha256 \
    -subj "/CN=mySQLPunk cross-platform test CA" \
    -keyout "$tls_directory/ca.key" -out "$tls_directory/ca.crt" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -sha256 \
    -subj "/CN=127.0.0.1" \
    -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
    -addext "extendedKeyUsage=serverAuth" \
    -keyout "$tls_directory/pg/server.key" -out "$tls_directory/pg/server.csr" >/dev/null 2>&1
openssl x509 -req -days 2 -sha256 -CAcreateserial \
    -CA "$tls_directory/ca.crt" -CAkey "$tls_directory/ca.key" \
    -in "$tls_directory/pg/server.csr" -out "$tls_directory/pg/server.crt" \
    -copy_extensions copy >/dev/null 2>&1
chmod 644 "$tls_directory/pg/server.key" "$tls_directory/pg/server.crt" "$tls_directory/ca.crt"
cat > "$tls_directory/pg-init/10-enable-tls.sh" <<'INIT'
#!/usr/bin/env bash
set -euo pipefail
cp /tls/server.crt /tls/server.key "$PGDATA"/
chmod 600 "$PGDATA/server.key"
cat >> "$PGDATA/postgresql.conf" <<CONF
ssl = on
ssl_cert_file = 'server.crt'
ssl_key_file = 'server.key'
CONF
INIT
chmod 755 "$tls_directory/pg-init/10-enable-tls.sh" "$tls_directory" "$tls_directory/pg" "$tls_directory/pg-init"
# SQL Server reuses the same SAN 127.0.0.1 certificate; forceencryption keeps the harness honest about Optional.
cat > "$tls_directory/mssql.conf" <<'CONF'
[network]
tlscert = /tls/server.crt
tlskey = /tls/server.key
tlsprotocols = 1.2
forceencryption = 1
CONF
chmod 644 "$tls_directory/mssql.conf"

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
    --name "$postgres_tls_container" \
    -e POSTGRES_PASSWORD="$test_password" \
    -v "$tls_directory/pg:/tls:ro" \
    -v "$tls_directory/pg-init:/docker-entrypoint-initdb.d:ro" \
    -p 127.0.0.1::5432 \
    "$postgres_image" >/dev/null

docker run -d --rm \
    --name "$sqlserver_container" \
    -e ACCEPT_EULA=Y \
    -e MSSQL_SA_PASSWORD="$test_password" \
    -p 127.0.0.1::1433 \
    "$sqlserver_image" >/dev/null

docker run -d --rm \
    --name "$sqlserver_tls_container" \
    -e ACCEPT_EULA=Y \
    -e MSSQL_SA_PASSWORD="$test_password" \
    -v "$tls_directory/pg:/tls:ro" \
    -v "$tls_directory/mssql.conf:/var/opt/mssql/mssql.conf:ro" \
    -p 127.0.0.1::1433 \
    "$sqlserver_image" >/dev/null

# TiDB（MySQL 協定相容）：單機模式啟動，root 預設無密碼，就緒後透過 MySQL 容器的用戶端設定密碼。
docker run -d --rm \
    --name "$tidb_container" \
    -p 127.0.0.1::4000 \
    "$tidb_image" >/dev/null

if [[ "$with_oceanbase" == "1" ]]; then
    docker run -d \
        --name "$oceanbase_container" \
        --ulimit nofile=65536:65536 \
        --ulimit stack=-1:-1 \
        -e MODE=mini \
        -e OB_TENANT_PASSWORD="$test_password" \
        -p 127.0.0.1::2881 \
        "$oceanbase_image" >/dev/null
fi

mysql_ready=0
mariadb_ready=0
postgres_ready=0
postgres_tls_ready=0
sqlserver_ready=0
sqlserver_tls_ready=0
tidb_ready=0
tidb_address=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$tidb_container")
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
    if [[ "$postgres_tls_ready" -eq 0 ]] &&
       docker exec "$postgres_tls_container" pg_isready -U postgres >/dev/null 2>&1 &&
       docker exec "$postgres_tls_container" psql -U postgres -tAc "SHOW ssl" 2>/dev/null | grep -qx on; then
        postgres_tls_ready=1
    fi
    if [[ "$sqlserver_ready" -eq 0 ]] &&
       docker logs "$sqlserver_container" 2>&1 | grep -F "SQL Server is now ready for client connections" >/dev/null; then
        sqlserver_ready=1
    fi
    if [[ "$sqlserver_tls_ready" -eq 0 ]] &&
       docker logs "$sqlserver_tls_container" 2>&1 | grep -F "SQL Server is now ready for client connections" >/dev/null &&
       docker logs "$sqlserver_tls_container" 2>&1 | grep -F "was successfully loaded for encryption" >/dev/null; then
        sqlserver_tls_ready=1
    fi
    if [[ "$tidb_ready" -eq 0 && "$mysql_ready" -eq 1 ]] &&
       docker exec "$mysql_container" mysql -h "$tidb_address" -P 4000 -uroot \
           -e "ALTER USER 'root'@'%' IDENTIFIED BY '$test_password';" >/dev/null 2>&1; then
        tidb_ready=1
    fi
    if [[ "$mysql_ready" -eq 1 && "$mariadb_ready" -eq 1 && "$postgres_ready" -eq 1 && "$postgres_tls_ready" -eq 1 && "$sqlserver_ready" -eq 1 && "$sqlserver_tls_ready" -eq 1 && "$tidb_ready" -eq 1 ]]; then
        break
    fi
    sleep 1
done

if [[ "$mysql_ready" -ne 1 || "$mariadb_ready" -ne 1 || "$postgres_ready" -ne 1 || "$postgres_tls_ready" -ne 1 || "$sqlserver_ready" -ne 1 || "$sqlserver_tls_ready" -ne 1 || "$tidb_ready" -ne 1 ]]; then
    echo "Database containers did not become ready: MySQL=$mysql_ready MariaDB=$mariadb_ready PostgreSQL=$postgres_ready PostgreSQL-TLS=$postgres_tls_ready SQLServer=$sqlserver_ready SQLServer-TLS=$sqlserver_tls_ready TiDB=$tidb_ready" >&2
    docker logs "$postgres_tls_container" 2>&1 | tail -n 20 >&2 || true
    docker logs "$sqlserver_tls_container" 2>&1 | tail -n 20 >&2 || true
    exit 3
fi

# MySQL 8 generates its own CA at first start; export it so VerifyCA can be exercised against the real server.
docker cp "$mysql_container:/var/lib/mysql/ca.pem" "$tls_directory/mysql-ca.pem"
chmod 644 "$tls_directory/mysql-ca.pem"

oceanbase_environment=()
if [[ "$with_oceanbase" == "1" ]]; then
    oceanbase_ready=0
    for _ in $(seq 1 180); do
        if docker logs "$oceanbase_container" 2>&1 | grep -q "boot success"; then
            oceanbase_ready=1
            break
        fi
        if [[ "$(docker inspect -f '{{.State.Running}}' "$oceanbase_container" 2>/dev/null)" != "true" ]]; then
            break
        fi
        sleep 5
    done
    if [[ "$oceanbase_ready" -ne 1 ]]; then
        echo "OceanBase container did not boot." >&2
        docker logs "$oceanbase_container" 2>&1 | tail -n 20 >&2 || true
        exit 3
    fi
    oceanbase_port=$(docker port "$oceanbase_container" 2881/tcp | sed 's/.*://')
    oceanbase_environment=(
        MYSQLPUNK_OCEANBASE_HOST=127.0.0.1
        MYSQLPUNK_OCEANBASE_PORT="$oceanbase_port"
        MYSQLPUNK_OCEANBASE_USER=root@test
        MYSQLPUNK_OCEANBASE_PASSWORD="$test_password"
    )
fi

mysql_port=$(docker port "$mysql_container" 3306/tcp | sed 's/.*://')
mariadb_port=$(docker port "$mariadb_container" 3306/tcp | sed 's/.*://')
postgres_port=$(docker port "$postgres_container" 5432/tcp | sed 's/.*://')
postgres_tls_port=$(docker port "$postgres_tls_container" 5432/tcp | sed 's/.*://')
sqlserver_port=$(docker port "$sqlserver_container" 1433/tcp | sed 's/.*://')
sqlserver_tls_port=$(docker port "$sqlserver_tls_container" 1433/tcp | sed 's/.*://')
tidb_port=$(docker port "$tidb_container" 4000/tcp | sed 's/.*://')

cd "$repo_root"
env "${oceanbase_environment[@]}" \
MYSQLPUNK_LIVE_TESTS=1 \
MYSQLPUNK_MYSQL_HOST=127.0.0.1 \
MYSQLPUNK_MYSQL_PORT="$mysql_port" \
MYSQLPUNK_MYSQL_USER=root \
MYSQLPUNK_MYSQL_PASSWORD="$test_password" \
MYSQLPUNK_MYSQL_CA_PATH="$tls_directory/mysql-ca.pem" \
MYSQLPUNK_WRONG_CA_PATH="$tls_directory/ca.crt" \
MYSQLPUNK_POSTGRES_TLS_PORT="$postgres_tls_port" \
MYSQLPUNK_POSTGRES_CA_PATH="$tls_directory/ca.crt" \
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
MYSQLPUNK_SQLSERVER_TLS_PORT="$sqlserver_tls_port" \
MYSQLPUNK_SQLSERVER_CERT_PATH="$tls_directory/pg/server.crt" \
MYSQLPUNK_TIDB_HOST=127.0.0.1 \
MYSQLPUNK_TIDB_PORT="$tidb_port" \
MYSQLPUNK_TIDB_USER=root \
MYSQLPUNK_TIDB_PASSWORD="$test_password" \
dotnet run \
    --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj \
    -c Release
