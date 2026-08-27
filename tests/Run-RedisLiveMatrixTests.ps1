param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputDir = Join-Path $repoRoot "mySQLPunk\bin\CodexVerify"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$testExe = Join-Path $outputDir "mySQLPunk.RedisLiveMatrixTests.exe"
$source = Join-Path $PSScriptRoot "RedisLiveMatrixTests.cs"

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

& $csc /nologo /platform:anycpu "/out:$testExe" "/r:$appExe" /r:System.Data.dll /r:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$appConfig = "$appExe.config"
if (Test-Path -LiteralPath $appConfig) {
    Copy-Item -LiteralPath $appConfig -Destination "$testExe.config" -Force
}

docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Docker is not ready." }

$cases = @(
    [pscustomobject]@{ Image = "redis:6.2"; Label = "redis-6.2" },
    [pscustomobject]@{ Image = "redis:7"; Label = "redis-7" },
    [pscustomobject]@{ Image = "ghcr.io/microsoft/garnet"; Label = "garnet" }
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($case in $cases) {
    $name = "mysqlpunk-redis-it-" + [Guid]::NewGuid().ToString("N").Substring(0, 10)
    try {
        Write-Host "Starting $($case.Image)..."
        docker run --pull=missing --detach --rm --name $name --publish-all $case.Image | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to start $($case.Image)." }

        $portText = $null
        for ($attempt = 0; $attempt -lt 30 -and !$portText; $attempt++) {
            $portText = docker port $name 6379/tcp 2>$null | Select-Object -First 1
            if (!$portText) { Start-Sleep -Milliseconds 500 }
        }
        if (!$portText -or $portText -notmatch ':(\d+)$') { throw "Unable to resolve the mapped Redis port for $($case.Image)." }
        $port = [int]$Matches[1]

        $ready = $false
        for ($attempt = 0; $attempt -lt 30 -and !$ready; $attempt++) {
            docker exec $name redis-cli ping *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true } else { Start-Sleep -Milliseconds 500 }
        }
        # Garnet 映像沒有 redis-cli；直接交給測試端重試連線。

        & $testExe $port $case.Label
        if ($LASTEXITCODE -ne 0) { throw "Live matrix failed for $($case.Image)." }
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

Write-Host "Redis/Garnet live matrix passed: $($cases.Count)"
