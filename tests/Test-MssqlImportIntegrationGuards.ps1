# Pure runner guard/receipt fixtures; execution performs no DB/build IO.
# Run directly before enabling live isolated acceptance.
. (Join-Path $PSScriptRoot 'MssqlImportIntegration.Guards.ps1')

function Test-ImportGuardDrafts {
    $root = $script:ImportExpectedRoot
    $empty = { param($path) @{ Exists=$false; IsDirectory=$false; IsReparse=$false; Entries=0 } }
    function New-ApprovedManifest {
        [pscustomobject]@{ Instance=$script:ImportExpectedInstance; CreatedByTask=$true; UserAuthorization=$script:ImportExpectedApproval; EngineVersion='17.0'; FixtureRoot=$script:ImportExpectedRoot }
    }
    function Assert-GuardThrows {
        param([scriptblock]$Action,[string]$Code)
        $caught = $null
        try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
        if ($caught -cne $Code) { throw ('GuardExpected:'+ $Code + ';Actual:'+ $caught) }
    }
    function Invoke-GuardThenContinuation {
        param([hashtable]$Arguments,[scriptblock]$Continuation)
        $resolved = Assert-ImportAuthorizationEnvelope @Arguments
        if ($resolved.Enabled) { & $Continuation $resolved }
    }
    $arguments = @{ Run=$true; Manifest=(New-ApprovedManifest); Server=$script:ImportExpectedServer; OutputDirectory=($root+'\guard-draft'); FreeCBytes=25GB; PathProbe=$empty }
    $script:ImportDraftContinuationCalls = 0
    $continuation = { param($approval) $script:ImportDraftContinuationCalls++ }
    $withoutRun = $arguments.Clone(); $withoutRun.Run=$false
    Invoke-GuardThenContinuation $withoutRun $continuation
    if ($script:ImportDraftContinuationCalls -ne 0) { throw 'NoRunInvokedContinuation' }
    foreach ($case in @(
        @{Change={param($a) $a.Manifest=$null}; Code='AuthorizationMissing'},
        @{Change={param($a) $a.Server='localhost'}; Code='ServerMismatch'},
        @{Change={param($a) $a.Manifest.Instance='MSSQLLocalDB'}; Code='AuthorizationMismatch'},
        @{Change={param($a) $a.Manifest.CreatedByTask='true'}; Code='AuthorizationMismatch'},
        @{Change={param($a) $a.Manifest.UserAuthorization=''}; Code='AuthorizationMismatch'},
        @{Change={param($a) $a.Manifest.EngineVersion='15.0'}; Code='AuthorizationMismatch'},
        @{Change={param($a) $a.Manifest.FixtureRoot='D:\fixture'}; Code='FixtureRootNotC'},
        @{Change={param($a) $a.Manifest.FixtureRoot='C:\other-fixture'}; Code='FixtureRootMismatch'},
        @{Change={param($a) $a.OutputDirectory=$root}; Code='OutputMustBeStrictDescendant'},
        @{Change={param($a) $a.OutputDirectory=$root+'-sibling\x'}; Code='OutputMustBeStrictDescendant'},
        @{Change={param($a) $a.OutputDirectory=$root+'\..\escape'}; Code='OutputMustBeStrictDescendant'},
        @{Change={param($a) $a.OutputDirectory='C:relative'}; Code='OutputMustBeAbsoluteC'},
        @{Change={param($a) $a.OutputDirectory='\\server\share\x'}; Code='OutputMustBeAbsoluteC'},
        @{Change={param($a) $a.FreeCBytes=20GB-1}; Code='InsufficientFixtureSpace'},
        @{Change={param($a) $a.PathProbe={param($path) @{ Exists=$true; IsDirectory=$true; IsReparse=$false; Entries=1 }}}; Code='OutputNotEmptyDirectory'},
        @{Change={param($a) $a.PathProbe={param($path) @{ Exists=$true; IsDirectory=$false; IsReparse=$false; Entries=0 }}}; Code='OutputNotEmptyDirectory'},
        @{Change={param($a) $a.PathProbe={param($path) @{ Exists=$true; IsDirectory=$true; IsReparse=$true; Entries=0 }}}; Code='ReparseFixturePath'}
    )) {
        $candidate=$arguments.Clone(); $candidate.Manifest=New-ApprovedManifest
        & $case.Change $candidate
        Assert-GuardThrows { Invoke-GuardThenContinuation $candidate $continuation } $case.Code
        if ($script:ImportDraftContinuationCalls -ne 0) { throw ('RejectedGuardInvokedContinuation:'+ $case.Code) }
    }
    # Exactly20GiB is accepted; no SQL/file mutation occurs, only the counter continuation.
    $threshold=$arguments.Clone(); $threshold.FreeCBytes=20GB
    Invoke-GuardThenContinuation $threshold $continuation
    if ($script:ImportDraftContinuationCalls -ne 1) { throw 'ValidGuardDidNotReachContinuation' }

    $hash = ('a' * 64)
    function New-Receipt {
        [pscustomobject]@{ schemaVersion=1; profile='Core'; runId='draft'; complete=$true; processBits=64; cleanupStatus='Confirmed'; cases=@(
            [pscustomobject]@{caseId='one';status='Passed';executed=$true;assertionsPassed=1;assertionsFailed=0;receiptSha256=$hash},
            [pscustomobject]@{caseId='two';status='Passed';executed=$true;assertionsPassed=1;assertionsFailed=0;receiptSha256=$hash}
        ) }
    }
    Assert-ImportProfileReceipt (New-Receipt) 'Core' 'draft' @('one','two')
    foreach ($fault in @('skip','missing','extra','duplicate','pending','noassertion','x86','incomplete','wrongrun','cleanup')) {
        $receipt=New-Receipt
        switch ($fault) {
            'skip' {$receipt.cases[0].executed=$false}
            'missing' {$receipt.cases=@($receipt.cases[0])}
            'extra' {$receipt.cases+= [pscustomobject]@{caseId='three'}}
            'duplicate' {$receipt.cases[1].caseId='one'}
            'pending' {$receipt.cases[0].status='Pending'}
            'noassertion' {$receipt.cases[0].assertionsPassed=0}
            'x86' {$receipt.processBits=32}
            'incomplete' {$receipt.complete=$false}
            'wrongrun' {$receipt.runId='other'}
            'cleanup' {$receipt.cleanupStatus='Uncertain'}
        }
        $thrown=$false
        try { Assert-ImportProfileReceipt $receipt 'Core' 'draft' @('one','two') } catch { $thrown=$true }
        if (!$thrown) { throw ('FalseLiveReceiptAccepted:'+ $fault) }
    }
    # Full runner integration must also assert env restoration in finally and exact case
    # receipt SHA/executable/base matches; these pure helpers do not launch processes.
    # Dual ownership cleanup tests use metadata observations, never actual DROP in guards.
}
function Test-ImportCleanupGuardDrafts {
    $run=[guid]'01234567-89ab-cdef-0123-456789abcdef'
    $root=$script:ImportExpectedRoot+'\cleanup-draft'
    $database='mysqlpunk_import_'+$run.ToString('N')+'_src'
    $files=@(($root+'\'+$database+'.mdf'),($root+'\'+$database+'.ldf'))
    $empty={param($path) @{Exists=$false;IsDirectory=$false;IsReparse=$false;Entries=0}}
    $ok=@{RunGuid=$run;DatabaseName=$database;CreatedDatabases=@($database);RunOutput=$root;InMemoryOwner='draft-private-owner';FileOwner='draft-private-owner';DatabaseOwner='draft-private-owner';PhysicalFiles=$files;ExpectedFiles=$files;PathProbe=$empty}
    if (!(Assert-ImportCleanupObservation @ok)) { throw 'OwnedCleanupObservationRejected' }
    foreach($fault in @('wrongdb','unrecorded','filetoken','dbtoken','missingdbtoken','missingfile','unexpectedfile','siblingfile','reparse')) {
        $candidate=$ok.Clone()
        switch($fault) {
            'wrongdb' {$candidate.DatabaseName='Transfer'}
            'unrecorded' {$candidate.CreatedDatabases=@()}
            'filetoken' {$candidate.FileOwner='other'}
            'dbtoken' {$candidate.DatabaseOwner='other'}
            'missingdbtoken' {$candidate.DatabaseOwner=$null}
            'missingfile' {$candidate.PhysicalFiles=@($files[0])}
            'unexpectedfile' {$candidate.PhysicalFiles=@($files[0],($root+'\unknown.ldf'))}
            'siblingfile' {$candidate.PhysicalFiles=@($files[0],($root+'-sibling\x.ldf'))}
            'reparse' {$candidate.PathProbe={param($path) @{Exists=$true;IsDirectory=$true;IsReparse=$true;Entries=0}}}
        }
        $thrown=$false
        try { Assert-ImportCleanupObservation @candidate | Out-Null } catch { $thrown=$true }
        if (!$thrown) { throw ('UnsafeCleanupObservationAccepted:'+ $fault) }
    }
    # All tokens above are invented fixtures; no actual owner.token is read or printed.
    # This tests permission to reach fixture cleanup only; no DROP/DB connection is issued.
}
Test-ImportGuardDrafts
Test-ImportCleanupGuardDrafts
Write-Host 'PASS MSSQL import runner guards and receipts (no DB connection)'
