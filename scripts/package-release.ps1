param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [string]$InnoSetupCompiler = "",
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

function Get-InnoSetupCompiler {
    param([string]$ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    $candidates += Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    $candidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    $candidates += Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or pass -InnoSetupCompiler."
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

function Remove-BlockedReleaseFiles {
    param([string]$TargetDirectory)

    $blockedFiles = @(
        "binary\sqlite3_ext\sqlite3.exe",
        "binary\sqlite3_ext\libreadline8.dll",
        "binary\sqlite3_ext\libtermcap-0.dll"
    )

    foreach ($relativePath in $blockedFiles) {
        $path = Join-Path $TargetDirectory $relativePath
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

function Copy-ReleaseComplianceFiles {
    param(
        [string]$RepositoryRoot,
        [string]$TargetDirectory
    )

    $thirdPartyNotices = Join-Path $RepositoryRoot "THIRD_PARTY_NOTICES.md"
    if (-not (Test-Path -LiteralPath $thirdPartyNotices)) {
        throw "THIRD_PARTY_NOTICES.md was not found: $thirdPartyNotices"
    }
    Copy-Item -LiteralPath $thirdPartyNotices -Destination (Join-Path $TargetDirectory "THIRD_PARTY_NOTICES.md") -Force

    $projectLicense = Join-Path $RepositoryRoot "LICENSE"
    if (-not (Test-Path -LiteralPath $projectLicense)) {
        throw "Project LICENSE was not found: $projectLicense"
    }
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $TargetDirectory "LICENSE.txt") -Force

    $packagesRoot = Join-Path $RepositoryRoot "packages"
    if (-not (Test-Path -LiteralPath $packagesRoot)) {
        throw "NuGet packages directory was not found: $packagesRoot"
    }

    $oracleLicense = Join-Path $packagesRoot "Oracle.ManagedDataAccess.23.26.200\LICENSE.txt"
    if (-not (Test-Path -LiteralPath $oracleLicense)) {
        throw "Oracle.ManagedDataAccess.23.26.200 LICENSE.txt was not found: $oracleLicense"
    }

    $licensesRoot = Join-Path $TargetDirectory "THIRD_PARTY_LICENSES\NuGet"
    New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null

    $licenseNamePattern = '^(LICENSE|LICENCE|NOTICE|THIRD-PARTY-NOTICES|COPYING)'
    Get-ChildItem -LiteralPath $packagesRoot -Directory | ForEach-Object {
        $packageDirectory = $_
        $licenseFiles = Get-ChildItem -LiteralPath $packageDirectory.FullName -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match $licenseNamePattern }

        foreach ($licenseFile in $licenseFiles) {
            $relativePath = $licenseFile.FullName.Substring($packageDirectory.FullName.Length).TrimStart('\', '/')
            $destinationPath = Join-Path (Join-Path $licensesRoot $packageDirectory.Name) $relativePath
            $destinationDirectory = Split-Path -Parent $destinationPath
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $licenseFile.FullName -Destination $destinationPath -Force
        }
    }
}

$repoRoot = Get-RepositoryRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AssemblyVersion -AssemblyInfoPath (Join-Path $repoRoot "mySQLPunk\Properties\AssemblyInfo.cs")
}

$solution = Join-Path $repoRoot "mySQLPunk.sln"
$projectOutput = Join-Path $repoRoot "mySQLPunk\bin\$Configuration"
$installerScript = Join-Path $repoRoot "installer\mySQLPunk.iss"
$stagingRoot = Join-Path $OutputRoot ".release-staging"
$packageDirectory = Join-Path $stagingRoot "mySQLPunk-$Version-win-x64"
$setupPath = Join-Path $OutputRoot "mySQLPunk-$Version-win-x64-setup.exe"

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
if (-not (Test-Path -LiteralPath $installerScript)) {
    throw "Inno Setup script was not found: $installerScript"
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$iscc = Get-InnoSetupCompiler -ExplicitPath $InnoSetupCompiler

try {
    Copy-ReleaseFiles -SourceDirectory $projectOutput -TargetDirectory $packageDirectory
    Remove-BlockedReleaseFiles -TargetDirectory $packageDirectory
    Copy-ReleaseComplianceFiles -RepositoryRoot $repoRoot -TargetDirectory $packageDirectory

    if (Test-Path -LiteralPath $setupPath) {
        Remove-Item -LiteralPath $setupPath -Force
    }

    Write-Host "Building single EXE installer with Inno Setup: $iscc"
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$packageDirectory" "/DOutputDir=$OutputRoot" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Setup EXE was not created: $setupPath"
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStagingRoot.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a staging directory outside the output root: $resolvedStagingRoot"
        }
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

$setupItem = Get-Item -LiteralPath $setupPath
$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
Write-Host "Setup EXE: $($setupItem.FullName)"
Write-Host "Size:      $($setupItem.Length) bytes"
Write-Host "SHA-256:   $($hash.Hash)"
