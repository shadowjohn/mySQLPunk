# Offline pure guards: no DB connections, instance startup, build or user SQL-file access.
$ErrorActionPreference='Stop'
if(!('Newtonsoft.Json.Linq.JObject' -as [type])){
    [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '../mySQLPunk/bin/CodexVerify/Newtonsoft.Json.dll'))|Out-Null
}
. (Join-Path $PSScriptRoot 'MssqlImportTransfer.Guards.ps1')
# PREPARATION ONLY. Pure assertions may be invoked under the explicit preparation authority; no SQL/filesystem probes or mutation.
function Invoke-TransferTrialDraftAssertions {
    $script:TransferDraftAssertionCount=0
    function Assert-Decision([string]$ExpectedCode, $Actual) {
        $script:TransferDraftAssertionCount++
        if ($Actual.Code -cne $ExpectedCode) { throw ('Expected ' + $ExpectedCode + '; got ' + $Actual.Code) }
    }
    $request = @{Server='localhost';Database='Transfer';InputCanonicalPath='C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql';OutputCanonicalPath='C:\mysqlpunk-trial\fresh'}
    $file = @{CanonicalPath=$request.InputCanonicalPath;RegularFile=$true;Readable=$true;NoReparseChain=$true;Length=4478414151L}
    $output = @{CanonicalPath=$request.OutputCanonicalPath;OnLocalCVolume=$true;NoReparseChain=$true;EmptyOrNew=$true;Writable=$true;FreeBytes=100000000000L;RequiredBytes=68719476736L}
    Assert-Decision 'SkippedNoRun' (Test-TransferTrialInvocation $false $null $null $null)
    Assert-Decision 'InvocationAccepted' (Test-TransferTrialInvocation $true $request $file $output)
    foreach ($change in @(@('Server','(localdb)\fixture','WrongServer'),@('Database','master','WrongDatabase'),@('Database','transfer','WrongDatabase'),@('InputCanonicalPath','C:\other.sql','WrongInput'),@('OutputCanonicalPath','D:\trial','OutputMismatch'))) {
        $copy=$request.Clone();$copy[$change[0]]=$change[1]
        Assert-Decision $change[2] (Test-TransferTrialInvocation $true $copy $file $output)
    }
    foreach ($field in @('RegularFile','Readable','NoReparseChain')) { $copy=$file.Clone();$copy[$field]=$false; Assert-Decision 'UnsafeInput' (Test-TransferTrialInvocation $true $request $copy $output) }
    foreach ($field in @('OnLocalCVolume','NoReparseChain','EmptyOrNew','Writable')) { $copy=$output.Clone();$copy[$field]=$false; Assert-Decision 'UnsafeOutput' (Test-TransferTrialInvocation $true $request $file $copy) }
    $copy=$output.Clone();$copy.FreeBytes=68719476735L; Assert-Decision 'InsufficientSpace' (Test-TransferTrialInvocation $true $request $file $copy)
    $copy=$file.Clone();$copy.Length=4478414150L; Assert-Decision 'InputLengthMismatch' (Test-TransferTrialInvocation $true $request $copy $output)
    $copy=$file.Clone();$copy.Length='4478414151'; Assert-Decision 'InputLengthMismatch' (Test-TransferTrialInvocation $true $request $copy $output)

    $target=@{ServerName='JOHN\MSSQL2019';DatabaseName='Transfer';DatabaseId=7;DatabaseCreateDate=[datetime]'2026-10-13T01:00:00';Version='15.0.2000.5';Compatibility=150;State='ONLINE';Writable=$true;WindowsIntegratedJohnho=$true;WindowsSid="aabb";OriginalSqlSid="aabb";CurrentSqlSid="aabb";WindowsName="JOHN\johnho";OriginalSqlName="JOHN\johnho";CurrentSqlName="JOHN\johnho";MetadataVisible=$true;UserObjectCount=0L;CanCreateTable=$true;CanCreateView=$false;SettingsUnchanged=$true}
    $baseline=$target.Clone()
    Assert-Decision 'TargetAccepted' (Test-TransferTrialTarget $target $baseline 126L $null)
    Assert-Decision 'CreateViewDenied' (Test-TransferTrialTarget $target $baseline 126L $true)
    foreach ($change in @(@('ServerName','OTHER\MSSQL2019','WrongIdentity'),@('DatabaseName','master','WrongIdentity'),@('UserObjectCount',1L,'TargetNonempty'),@('UserObjectCount',$null,'MetadataUnknown'),@('MetadataVisible',$false,'MetadataUnknown'),@('CanCreateTable',$null,'CreateTableDenied'),@('DatabaseId',8,'TargetIdentityChanged'),@('DatabaseCreateDate',[datetime]'2026-10-13T01:00:01','TargetIdentityChanged'),@('State','RESTORING','WrongTargetState'),@('Writable',$false,'WrongTargetState'),@('WindowsIntegratedJohnho',$false,'WrongLogin'),@('SettingsUnchanged',$false,'SettingsChanged'))) {
        $copy=$target.Clone();$copy[$change[0]]=$change[1]; Assert-Decision $change[2] (Test-TransferTrialTarget $copy $baseline 126L $null)
    }
    # Metadata proof itself belongs to accepted Task3/Task6 tests. This helper never infers visibility from zero objects.
    $copy=$target.Clone();$copy.MetadataVisible='true'; Assert-Decision 'MetadataUnknown' (Test-TransferTrialTarget $copy $baseline 126L $null)

    $attempt=@{Nonce='11111111111111111111111111111111';JobId=[guid]'22222222-2222-2222-2222-222222222222';StableMarkerAbsenceVerified=$true;GuardsPassed=$true;PreflightPassed=$true}
    Assert-Decision 'AtomicClaimRequired' (Test-TransferTrialAttemptClaim $attempt $null)
    foreach ($existing in @('unparseable',@{status='Started'},@{status='Failed'},@{status='Completed'},@{output='another-output'})) {
        Assert-Decision 'AttemptAlreadyClaimed' (Test-TransferTrialAttemptClaim $attempt $existing)
    }
    $copy=$attempt.Clone();$copy.StableMarkerAbsenceVerified=$false; Assert-Decision 'AttemptAbsenceUnknown' (Test-TransferTrialAttemptClaim $copy $null)
    $copy=$attempt.Clone();$copy.PreflightPassed=$false; Assert-Decision 'AttemptNotReady' (Test-TransferTrialAttemptClaim $copy $null)
    # Atomic create/flush/race/crash tests are future runner integration tests using a small isolated C temp path, NEVER Transfer.

    $contract=@{SchemaVersion=2L;Kind='Request';Nonce='11111111111111111111111111111111';JobId='22222222-2222-2222-2222-222222222222';Server='localhost';DatabaseName='Transfer';InputCanonicalPath=$request.InputCanonicalPath;OutputCanonicalPath=$request.OutputCanonicalPath;RequiredFreeBytes=68719476736L;CancelBelowBytes=21474836480L;SourceCommit=('a'*40);SourceSnapshotSha256=('b'*64);ExecutableSha256=('c'*64);ProductExecutableSha256=('9'*64);ProductExecutablePath='C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\mySQLPunk\bin\CodexVerify\mySQLPunk.exe';RegistrationSha256=('d'*64)}
    Assert-Decision 'RequestAccepted' (Test-TransferTrialRequest $contract)
    foreach($change in @(@('SchemaVersion',1L,'RequestPolicy'),@('RequiredFreeBytes',21474836480L,'RequestPolicy'),@('DatabaseName','master','RequestPolicy'),@('ExecutableSha256','bad','BuildPinInvalid'),@('ProductExecutableSha256','bad','BuildPinInvalid'),@('ProductExecutablePath','C:\wrong.exe','RequestPolicy'),@('Nonce','bad','RequestIdentity'))){$copy=$contract.Clone();$copy[$change[0]]=$change[1];Assert-Decision $change[2] (Test-TransferTrialRequest $copy)}
    $copy=$contract.Clone();$copy.Extra='reject';Assert-Decision 'RequestShape' (Test-TransferTrialRequest $copy)
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($contract|ConvertTo-Json -Compress))
    Assert-Decision 'RequestAccepted' (Test-TransferTrialRequest (ConvertFrom-TransferBoundedJson $bytes))
    foreach($bad in @('{/*comment*/"A":1}','{"SchemaVersion":2,"SchemaVersion":2}','{"A":{"Nested":1}}','{"A":1} {"B":2}','{"A":1,"a":2}')){
        $failed=$false;try{$null=ConvertFrom-TransferBoundedJson ([Text.Encoding]::UTF8.GetBytes($bad))}catch{$failed=$true};if(!$failed){throw 'Malformed JSON accepted'}
    }
    $failed=$false;try{$null=ConvertFrom-TransferBoundedJson ([byte[]]@(255))}catch{$failed=$true};if(!$failed){throw 'Invalid UTF8 accepted'}
    $failed=$false;try{$null=ConvertFrom-TransferBoundedJson ([byte[]]::new(1048577))}catch{$failed=$true};if(!$failed){throw 'Oversize JSON accepted'}
    $expected=$contract.Clone();$expected.ProcessId=1234L;$expected.WindowsSid='aabb';$expected.WindowsName='JOHN\johnho';$expected.RequestSha256=('e'*64)
    $marker=$expected.Clone();$marker.Kind='Attempt';$marker.ServerName='JOHN\MSSQL2019';$marker.DatabaseId=7L;$marker.DatabaseCreateDateTicks=123456789L;$marker.SourceSha256=('f'*64);$marker.WindowsSid='aabb';$marker.WindowsName='JOHN\johnho'
    $receipt=$marker.Clone();$receipt.Kind='Final';$receipt.MarkerSha256=('1'*64);$receipt.Executed=$true;$receipt.AttemptMarkerDurable=$true;$receipt.Status='Completed';$receipt.FailedBatches=0L;$receipt.SkippedBytes=0L;$receipt.TotalErrors=0L;$receipt.CleanupErrors=0L;$receipt.UncertainBatch=$null;$receipt.ActualTables=126L;$receipt.ActualRows=3247785L;$receipt.FailureCode='';$receipt.SecondaryFailureCode='';$receipt.ObjectVerificationBoundary='InventoryOnly;SourceDefinitionEqualityUnverified';$receipt.FieldHashEqualityVerified=$false
    $receipt.PreRunLogUsedBytes=123456L;$receipt.PreRunLogUsedAvailability='LogUsedAvailable';$receipt.PreRunLogUsedUtc='2026-10-14T00:00:00Z';$receipt.TerminalLogUsedBytes=$null;$receipt.TerminalLogUsedAvailability='LogUsedUnavailable';$receipt.TerminalLogUsedUtc='2026-10-14T00:00:01Z'
    $proofs=@('ServiceCompletionValidated','CommandEnded','SessionReleased','TransactionCleanupConfirmed','LogComplete','IndependentVerificationCompleted','IndependentCatalogCaptured','MonitorComplete')
    foreach($proof in $proofs){$receipt[$proof]=$true}
    Assert-Decision 'TrialVerifiedWithinReceiptBoundary' (Test-TransferTrialCompletionReceipt $expected $marker $receipt ('1'*64))
    Assert-Decision 'ReceiptMissing' (Test-TransferTrialCompletionReceipt $expected $null $receipt ('1'*64))
    foreach($change in @(@('Nonce','bad','ReceiptIdentityMismatch'),@('ProcessId',4321L,'ReceiptIdentityMismatch'),@('ExecutableSha256',('2'*64),'ReceiptIdentityMismatch'),@('ProductExecutableSha256',('2'*64),'ReceiptIdentityMismatch'),@('ProductExecutablePath','C:\wrong.exe','ReceiptIdentityMismatch'),@('RequestSha256',('3'*64),'ReceiptIdentityMismatch'),@('DatabaseCreateDateTicks',1L,'ReceiptIdentityMismatch'),@('MarkerSha256',('2'*64),'MarkerHashMismatch'),@('Status','Skipped','TrialNotSuccessful'),@('Status','CompletedWithErrors','TrialNotSuccessful'),@('CleanupErrors',1L,'TrialNotSuccessful'),@('UncertainBatch',1L,'TrialNotSuccessful'),@('ActualRows',3247784L,'IndependentCountMismatch'),@('FieldHashEqualityVerified',$true,'VerificationBoundaryMismatch'))){$copy=$receipt.Clone();$copy[$change[0]]=$change[1];Assert-Decision $change[2] (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))}
    foreach($proof in $proofs){foreach($badBool in @($false,'true')){$copy=$receipt.Clone();$copy[$proof]=$badBool;Assert-Decision ('ReceiptProofMissing:'+$proof) (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))}}
    $inherited=@{MYSQLPUNK_MSSQL_IMPORT_INTEGRATION='1';MYSQLPUNK_MSSQL_IMPORT_PROFILE='All';UNCHANGED='yes'}
    $child=Get-TransferTrialEnvironment $inherited @{MYSQLPUNK_SMOKE_FILTER='MSSQL import Transfer trial'}
    if($child.MYSQLPUNK_MSSQL_IMPORT_INTEGRATION -ne $null -or $child.MYSQLPUNK_MSSQL_IMPORT_PROFILE -ne $null -or $child.UNCHANGED -ne 'yes' -or $inherited.MYSQLPUNK_MSSQL_IMPORT_INTEGRATION -ne '1'){throw 'Environment isolation mutated inheritance'}
    Assert-Decision 'MonitorHealthy' (Test-TransferTrialMonitorHealth 21474836480L $true)
    Assert-Decision 'CancelLowSpace' (Test-TransferTrialMonitorHealth 21474836479L $true)
    Assert-Decision 'CancelMonitorIo' (Test-TransferTrialMonitorHealth 100000000000L $false)
    foreach($key in @('OriginalSqlSid','CurrentSqlSid','OriginalSqlName','CurrentSqlName')){$copy=$target.Clone();$copy[$key]='other';Assert-Decision 'WrongLogin' (Test-TransferTrialTarget $copy $baseline 126L $null)}
    $copy=$contract.Clone();$copy.RequiredFreeBytes='68719476736';Assert-Decision 'RequestType' (Test-TransferTrialRequest $copy)
    $copy=$contract.Clone();$copy.Nonce=('0'*32);Assert-Decision 'RequestIdentity' (Test-TransferTrialRequest $copy)
    $copy=$receipt.Clone();$copy.TotalErrors='0';Assert-Decision 'ReceiptType' (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))
    $copy=$receipt.Clone();$copy.Unexpected='x';Assert-Decision 'ReceiptShape' (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))
    $pin=@{FinalPinAccepted=$true;TransferTrialRegistered=$true;ExecutableSha256=('a'*64);ProductExecutableSha256=('9'*64);SourceSnapshotSha256=('b'*64);RegistrationSha256=('c'*64)}
    Assert-Decision 'BuildSnapshotAccepted' (Test-TransferTrialBuildSnapshot $pin ('a'*64) ('9'*64) ('b'*64) ('c'*64) $true)
    Assert-Decision 'BuildPinNotReleased' (Test-TransferTrialBuildSnapshot $pin ('a'*64) ('9'*64) ('b'*64) ('c'*64) $false)
    $copy=$pin.Clone();$copy.FinalPinAccepted='true';Assert-Decision 'BuildPinNotReleased' (Test-TransferTrialBuildSnapshot $copy ('a'*64) ('9'*64) ('b'*64) ('c'*64) $true)
    foreach($field in @('ExecutableSha256','ProductExecutableSha256','SourceSnapshotSha256','RegistrationSha256')){$copy=$pin.Clone();$copy[$field]=('d'*64);Assert-Decision 'BuildSnapshotChanged' (Test-TransferTrialBuildSnapshot $copy ('a'*64) ('9'*64) ('b'*64) ('c'*64) $true)}
    $copy=$receipt.Clone();$copy.TerminalLogUsedBytes=0L;Assert-Decision 'UsedLogEvidenceInvalid' (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))
    $copy=$receipt.Clone();$copy.PreRunLogUsedBytes=$null;Assert-Decision 'UsedLogEvidenceInvalid' (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))
    $copy=$receipt.Clone();$copy.TerminalLogUsedBytes=0L;$copy.TerminalLogUsedAvailability='LogUsedAvailable';Assert-Decision 'TrialVerifiedWithinReceiptBoundary' (Test-TransferTrialCompletionReceipt $expected $marker $copy ('1'*64))
    Write-Output ('Decision assertions: '+$script:TransferDraftAssertionCount+'; strict JSON exception cases also passed.')
    return 'PURE GUARDS PASS: invocation, 64GiB, target, exact SID/name, attempt, bounded strict JSON, unified marker/receipt, opt-in isolation, 20GiB/IO cancellation. No SQL or filesystem mutation.'
}

Invoke-TransferTrialDraftAssertions
