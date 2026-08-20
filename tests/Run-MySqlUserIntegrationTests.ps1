param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputDir = Join-Path $repoRoot "mySQLPunk\bin\CodexVerify"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$testExe = Join-Path $outputDir "mySQLPunk.MySqlUserIntegrationTests.exe"
$source = Join-Path $PSScriptRoot "MySqlUserIntegrationTests.cs"

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
    [pscustomobject]@{ Image = "mysql:5.6"; Expected = "MySQL5"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mysql:5.7"; Expected = "MySQL5"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mysql:8.0"; Expected = "MySQL8"; PasswordEnv = "MYSQL_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:10.6"; Expected = "MariaDB"; PasswordEnv = "MARIADB_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:10.11"; Expected = "MariaDB"; PasswordEnv = "MARIADB_ROOT_PASSWORD" },
    [pscustomobject]@{ Image = "mariadb:11.4"; Expected = "MariaDB"; PasswordEnv = "MARIADB_ROOT_PASSWORD" }
)

$password = "Mp!" + [Guid]::NewGuid().ToString("N")
$failures = New-Object System.Collections.Generic.List[string]

foreach ($case in $cases) {
    $name = "mysqlpunk-user-it-" + [Guid]::NewGuid().ToString("N").Substring(0, 10)
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

        & $testExe $port $case.Expected $password
        if ($LASTEXITCODE -ne 0) { throw "Live integration test failed for $($case.Image)." }
    }
    catch {
        $failures.Add("$($case.Image): $($_.Exception.Message)")
    }
    finally {
        docker rm --force $name *> $null
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "MySQL/MariaDB live integration tests passed: $($cases.Count)"
