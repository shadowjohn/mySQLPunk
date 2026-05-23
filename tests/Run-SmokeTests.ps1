param(
    [string]$Configuration = "Debug",
    [string]$Platform = "Any CPU"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "mySQLPunk.sln"
$projectDir = Join-Path $repoRoot "mySQLPunk"
$outputDir = Join-Path $projectDir "bin\CodexVerify"
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$csc = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"

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

if (!(Test-Path -LiteralPath $msbuild)) {
    throw "找不到 MSBuild：$msbuild"
}
if (!(Test-Path -LiteralPath $csc)) {
    throw "找不到 C# compiler：$csc"
}

Write-Host "NuGet restore：$solutionPath"
$nugetExe = Resolve-NuGetExePath
if (!$nugetExe) {
    throw "找不到 nuget.exe（請安裝 NuGet Command Line，或設定環境變數 NUGET_EXE 指向 nuget.exe）"
}
& $nugetExe restore $solutionPath -NonInteractive
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
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
    $source
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $testExe
exit $LASTEXITCODE
