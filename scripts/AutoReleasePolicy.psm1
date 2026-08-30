Set-StrictMode -Version Latest

function Get-AutoReleaseCommitImpact {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$Message
    )

    $normalized = $(if ($null -eq $Message) { "" } else { $Message }) -replace "`r", ""
    $header = ($normalized -split "`n", 2)[0].Trim()
    $type = "other"
    $score = 0

    if ($header -match '^(?<type>feat|fix|perf|refactor|revert|docs|test|style|chore)(?:\([^)]+\))?(?<breaking>!)?:') {
        $type = $Matches['type'].ToLowerInvariant()
        switch ($type) {
            'feat' { $score = 2 }
            'fix' { $score = 1 }
            'perf' { $score = 1 }
            'refactor' { $score = 1 }
            'revert' { $score = 1 }
            default { $score = 0 }
        }
    }

    $breaking = $header -match '^(?:feat|fix|perf|refactor|revert|docs|test|style|chore)(?:\([^)]+\))?!:' `
        -or $normalized -match '(?mi)^BREAKING[ -]CHANGE:\s*\S'
    $releaseNow = $breaking -or $normalized -match '(?mi)^Release-Now:\s*(?:true|yes|1)\s*$'
    if ($releaseNow -and $score -eq 0) {
        $score = 1
    }

    [pscustomobject]@{
        Header = $header
        Type = $type
        Score = $score
        ReleaseNow = [bool]$releaseNow
        Breaking = [bool]$breaking
    }
}

function Get-NextAutoReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LatestTag
    )

    $raw = $LatestTag.Trim()
    if ($raw.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $raw = $raw.Substring(1)
    }
    if ($raw -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Latest release tag must be vN.N.N or vN.N.N.N. Received: $LatestTag"
    }

    $version = [version]$raw
    if ($version.Revision -ge 0) {
        return '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, $version.Build, ($version.Revision + 1)
    }
    return '{0}.{1}.{2}' -f $version.Major, $version.Minor, ($version.Build + 1)
}

function Get-AutoReleaseDecision {
    [CmdletBinding()]
    param(
        [string[]]$CommitMessages = @(),

        [Parameter(Mandatory = $true)]
        [string]$LatestTag,

        [switch]$Force
    )

    $details = @($CommitMessages | ForEach-Object { Get-AutoReleaseCommitImpact -Message $_ })
    $impactCommits = @($details | Where-Object { $_.Score -gt 0 })
    $immediateCommits = @($details | Where-Object { $_.ReleaseNow })
    $score = 0
    foreach ($detail in $details) {
        $score += [int]$detail.Score
    }
    $shouldRelease = $false
    $reason = '沒有需要發版的程式變更。'
    if ($CommitMessages.Count -eq 0) {
        $reason = '最新 tag 之後沒有新 commit。'
    } elseif ($Force) {
        $shouldRelease = $true
        $reason = '已由 workflow_dispatch 強制發版。'
    } elseif ($immediateCommits.Count -gt 0) {
        $shouldRelease = $true
        $reason = '偵測到 Release-Now 或 BREAKING CHANGE，立即發版。'
    } elseif ($impactCommits.Count -gt 0) {
        $reason = "偵測到 $($impactCommits.Count) 筆程式變更；未標記重大里程碑，繼續累積但不自動發版。"
    }

    [pscustomobject]@{
        ShouldRelease = [bool]$shouldRelease
        Reason = $reason
        LatestTag = $LatestTag
        NextVersion = Get-NextAutoReleaseVersion -LatestTag $LatestTag
        CommitCount = $CommitMessages.Count
        ImpactCommitCount = $impactCommits.Count
        ImmediateCommitCount = $immediateCommits.Count
        Score = $score
        Details = $details
    }
}

Export-ModuleMember -Function Get-AutoReleaseCommitImpact, Get-NextAutoReleaseVersion, Get-AutoReleaseDecision
