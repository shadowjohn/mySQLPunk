param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputDir = Join-Path $repoRoot "mySQLPunk\bin\CodexVerify"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$testExe = Join-Path $outputDir "mySQLPunk.MySqlExportRenameIntegrationTests.exe"
$source = Join-Path $PSScriptRoot "MySqlExportRenameIntegrationTests.cs"

if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot "Run-SmokeTests.ps1")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (!(Test-Path -LiteralPath $appExe)) {
    throw "Application build output was not found: $appExe"
}

$cscCandidates = @(
    "C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
    "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
    "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\Roslyn\csc.exe",
    "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$csc) { throw "Roslyn C# compiler was not found." }

$mysqlConnector = Join-Path $outputDir "MySqlConnector.dll"
& $csc /nologo /platform:anycpu "/out:$testExe" "/r:$appExe" "/r:$mysqlConnector" /r:System.Data.dll /r:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$appConfig = "$appExe.config"
if (Test-Path -LiteralPath $appConfig) {
    Copy-Item -LiteralPath $appConfig -Destination "$testExe.config" -Force
}

docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Docker is not ready." }

$cases = @(
    [pscustomobject]@{ Image = "mysql:5.6"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mysql:5.7"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mysql:8.0"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:10.6"; PasswordEnv = "MARIADB_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:10.11"; PasswordEnv = "MARIADB_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:11.4"; PasswordEnv = "MARIADB_ROOT_PASSWORD" }
)

$password = "Mp!" + [Guid]::NewGuid().ToString("N")
$failures = New-Object System.Collections.Generic.List[string]

foreach ($case in $cases) {
    $name = "mysqlpunk-export-it-" + [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $cliSql = Join-Path ([IO.Path]::GetTempPath()) ("mysqlpunk-cli-" + [Guid]::NewGuid().ToString("N") + ".sql")
    try {
        Write-Host "Starting $($case.Image)..."
        docker run --pull=missing --detach --rm --name $name --publish-all --tmpfs /var/lib/mysql --env "$($case.PasswordEnv)=$password" $case.Image | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to start $($case.Image)." }

        $portText = $null
        for ($attempt = 0; $attempt -lt 30 -and !$portText; $attempt++) {
            $portText = docker port $name 3306/tcp 2>$null | Select-Object -First 1
            if (!$portText) { Start-Sleep -Milliseconds 500 }
        }
        if (!$portText -or $portText -notmatch ':(\d+)$') { throw "Unable to resolve the mapped MySQL port for $($case.Image)." }
        $port = [int]$Matches[1]

        & $testExe $port $case.Image $password $cliSql
        if ($LASTEXITCODE -ne 0) { throw "Live integration test failed for $($case.Image)." }

        docker cp $cliSql "${name}:/tmp/mysqlpunk-export.sql" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to copy the export SQL into $($case.Image)." }
        $client = if ($case.Image.StartsWith("mariadb:")) { "mariadb" } else { "mysql" }
        docker exec --env "MYSQL_PWD=$password" $name sh -c "$client -uroot < /tmp/mysqlpunk-export.sql"
        if ($LASTEXITCODE -ne 0) { throw "The standard $client client could not import the exported SQL for $($case.Image)." }
        $rowCount = docker exec --env "MYSQL_PWD=$password" $name $client -uroot -N -e "SELECT COUNT(*) FROM mysqlpunk_export_it.parent_items;"
        if ($LASTEXITCODE -ne 0 -or ($rowCount | Select-Object -Last 1).Trim() -ne "2") { throw "The standard $client client did not restore the expected rows for $($case.Image)." }
    }
    catch {
        $failures.Add("$($case.Image): $($_.Exception.Message)")
    }
    finally {
        docker rm --force $name *> $null
        Remove-Item -LiteralPath $cliSql -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "MySQL/MariaDB export/import and rename integration tests passed: $($cases.Count)"
