[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$LatestTag,

    [string]$RepositoryRoot,

    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
Import-Module (Join-Path $PSScriptRoot 'AutoReleasePolicy.psm1') -Force

$expectedVersion = Get-NextAutoReleaseVersion -LatestTag $LatestTag
if ($Version -ne $expectedVersion) {
    throw "Version $Version is not the next version after $LatestTag. Expected $expectedVersion."
}

$currentVersion = $LatestTag.Trim()
if ($currentVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $currentVersion = $currentVersion.Substring(1)
}

$assemblyInfoPath = Join-Path $RepositoryRoot 'mySQLPunk\Properties\AssemblyInfo.cs'
$readmePath = Join-Path $RepositoryRoot 'README.md'
$changelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md'
foreach ($path in @($assemblyInfoPath, $readmePath, $changelogPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file was not found: $path"
    }
}

$assemblyInfo = Get-Content -LiteralPath $assemblyInfoPath -Raw -Encoding UTF8
if ($assemblyInfo -notmatch 'AssemblyFileVersion\("([^"]+)"\)') {
    throw 'AssemblyFileVersion was not found.'
}
if ([version]$Matches[1] -ne [version]$currentVersion) {
    throw "AssemblyFileVersion $($Matches[1]) does not match latest tag $LatestTag."
}

$changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
$escapedVersion = [regex]::Escape($Version)
$section = [regex]::Match($changelog, "(?ms)^## \[(?:v)?$escapedVersion\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
if (-not $section.Success) {
    throw "CHANGELOG.md does not contain the next release section $Version."
}
if ($section.Groups[1].Value -notmatch '(?m)^### 🚀 新增功能\s*$' `
    -or $section.Groups[1].Value -notmatch '(?m)^### 🛠️ 問題修正與優化\s*$' `
    -or $section.Groups[1].Value -notmatch '(?m)^-\s+\*\*[^*]+\*\*') {
    throw "CHANGELOG.md section $Version is missing required release-note content."
}

if ($CheckOnly) {
    Write-Host "Auto-release preparation check passed for v$Version."
    return
}

$updatedAssemblyInfo = [regex]::Replace($assemblyInfo, 'AssemblyVersion\("[^"]+"\)', ('AssemblyVersion("{0}")' -f $Version))
$updatedAssemblyInfo = [regex]::Replace($updatedAssemblyInfo, 'AssemblyFileVersion\("[^"]+"\)', ('AssemblyFileVersion("{0}")' -f $Version))
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
$updatedReadme = $readme.Replace($currentVersion, $Version)
if ($updatedReadme -eq $readme) {
    throw "README.md does not contain current version $currentVersion."
}

$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($assemblyInfoPath, $updatedAssemblyInfo, $utf8NoBom)
[IO.File]::WriteAllText($readmePath, $updatedReadme, $utf8NoBom)
Write-Host "Prepared v$Version in AssemblyInfo.cs and README.md."
