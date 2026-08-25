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

        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$LatestReleaseAt,

        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow,

        [ValidateRange(1, 100)]
        [int]$ScoreThreshold = 5,

        [ValidateRange(1, 365)]
        [int]$MaxBatchDays = 7,

        [switch]$Force
    )

    $details = @($CommitMessages | ForEach-Object { Get-AutoReleaseCommitImpact -Message $_ })
    $impactCommits = @($details | Where-Object { $_.Score -gt 0 })
    $immediateCommits = @($details | Where-Object { $_.ReleaseNow })
    $score = 0
    foreach ($detail in $details) {
        $score += [int]$detail.Score
    }
    $ageDays = [Math]::Max(0, [Math]::Floor(($Now.ToUniversalTime() - $LatestReleaseAt.ToUniversalTime()).TotalDays))

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
    } elseif ($score -ge $ScoreThreshold) {
        $shouldRelease = $true
        $reason = "累積分數 $score 已達門檻 $ScoreThreshold。"
    } elseif ($impactCommits.Count -gt 0 -and $ageDays -ge $MaxBatchDays) {
        $shouldRelease = $true
        $reason = "已有程式變更且距上次發版 $ageDays 天，進入定期批次發版。"
    } elseif ($impactCommits.Count -gt 0) {
        $reason = "目前累積分數 $score/$ScoreThreshold，先繼續累積小改動。"
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
        ScoreThreshold = $ScoreThreshold
        AgeDays = $ageDays
        MaxBatchDays = $MaxBatchDays
        Details = $details
    }
}

Export-ModuleMember -Function Get-AutoReleaseCommitImpact, Get-NextAutoReleaseVersion, Get-AutoReleaseDecision
