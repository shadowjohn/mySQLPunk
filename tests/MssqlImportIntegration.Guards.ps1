# Pure runner authorization/receipt/ownership guards; no SQL or filesystem mutations.
# Both runner and guard tests use these exact fail-closed decisions.
Set-StrictMode -Version 2.0

$script:ImportExpectedInstance = 'mySQLPunkExportTest_20261012'
$script:ImportExpectedServer = '(localdb)\mySQLPunkExportTest_20261012'
$script:ImportExpectedRoot = 'C:\Users\johnho\AppData\Local\Temp\mysqlpunk-export-20261012-7cadb673332543c3b17c61924490a702'
$script:ImportExpectedApproval = '2026-10-12 dedicated LocalDB creation and startup approved'

function Get-ImportGuardPathInfo {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path)) { return @{ Exists=$false; IsDirectory=$false; IsReparse=$false; Entries=0 } }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    return @{ Exists=$true; IsDirectory=[bool]$item.PSIsContainer; IsReparse=[bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint); Entries=$(if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1).Count } else { 0 }) }
}
function Assert-ImportAuthorizationEnvelope {
    param(
        [bool]$Run, [object]$Manifest, [string]$Server, [string]$OutputDirectory,
        [long]$FreeCBytes,
        [scriptblock]$PathProbe = ${function:Get-ImportGuardPathInfo}
    )
    # All arguments are observations. This function cannot create files/open SQL/start instances.
    if (!$Run) { return @{ Enabled=$false; Code='OptInRequired' } }
    if ($null -eq $Manifest) { throw 'AuthorizationMissing' }
    if ($Server -cne $script:ImportExpectedServer) { throw 'ServerMismatch' }
    if ($Manifest.Instance -cne $script:ImportExpectedInstance -or $Manifest.CreatedByTask -isnot [bool] -or !$Manifest.CreatedByTask) { throw 'AuthorizationMismatch' }
    if ($Manifest.UserAuthorization -cne $script:ImportExpectedApproval -or $Manifest.EngineVersion -cne '17.0') { throw 'AuthorizationMismatch' }
    if ([string]::IsNullOrWhiteSpace($Manifest.FixtureRoot)) { throw 'FixtureRootMissing' }
    if ($Manifest.FixtureRoot -notmatch '^[cC]:[\\/]') { throw 'FixtureRootNotC' }
    $root = [IO.Path]::GetFullPath($Manifest.FixtureRoot).TrimEnd([char[]]@('\','/'))
    if ($root -ine $script:ImportExpectedRoot) { throw 'FixtureRootMismatch' }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory) -or $OutputDirectory -notmatch '^[cC]:[\\/]') { throw 'OutputMustBeAbsoluteC' }
    $output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([char[]]@('\','/'))
    if (!$output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputMustBeStrictDescendant' }
    $observed = & $PathProbe $output
    if ($observed.Exists -and (!$observed.IsDirectory -or $observed.Entries -ne 0)) { throw 'OutputNotEmptyDirectory' }
    $ancestor = $output
    while ($ancestor) {
        $observed = & $PathProbe $ancestor
        if ($observed.Exists -and $observed.IsReparse) { throw 'ReparseFixturePath' }
        $parent = [IO.Directory]::GetParent($ancestor)
        $ancestor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
    if ($FreeCBytes -lt 20GB) { throw 'InsufficientFixtureSpace' }
    return @{ Enabled=$true; Server=$Server; Root=$root; Output=$output }
}
function Assert-ImportProfileReceipt {
    param([object]$Receipt,[string]$AcceptanceProfile,[string]$RunId,[string[]]$ExpectedCases)
    # Caller separately hashes case receipt files and matches executable SHA/base, output/runId.
    if (!$Receipt -or $Receipt.schemaVersion -ne 1 -or $Receipt.profile -cne $AcceptanceProfile -or $Receipt.runId -cne $RunId -or $Receipt.complete -cne $true -or $Receipt.processBits -ne 64) { throw 'ProfileReceiptIdentityMismatch' }
    $actual = @($Receipt.cases | ForEach-Object { [string]$_.caseId })
    if ($actual.Count -ne $ExpectedCases.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count) { throw 'ProfileReceiptCaseCountMismatch' }
    if (@(Compare-Object ($ExpectedCases | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'ProfileReceiptCaseSetMismatch' }
    foreach ($case in $Receipt.cases) {
        if ($case.status -cne 'Passed' -or $case.executed -cne $true -or $case.assertionsPassed -lt 1 -or $case.assertionsFailed -ne 0 -or $case.receiptSha256 -notmatch '^[a-f0-9]{64}$') { throw ('CaseNotAccepted:'+ $case.caseId) }
    }
    if ($Receipt.cleanupStatus -cne 'Confirmed') { throw 'ProfileCleanupNotConfirmed' }
}

# File-reading caller (future runner): explicit AuthorizationPath must exist and be a file;
# parse JSON with -ErrorAction Stop, pass only the fixed instance/root/approval facts above.
# Only AFTER this helper passes: inspect exact LocalDB running state, create output/token,
# launch fixed filter, verify receipts and hash identities, restore all env names in finally.
# Never authorize from receipt contents, start SQL, sweep prefixes, or use saved settings.
function Assert-ImportCleanupObservation {
    param(
        [guid]$RunGuid, [string]$DatabaseName, [string[]]$CreatedDatabases,
        [string]$RunOutput, [string]$InMemoryOwner, [string]$FileOwner, [object]$DatabaseOwner,
        [string[]]$PhysicalFiles, [string[]]$ExpectedFiles,
        [scriptblock]$PathProbe = ${function:Get-ImportGuardPathInfo}
    )
    # Pure observation only. Actual fixture reads token/property/files immediately before
    # calling this; only the successful caller may then issue exact generated DB DROP.
    # Never emit owner values, even on failed checks.
    if ($RunGuid -eq [guid]::Empty) { throw 'CleanupRunIdentityMissing' }
    $prefix='mysqlpunk_import_'+$RunGuid.ToString('N')
    if ($DatabaseName -cne $prefix+'_src' -and $DatabaseName -cne $prefix+'_dst') { throw 'CleanupDatabaseNotGenerated' }
    if ($CreatedDatabases -cnotcontains $DatabaseName) { throw 'CleanupDatabaseNotRecorded' }
    if ([string]::IsNullOrEmpty($InMemoryOwner) -or $FileOwner -cne $InMemoryOwner -or $null -eq $DatabaseOwner -or [string]$DatabaseOwner -cne $InMemoryOwner) { throw 'CleanupDualOwnershipMismatch' }
    if (!$PhysicalFiles -or $PhysicalFiles.Count -ne $ExpectedFiles.Count -or @($PhysicalFiles | Select-Object -Unique).Count -ne $PhysicalFiles.Count) { throw 'CleanupFileSetMismatch' }
    $root=[IO.Path]::GetFullPath($RunOutput).TrimEnd([char[]]@([char]92,[char]47))
    foreach ($path in $PhysicalFiles) {
        if ($path -notmatch '^[cC]:[\\/]') { throw 'CleanupFileNotC' }
        $file=[IO.Path]::GetFullPath($path)
        if (!$file.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $ExpectedFiles -inotcontains $file) { throw 'CleanupFileNotOwned' }
        $ancestor=$file
        while ($ancestor) {
            $observed = & $PathProbe $ancestor
            if ($observed.Exists -and $observed.IsReparse) { throw 'CleanupReparsePath' }
            $parent=[IO.Directory]::GetParent($ancestor)
            $ancestor=if ($null -eq $parent) {$null} else {$parent.FullName}
        }
    }
    return $true
}