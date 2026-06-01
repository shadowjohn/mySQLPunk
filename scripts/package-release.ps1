param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Get-RepositoryRoot {
    $current = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $current) {
        if ((Test-Path -LiteralPath (Join-Path $current.FullName "mySQLPunk.sln")) -and
            (Test-Path -LiteralPath (Join-Path $current.FullName "README.md"))) {
            return $current.FullName
        }
        $current = $current.Parent
    }
    throw "Cannot locate repository root."
}

function Get-MSBuildPath {
    $candidates = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installPath) {
            $candidates += Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
            $candidates += Join-Path $installPath "MSBuild\15.0\Bin\MSBuild.exe"
        }
    }

    $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2017\BuildTools\MSBuild\15.0\Bin\MSBuild.exe"

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or add MSBuild to PATH."
}

function Get-AssemblyVersion {
    param([string]$AssemblyInfoPath)

    $content = Get-Content -LiteralPath $AssemblyInfoPath -Raw -Encoding UTF8
    if ($content -match 'AssemblyFileVersion\("([^"]+)"\)') {
        return $Matches[1]
    }
    if ($content -match 'AssemblyVersion\("([^"]+)"\)') {
        return $Matches[1]
    }
    return "0.0.0.0"
}

function Copy-ReleaseFiles {
    param(
        [string]$SourceDirectory,
        [string]$TargetDirectory
    )

    if (Test-Path -LiteralPath $TargetDirectory) {
        Remove-Item -LiteralPath $TargetDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $TargetDirectory | Out-Null

    $items = Get-ChildItem -LiteralPath $SourceDirectory -Force | Where-Object {
        $_.Name -notmatch '\.pdb$' -and
        $_.Name -notmatch '\.xml$' -and
        $_.Name -ne "CodexVerify"
    }

    foreach ($item in $items) {
        Copy-Item -LiteralPath $item.FullName -Destination $TargetDirectory -Recurse -Force
    }

    $exe = Join-Path $TargetDirectory "mySQLPunk.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "mySQLPunk.exe was not found in release output: $SourceDirectory"
    }
}

$repoRoot = Get-RepositoryRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AssemblyVersion -AssemblyInfoPath (Join-Path $repoRoot "mySQLPunk\Properties\AssemblyInfo.cs")
}

$solution = Join-Path $repoRoot "mySQLPunk.sln"
$projectOutput = Join-Path $repoRoot "mySQLPunk\bin\$Configuration"
$packageName = "mySQLPunk-$Version-win-x64-portable"
$packageDirectory = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$manifestPath = Join-Path $OutputRoot "release-manifest.json"

if (-not $SkipBuild) {
    $msbuild = Get-MSBuildPath
    Write-Host "Building $Configuration with MSBuild: $msbuild"
    & $msbuild $solution /restore /p:Configuration=$Configuration /p:Platform="Any CPU"
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $projectOutput)) {
    throw "Release output directory was not found: $projectOutput"
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Copy-ReleaseFiles -SourceDirectory $projectOutput -TargetDirectory $packageDirectory

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $zipPath -Force

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$zipItem = Get-Item -LiteralPath $zipPath
$manifest = [ordered]@{
    app = "mySQLPunk"
    version = $Version
    package = $zipItem.Name
    sha256 = $hash.Hash
    sizeBytes = $zipItem.Length
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    releaseAssetHint = "Upload this zip and installer assets to GitHub Releases."
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Package directory: $packageDirectory"
Write-Host "Package zip:       $zipPath"
Write-Host "Manifest:          $manifestPath"
