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

if (!(Test-Path -LiteralPath $msbuild)) {
    throw "找不到 MSBuild：$msbuild"
}
if (!(Test-Path -LiteralPath $csc)) {
    throw "找不到 C# compiler：$csc"
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
