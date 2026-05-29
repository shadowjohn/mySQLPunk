param(
    [string]$Configuration = "Debug",
    [string]$Platform = "Any CPU"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "mySQLPunk.sln"
$projectDir = Join-Path $repoRoot "mySQLPunk"
$outputDir = Join-Path $projectDir "bin\CodexVerify"

function Resolve-MSBuildExePath {
    if ($env:MSBUILD_EXE -and (Test-Path -LiteralPath $env:MSBUILD_EXE)) {
        return $env:MSBUILD_EXE
    }

    $msbuildCmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($msbuildCmd -and $msbuildCmd.Source) {
        return $msbuildCmd.Source
    }

    $candidatePaths = @(
        "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($p in $candidatePaths) {
        if (Test-Path -LiteralPath $p) {
            return $p
        }
    }

    return $null
}

function Resolve-CscExePath {
    param([string]$MSBuildPath)

    if ($env:CSC_EXE -and (Test-Path -LiteralPath $env:CSC_EXE)) {
        return $env:CSC_EXE
    }

    if ($MSBuildPath) {
        $fromMsbuild = Join-Path (Split-Path -Parent $MSBuildPath) "Roslyn\csc.exe"
        if (Test-Path -LiteralPath $fromMsbuild) {
            return $fromMsbuild
        }
    }

    $cscCmd = Get-Command csc -ErrorAction SilentlyContinue
    if ($cscCmd -and $cscCmd.Source) {
        return $cscCmd.Source
    }

    return $null
}

function Resolve-NuGetExePath {
    if ($env:NUGET_EXE -and (Test-Path -LiteralPath $env:NUGET_EXE)) {
        return $env:NUGET_EXE
    }

    $nugetCmd = Get-Command nuget -ErrorAction SilentlyContinue
    if ($nugetCmd -and $nugetCmd.Source) {
        return $nugetCmd.Source
    }

    $candidatePaths = @(
        "C:\Program Files (x86)\NuGet\nuget.exe",
        "C:\Program Files\NuGet\nuget.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\NuGet\nuget.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\NuGet\nuget.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\NuGet\nuget.exe"
    )

    foreach ($p in $candidatePaths) {
        if (Test-Path -LiteralPath $p) {
            return $p
        }
    }

    return $null
}

$msbuild = Resolve-MSBuildExePath
if (!$msbuild) {
    throw "MSBuild not found. Install Visual Studio Build Tools or set MSBUILD_EXE to MSBuild.exe."
}

$csc = Resolve-CscExePath $msbuild
if (!$csc) {
    throw "C# compiler not found. Install Roslyn compiler or set CSC_EXE to csc.exe."
}

Write-Host "NuGet restore: $solutionPath"
$nugetExe = Resolve-NuGetExePath
if ($nugetExe) {
    & $nugetExe restore $solutionPath -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} elseif (Test-Path -LiteralPath (Join-Path $repoRoot "packages")) {
    Write-Warning "nuget.exe not found, packages directory detected; skipping NuGet restore."
} else {
    throw "nuget.exe not found and packages directory is missing. Install NuGet Command Line, set NUGET_EXE, or restore packages first."
}

& $msbuild $solutionPath /p:Configuration=$Configuration "/p:Platform=$Platform" /p:OutputPath=bin\CodexVerify\ /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$testExe = Join-Path $outputDir "mySQLPunk.SmokeTests.exe"
$source = Join-Path $PSScriptRoot "SmokeTests.cs"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$newtonsoft = Join-Path $outputDir "Newtonsoft.Json.dll"
$mysqlConnector = Join-Path $outputDir "MySqlConnector.dll"
if (!(Test-Path -LiteralPath $mysqlConnector)) {
    $mysqlConnector = Join-Path $repoRoot "packages\MySqlConnector.2.3.7\lib\net471\MySqlConnector.dll"
}

& $csc /nologo /platform:anycpu "/out:$testExe" `
    "/r:$appExe" `
    "/r:$newtonsoft" `
    "/r:$mysqlConnector" `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Data.dll `
    /r:System.Core.dll `
    /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll `
    $source
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$appConfig = "$appExe.config"
$testConfig = "$testExe.config"
if (Test-Path -LiteralPath $appConfig) {
    Copy-Item -LiteralPath $appConfig -Destination $testConfig -Force
}

& $testExe
exit $LASTEXITCODE
