[CmdletBinding()]
param(
    [string]$RepositoryRoot,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
Import-Module (Join-Path $PSScriptRoot 'AutoReleasePolicy.psm1') -Force

Push-Location $RepositoryRoot
try {
    $latestTag = (& git describe --tags --abbrev=0 --match 'v[0-9]*').Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($latestTag)) {
        throw 'Cannot determine the latest v* release tag.'
    }

    $hashes = @(& git rev-list --reverse "$latestTag..HEAD")
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot list commits after $latestTag."
    }
    $messages = @()
    foreach ($hash in $hashes) {
        if ([string]::IsNullOrWhiteSpace($hash)) { continue }
        $messages += ((& git log -1 --format=%B $hash) -join "`n").Trim()
    }

    $decision = Get-AutoReleaseDecision `
        -CommitMessages $messages `
        -LatestTag $latestTag `
        -Force:$Force

    $headSubject = (& git log -1 --format=%s HEAD).Trim()
    if ($headSubject -match '^chore\(release\):\s*發佈\s+v') {
        $decision.ShouldRelease = $false
        $decision.Reason = 'HEAD 已是自動發版 commit，不重複建立版本。'
    }

    if ($decision.ShouldRelease) {
        $changelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md'
        $changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
        $escapedVersion = [regex]::Escape($decision.NextVersion)
        $hasVersionSection = $changelog -match "(?m)^## \[(?:v)?$escapedVersion\]"
        $unreleased = [regex]::Match($changelog, '(?ms)^## \[Unreleased\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)')
        $hasValidUnreleased = $unreleased.Success `
            -and $unreleased.Groups[1].Value -match '(?m)^### 🚀 新增功能\s*$' `
            -and $unreleased.Groups[1].Value -match '(?m)^### 🛠️ 問題修正與優化\s*$' `
            -and $unreleased.Groups[1].Value -match '(?m)^-\s+\*\*[^*]+\*\*'
        if (-not $hasVersionSection -and -not $hasValidUnreleased) {
            $decision.ShouldRelease = $false
            $decision.Reason = "已達重大里程碑發版條件，但 CHANGELOG.md 的 Unreleased 批次不完整。"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "should_release=$($decision.ShouldRelease.ToString().ToLowerInvariant())" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "reason=$($decision.Reason)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "latest_tag=$($decision.LatestTag)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "next_version=$($decision.NextVersion)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "score=$($decision.Score)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "impact_commit_count=$($decision.ImpactCommitCount)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    }

    $decision | ConvertTo-Json -Depth 5
}
finally {
    Pop-Location
}
