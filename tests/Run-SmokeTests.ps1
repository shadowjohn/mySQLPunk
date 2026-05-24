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
    throw "找不到 MSBuild。請安裝 Visual Studio Build Tools，或設定 MSBUILD_EXE 指向 MSBuild.exe。"
}

$csc = Resolve-CscExePath $msbuild
if (!$csc) {
    throw "找不到 C# compiler。請安裝 Roslyn compiler，或設定 CSC_EXE 指向 csc.exe。"
}

Write-Host "NuGet restore：$solutionPath"
$nugetExe = Resolve-NuGetExePath
if ($nugetExe) {
    & $nugetExe restore $solutionPath -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} elseif (Test-Path -LiteralPath (Join-Path $repoRoot "packages")) {
    Write-Warning "找不到 nuget.exe，已偵測到 packages 目錄，略過 NuGet restore。"
} else {
    throw "找不到 nuget.exe，且 packages 目錄不存在。請安裝 NuGet Command Line、設定 NUGET_EXE，或先還原 packages。"
}

& $msbuild $solutionPath /p:Configuration=$Configuration "/p:Platform=$Platform" /p:OutputPath=bin\CodexVerify\ /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$testExe = Join-Path $outputDir "mySQLPunk.SmokeTests.exe"
$source = Join-Path $PSScriptRoot "SmokeTests.cs"
$appExe = Join-Path $outputDir "mySQLPunk.exe"
$newtonsoft = Join-Path $outputDir "Newtonsoft.Json.dll"

& $csc /nologo /platform:anycpu "/out:$testExe" `
    "/r:$appExe" `
    "/r:$newtonsoft" `
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

& $testExe
exit $LASTEXITCODE
