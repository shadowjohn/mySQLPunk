param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputDir = Join-Path $repoRoot "mySQLPunk\bin\CodexVerify"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$testExe = Join-Path $outputDir "mySQLPunk.DatabaseRenameProviderIntegrationTests.exe"
$source = Join-Path $PSScriptRoot "DatabaseRenameProviderIntegrationTests.cs"

if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot "Run-SmokeTests.ps1")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (!(Test-Path -LiteralPath $appExe)) { throw "Application build output was not found: $appExe" }

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

$references = @($appExe, (Join-Path $outputDir "Npgsql.dll"), (Join-Path $outputDir "System.Data.SQLite.dll"))
$referenceArgs = $references | ForEach-Object { "/r:$_" }
$netstandard = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll"
if (Test-Path -LiteralPath $netstandard) { $referenceArgs += "/r:$netstandard" }
& $csc /nologo /platform:anycpu "/out:$testExe" $referenceArgs /r:System.Data.dll /r:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$appConfig = "$appExe.config"
if (Test-Path -LiteralPath $appConfig) { Copy-Item -LiteralPath $appConfig -Destination "$testExe.config" -Force }

& $testExe sqlite
if ($LASTEXITCODE -ne 0) { throw "Live SQLite rename integration failed." }

docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Docker is not ready." }
$failures = New-Object System.Collections.Generic.List[string]

$postgresPassword = "Mp!" + [Guid]::NewGuid().ToString("N")
$postgresName = "mysqlpunk-pg-rename-" + [Guid]::NewGuid().ToString("N").Substring(0, 10)
try {
    Write-Host "Starting postgres:17-alpine..."
    docker run --pull=missing --detach --rm --name $postgresName --publish-all --tmpfs /var/lib/postgresql/data --env "POSTGRES_PASSWORD=$postgresPassword" postgres:17-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to start PostgreSQL." }
    $portText = $null
    for ($attempt = 0; $attempt -lt 30 -and !$portText; $attempt++) {
        $portText = docker port $postgresName 5432/tcp 2>$null | Select-Object -First 1
        if (!$portText) { Start-Sleep -Milliseconds 500 }
    }
    if (!$portText -or $portText -notmatch ':(\d+)$') { throw "Unable to resolve the PostgreSQL port." }
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        docker exec $postgresName pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL did not become ready." }
    & $testExe postgresql ([int]$Matches[1]) $postgresPassword
    if ($LASTEXITCODE -ne 0) { throw "Live PostgreSQL rename integration failed." }
}
catch { $failures.Add("PostgreSQL: $($_.Exception.Message)") }
finally { docker rm --force $postgresName *> $null }

$sqlPassword = "Mp!Strong" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
$sqlName = "mysqlpunk-mssql-rename-" + [Guid]::NewGuid().ToString("N").Substring(0, 10)
try {
    Write-Host "Starting SQL Server 2022..."
    docker run --pull=missing --detach --rm --name $sqlName --publish-all --env "ACCEPT_EULA=Y" --env "MSSQL_SA_PASSWORD=$sqlPassword" mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to start SQL Server." }
    $portText = $null
    for ($attempt = 0; $attempt -lt 30 -and !$portText; $attempt++) {
        $portText = docker port $sqlName 1433/tcp 2>$null | Select-Object -First 1
        if (!$portText) { Start-Sleep -Milliseconds 500 }
    }
    if (!$portText -or $portText -notmatch ':(\d+)$') { throw "Unable to resolve the SQL Server port." }
    & $testExe mssql ([int]$Matches[1]) $sqlPassword
    if ($LASTEXITCODE -ne 0) { throw "Live SQL Server rename integration failed." }
}
catch { $failures.Add("SQL Server: $($_.Exception.Message)") }
finally { docker rm --force $sqlName *> $null }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "SQLite/PostgreSQL/SQL Server database rename integration tests passed: 3"
