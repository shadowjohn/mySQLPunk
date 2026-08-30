[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Import-Module (Join-Path $repoRoot 'scripts\AutoReleasePolicy.psm1') -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

$singleFix = Get-AutoReleaseDecision `
    -CommitMessages @('fix(ai): 修正一個小問題') `
    -LatestTag 'v1.0.0.15'
Assert-True (-not $singleFix.ShouldRelease) 'A single small fix should stay in the batch.'
Assert-Equal 1 $singleFix.Score 'A fix should count as one point.'

$docsOnly = Get-AutoReleaseDecision `
    -CommitMessages @('docs(readme): 補上操作說明', 'test(release): 增加測試') `
    -LatestTag 'v1.0.0.15'
Assert-True (-not $docsOnly.ShouldRelease) 'Documentation and tests should not publish a product release by themselves.'
Assert-Equal 0 $docsOnly.Score 'Documentation and tests should not add release points.'

$batched = Get-AutoReleaseDecision `
    -CommitMessages @(
        'feat(data): 新增資料分析',
        'feat(query): 新增視覺化執行計畫',
        'fix(ai): 修正模型切換'
    ) `
    -LatestTag 'v1.0.0.15'
Assert-True (-not $batched.ShouldRelease) 'Accumulated small changes must not publish without an explicit milestone.'
Assert-Equal 5 $batched.Score 'Two features and one fix should total five points.'

$largeUpdate = Get-AutoReleaseDecision `
    -CommitMessages @("feat(model): 新增完整模型設計器`n`nRelease-Now: true") `
    -LatestTag 'v1.0.0.15'
Assert-True $largeUpdate.ShouldRelease 'Release-Now should allow one large update to publish immediately.'
Assert-Equal 1 $largeUpdate.ImmediateCommitCount 'Release-Now should be counted as one immediate commit.'

$breaking = Get-AutoReleaseDecision `
    -CommitMessages @('feat(connection)!: 調整連線設定格式') `
    -LatestTag 'v1.0.0.15'
Assert-True $breaking.ShouldRelease 'A breaking Conventional Commit should publish immediately.'

$forced = Get-AutoReleaseDecision `
    -CommitMessages @('fix(grid): 修正欄位顯示') `
    -LatestTag 'v1.0.0.15' `
    -Force
Assert-True $forced.ShouldRelease 'Manual force should remain available for an approved release.'

Assert-Equal '1.0.0.16' (Get-NextAutoReleaseVersion -LatestTag 'v1.0.0.15') 'Four-part versions should increment the revision.'
Assert-Equal '1.2.4' (Get-NextAutoReleaseVersion -LatestTag 'v1.2.3') 'Three-part versions should increment the patch.'

$prepareRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mysqlpunk-auto-release-policy-" + [Guid]::NewGuid().ToString('N'))
$propertiesRoot = Join-Path $prepareRoot 'mySQLPunk\Properties'
New-Item -ItemType Directory -Path $propertiesRoot -Force | Out-Null
try {
    '[assembly: System.Reflection.AssemblyFileVersion("1.0.0.15")]' |
        Set-Content -LiteralPath (Join-Path $propertiesRoot 'AssemblyInfo.cs') -Encoding UTF8
    '目前發版版本：`v1.0.0.15`' |
        Set-Content -LiteralPath (Join-Path $prepareRoot 'README.md') -Encoding UTF8
    @'
# Changelog

## [Unreleased]

### 🚀 新增功能

- **測試功能**：驗證發版準備流程。

### 🛠️ 問題修正與優化

- 測試修正。
'@ | Set-Content -LiteralPath (Join-Path $prepareRoot 'CHANGELOG.md') -Encoding UTF8

    & (Join-Path $repoRoot 'scripts\Prepare-AutoRelease.ps1') `
        -Version '1.0.0.16' `
        -LatestTag 'v1.0.0.15' `
        -RepositoryRoot $prepareRoot

    $preparedAssembly = Get-Content -LiteralPath (Join-Path $propertiesRoot 'AssemblyInfo.cs') -Raw -Encoding UTF8
    $preparedReadme = Get-Content -LiteralPath (Join-Path $prepareRoot 'README.md') -Raw -Encoding UTF8
    $preparedChangelog = Get-Content -LiteralPath (Join-Path $prepareRoot 'CHANGELOG.md') -Raw -Encoding UTF8
    Assert-True ($preparedAssembly -match 'AssemblyFileVersion\("1\.0\.0\.16"\)') 'Release preparation should update the assembly version.'
    Assert-True ($preparedReadme -match 'v1\.0\.0\.16') 'Release preparation should update the README version.'
    Assert-True ($preparedChangelog -match '(?m)^## \[Unreleased\]\s*$') 'Release preparation should create a fresh Unreleased section.'
    Assert-True ($preparedChangelog -match '(?m)^## \[1\.0\.0\.16\] - \d{4}-\d{2}-\d{2}\s*$') 'Release preparation should promote the accumulated batch to the next version.'
    Assert-True ([regex]::Matches($preparedChangelog, '(?m)^- \*\*測試功能\*\*').Count -eq 1) 'Promoting Unreleased must preserve release-note content exactly once.'
    Assert-True (-not $preparedChangelog.Contains("`r`n")) 'Release preparation should preserve the changelog newline style.'
}
finally {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPrepareRoot = [System.IO.Path]::GetFullPath($prepareRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPrepareRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove test data outside the temporary directory: $resolvedPrepareRoot"
    }
    Remove-Item -LiteralPath $prepareRoot -Recurse -Force
}

Write-Host '[PASS] Auto release policy'
