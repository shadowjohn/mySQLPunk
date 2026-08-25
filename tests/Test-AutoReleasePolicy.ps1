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

$now = [DateTimeOffset]'2026-08-25T12:00:00+08:00'
$recent = $now.AddDays(-1)

$singleFix = Get-AutoReleaseDecision `
    -CommitMessages @('fix(ai): 修正一個小問題') `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $recent `
    -Now $now
Assert-True (-not $singleFix.ShouldRelease) 'A single small fix should stay in the batch.'
Assert-Equal 1 $singleFix.Score 'A fix should count as one point.'

$docsOnly = Get-AutoReleaseDecision `
    -CommitMessages @('docs(readme): 補上操作說明', 'test(release): 增加測試') `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $recent `
    -Now $now
Assert-True (-not $docsOnly.ShouldRelease) 'Documentation and tests should not publish a product release by themselves.'
Assert-Equal 0 $docsOnly.Score 'Documentation and tests should not add release points.'

$batched = Get-AutoReleaseDecision `
    -CommitMessages @(
        'feat(data): 新增資料分析',
        'feat(query): 新增視覺化執行計畫',
        'fix(ai): 修正模型切換'
    ) `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $recent `
    -Now $now
Assert-True $batched.ShouldRelease 'Accumulated user-facing changes should publish when the score reaches five.'
Assert-Equal 5 $batched.Score 'Two features and one fix should total five points.'

$largeUpdate = Get-AutoReleaseDecision `
    -CommitMessages @("feat(model): 新增完整模型設計器`n`nRelease-Now: true") `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $recent `
    -Now $now
Assert-True $largeUpdate.ShouldRelease 'Release-Now should allow one large update to publish immediately.'
Assert-Equal 1 $largeUpdate.ImmediateCommitCount 'Release-Now should be counted as one immediate commit.'

$breaking = Get-AutoReleaseDecision `
    -CommitMessages @('feat(connection)!: 調整連線設定格式') `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $recent `
    -Now $now
Assert-True $breaking.ShouldRelease 'A breaking Conventional Commit should publish immediately.'

$aged = Get-AutoReleaseDecision `
    -CommitMessages @('fix(grid): 修正欄位顯示') `
    -LatestTag 'v1.0.0.15' `
    -LatestReleaseAt $now.AddDays(-8) `
    -Now $now
Assert-True $aged.ShouldRelease 'A small user-facing change should publish after the seven-day batch window.'

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

## [1.0.0.16] - 2026-08-25

### 🚀 新增功能

- **測試功能**：驗證發版準備流程。

### 🛠️ 問題修正與優化

- 測試修正。
'@ | Set-Content -LiteralPath (Join-Path $prepareRoot 'CHANGELOG.md') -Encoding UTF8

    & (Join-Path $repoRoot 'scripts\Prepare-AutoRelease.ps1') `
        -Version '1.0.0.16' `
        -LatestTag 'v1.0.0.15' `
        -RepositoryRoot $prepareRoot `
        -CheckOnly
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
