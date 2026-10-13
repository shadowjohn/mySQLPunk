# Pure trial guards over caller-supplied snapshots; no SQL, filesystem probes or authorization acquisition.
# Shared by the one-shot runner and offline guard tests.
Set-StrictMode -Version 2.0

function New-TransferGuardDecision {
    param([bool]$Allowed, [string]$Code)
    [pscustomobject]@{ Allowed = $Allowed; Code = $Code }
}

function Test-TransferTrue {
    param($Value)
    return ($Value -is [bool] -and $Value)
}

function Test-TransferTrialInvocation {
    param([bool]$Run, [hashtable]$Request, [hashtable]$File, [hashtable]$Output)
    # The opt-in guard comes before any snapshot requirement. A future runner must likewise perform no source/DB/output I/O here.
    if (!$Run) { return New-TransferGuardDecision $false 'SkippedNoRun' }
    if (!$Request -or !$File -or !$Output) { return New-TransferGuardDecision $false 'MissingDescriptor' }
    if (![string]::Equals($Request.Server, 'localhost', [StringComparison]::OrdinalIgnoreCase)) { return New-TransferGuardDecision $false 'WrongServer' }
    if (![string]::Equals($Request.Database, 'Transfer', [StringComparison]::Ordinal)) { return New-TransferGuardDecision $false 'WrongDatabase' }
    $expectedInput = 'C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql'
    if (![string]::Equals($Request.InputCanonicalPath, $expectedInput, [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($File.CanonicalPath, $expectedInput, [StringComparison]::OrdinalIgnoreCase)) { return New-TransferGuardDecision $false 'WrongInput' }
    if (!(Test-TransferTrue $File.RegularFile) -or !(Test-TransferTrue $File.Readable) -or !(Test-TransferTrue $File.NoReparseChain)) { return New-TransferGuardDecision $false 'UnsafeInput' }
    if ($File.Length -isnot [long] -or $File.Length -ne 4478414151L) { return New-TransferGuardDecision $false 'InputLengthMismatch' }
    # Snapshot producer must resolve final volume and every existing ancestor; drive letter alone is insufficient.
    if (!(Test-TransferTrue $Output.OnLocalCVolume) -or !(Test-TransferTrue $Output.NoReparseChain) -or
        !(Test-TransferTrue $Output.EmptyOrNew) -or !(Test-TransferTrue $Output.Writable)) { return New-TransferGuardDecision $false 'UnsafeOutput' }
    if ([string]::IsNullOrWhiteSpace($Request.OutputCanonicalPath) -or
        ![string]::Equals($Request.OutputCanonicalPath, $Output.CanonicalPath, [StringComparison]::OrdinalIgnoreCase) -or
        !($Output.CanonicalPath -match '^C:\\[^\\]') -or $Output.CanonicalPath -match '(^|[\\/])\.\.([\\/]|$)') { return New-TransferGuardDecision $false 'OutputMismatch' }
    $outputBase = $Output.CanonicalPath.TrimEnd('\')
    if ([string]::Equals($outputBase, 'C:\Users\johnho\Desktop', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($outputBase, $expectedInput, [StringComparison]::OrdinalIgnoreCase)) { return New-TransferGuardDecision $false 'OutputTouchesInput' }
    if ($Output.FreeBytes -isnot [long] -or $Output.RequiredBytes -isnot [long] -or
        $Output.RequiredBytes -ne 68719476736L -or $Output.FreeBytes -lt $Output.RequiredBytes) { return New-TransferGuardDecision $false 'InsufficientSpace' }
    return New-TransferGuardDecision $true 'InvocationAccepted'
}

function Test-TransferTrialTarget {
    param([hashtable]$Target, [hashtable]$Baseline, [long]$ExpectedTables, $ContainsViews)
    if (!$Target) { return New-TransferGuardDecision $false 'TargetUnavailable' }
    if (![string]::Equals($Target.ServerName, 'JOHN\MSSQL2019', [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($Target.DatabaseName, 'Transfer', [StringComparison]::Ordinal)) { return New-TransferGuardDecision $false 'WrongIdentity' }
    if ($Target.DatabaseId -isnot [int] -or $Target.DatabaseId -le 0 -or $Target.DatabaseCreateDate -isnot [datetime]) { return New-TransferGuardDecision $false 'TargetIdentityUnavailable' }
    if (!($Target.Version -is [string] -and $Target.Version -match '^15\.\d+\.\d+\.\d+$') -or $Target.Compatibility -ne 150 -or
        $Target.State -cne 'ONLINE' -or !(Test-TransferTrue $Target.Writable)) { return New-TransferGuardDecision $false 'WrongTargetState' }
    if (!(Test-TransferTrue $Target.WindowsIntegratedJohnho) -or $Target.WindowsSid -cne $Target.OriginalSqlSid -or $Target.WindowsSid -cne $Target.CurrentSqlSid -or $Target.WindowsName -ine $Target.OriginalSqlName -or $Target.WindowsName -ine $Target.CurrentSqlName) { return New-TransferGuardDecision $false 'WrongLogin' }
    # MetadataVisible must come from the accepted Task 3 conservative proof, never a COUNT result alone.
    if (!(Test-TransferTrue $Target.MetadataVisible) -or $Target.UserObjectCount -isnot [long]) { return New-TransferGuardDecision $false 'MetadataUnknown' }
    if ($Target.UserObjectCount -ne 0L) { return New-TransferGuardDecision $false 'TargetNonempty' }
    if ($ExpectedTables -gt 0L -and !(Test-TransferTrue $Target.CanCreateTable)) { return New-TransferGuardDecision $false 'CreateTableDenied' }
    # No blanket CREATE VIEW requirement: gate only when scan has independently known view presence.
    if ((Test-TransferTrue $ContainsViews) -and !(Test-TransferTrue $Target.CanCreateView)) { return New-TransferGuardDecision $false 'CreateViewDenied' }
    if ($Baseline -and ($Target.DatabaseId -ne $Baseline.DatabaseId -or $Target.DatabaseCreateDate.Ticks -ne $Baseline.DatabaseCreateDate.Ticks -or
        ![string]::Equals($Target.ServerName, $Baseline.ServerName, [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($Target.DatabaseName, $Baseline.DatabaseName, [StringComparison]::Ordinal))) { return New-TransferGuardDecision $false 'TargetIdentityChanged' }
    if (!(Test-TransferTrue $Target.SettingsUnchanged)) { return New-TransferGuardDecision $false 'SettingsChanged' }
    return New-TransferGuardDecision $true 'TargetAccepted'
}

function Test-TransferTrialAttemptClaim {
    param([hashtable]$Proposed, $ExistingMarker)
    # Existing marker of ANY terminal/intermediate/corrupt state consumes the original one-attempt authority.
    # Future caller atomically CreateNew+flushes a FIXED C-path marker, independent of OutputDirectory, before SUT.Run.
    # This pure test neither creates nor removes that marker. A null value is insufficient without verified absence.
    if (!$Proposed -or !(Test-TransferTrue $Proposed.StableMarkerAbsenceVerified)) { return New-TransferGuardDecision $false 'AttemptAbsenceUnknown' }
    if ($null -ne $ExistingMarker) { return New-TransferGuardDecision $false 'AttemptAlreadyClaimed' }
    if ($Proposed.Nonce -isnot [string] -or $Proposed.Nonce -cnotmatch '^[0-9a-f]{32}$' -or
        $Proposed.JobId -isnot [guid] -or $Proposed.JobId -eq [guid]::Empty) { return New-TransferGuardDecision $false 'AttemptIdentityInvalid' }
    if (!(Test-TransferTrue $Proposed.GuardsPassed) -or !(Test-TransferTrue $Proposed.PreflightPassed)) { return New-TransferGuardDecision $false 'AttemptNotReady' }
    return New-TransferGuardDecision $true 'AtomicClaimRequired'
}

function ConvertFrom-TransferBoundedJson {
    param([byte[]]$Bytes)
    if(!$Bytes -or $Bytes.Length -gt 1048576){throw 'BoundedJsonSize'}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    $scan=[Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new($text));$scan.MaxDepth=4;$scan.DateParseHandling=[Newtonsoft.Json.DateParseHandling]::None
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try{
        if(!$scan.Read() -or $scan.TokenType -ne [Newtonsoft.Json.JsonToken]::StartObject){throw 'FlatStrictJsonRequired'}
        while($scan.Read()){
            if($scan.TokenType -eq [Newtonsoft.Json.JsonToken]::Comment -or $scan.TokenType -eq [Newtonsoft.Json.JsonToken]::StartObject -or $scan.TokenType -eq [Newtonsoft.Json.JsonToken]::StartArray){throw 'FlatStrictJsonRequired'}
            if($scan.TokenType -eq [Newtonsoft.Json.JsonToken]::PropertyName -and !$names.Add([string]$scan.Value)){throw 'FlatStrictJsonDuplicate'}
        }
    }finally{$scan.Dispose()}
    $settings=[Newtonsoft.Json.Linq.JsonLoadSettings]::new()
    $settings.DuplicatePropertyNameHandling=[Newtonsoft.Json.Linq.DuplicatePropertyNameHandling]::Error
    $reader=[Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new($text))
    $reader.MaxDepth=4;$reader.DateParseHandling=[Newtonsoft.Json.DateParseHandling]::None
    try{
        $object=[Newtonsoft.Json.Linq.JObject]::Load($reader,$settings)
        if($reader.Read()){throw 'BoundedJsonTrailingData'}
        $result=@{}
        foreach($property in $object.Properties()){
            if($result.ContainsKey($property.Name) -or $property.Value -isnot [Newtonsoft.Json.Linq.JValue]){throw 'FlatStrictJsonRequired'}
            $result[$property.Name]=$property.Value.Value
        }
        return $result
    }finally{$reader.Dispose()}
}
function Test-TransferTrialRequest {
    param([hashtable]$Request)
    $keys=@('SchemaVersion','Kind','Nonce','JobId','Server','DatabaseName','InputCanonicalPath','OutputCanonicalPath','RequiredFreeBytes','CancelBelowBytes','SourceCommit','SourceSnapshotSha256','ExecutableSha256','ProductExecutableSha256','ProductExecutablePath','RegistrationSha256')
    if(!$Request -or $Request.Count -ne $keys.Count){return New-TransferGuardDecision $false 'RequestShape'}
    foreach($key in $Request.Keys){if($key -cnotin $keys){return New-TransferGuardDecision $false 'RequestShape'}}
    foreach($key in $keys){if(!$Request.ContainsKey($key)){return New-TransferGuardDecision $false 'RequestShape'}}
    foreach($number in @('SchemaVersion','RequiredFreeBytes','CancelBelowBytes')){if($Request[$number] -isnot [long]){return New-TransferGuardDecision $false 'RequestType'}}
    if($Request.SchemaVersion -ne 2 -or $Request.Kind -cne 'Request' -or $Request.Server -cne 'localhost' -or $Request.DatabaseName -cne 'Transfer' -or
        $Request.InputCanonicalPath -cne 'C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql' -or $Request.RequiredFreeBytes -ne 68719476736L -or $Request.CancelBelowBytes -ne 21474836480L -or $Request.ProductExecutablePath -cne 'C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\mySQLPunk\bin\CodexVerify\mySQLPunk.exe'){return New-TransferGuardDecision $false 'RequestPolicy'}
    if($Request.Nonce -isnot [string] -or $Request.Nonce -cnotmatch '^[a-f0-9]{32}$' -or $Request.JobId -isnot [string] -or $Request.JobId -cnotmatch '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$' -or [guid]$Request.JobId -eq [guid]::Empty -or [guid]::ParseExact($Request.Nonce,'N') -eq [guid]::Empty){return New-TransferGuardDecision $false 'RequestIdentity'}
    if($Request.SourceCommit -isnot [string] -or $Request.SourceCommit -cnotmatch '^[a-f0-9]{40}$'){return New-TransferGuardDecision $false 'BuildPinInvalid'}
    foreach($key in @('SourceSnapshotSha256','ExecutableSha256','ProductExecutableSha256','RegistrationSha256')){if($Request[$key] -isnot [string] -or $Request[$key] -cnotmatch '^[a-f0-9]{64}$'){return New-TransferGuardDecision $false 'BuildPinInvalid'}}
    if($Request.OutputCanonicalPath -isnot [string] -or $Request.OutputCanonicalPath -notmatch '^C:\\[^\\]' -or $Request.OutputCanonicalPath -match '(^|[\\/])\.\.([\\/]|$)'){return New-TransferGuardDecision $false 'RequestPath'}
    return New-TransferGuardDecision $true 'RequestAccepted'
}
function Test-TransferTrialCompletionReceipt {
    param([hashtable]$Expected,[hashtable]$Marker,[hashtable]$Receipt,[string]$ActualMarkerSha256)
    if(!$Expected -or !$Receipt -or !$Marker){return New-TransferGuardDecision $false 'ReceiptMissing'}
    foreach($number in @('SchemaVersion','ProcessId','DatabaseId','DatabaseCreateDateTicks')){if($Receipt[$number] -isnot [long] -or $Marker[$number] -isnot [long]){return New-TransferGuardDecision $false 'ReceiptType'}}
    foreach($number in @('FailedBatches','SkippedBytes','TotalErrors','CleanupErrors','ActualTables','ActualRows')){if($Receipt[$number] -isnot [long]){return New-TransferGuardDecision $false 'ReceiptType'}}
    if(!(Test-TransferTrue $Receipt.Executed) -or !(Test-TransferTrue $Receipt.AttemptMarkerDurable) -or $Receipt.SchemaVersion -ne 2 -or $Receipt.Kind -cne 'Final' -or $Marker.Kind -cne 'Attempt' -or $Marker.SchemaVersion -ne 2){return New-TransferGuardDecision $false 'ReceiptNotExecuted'}
    $allowedMarker=@('SchemaVersion','Kind','Nonce','JobId','Server','DatabaseName','InputCanonicalPath','OutputCanonicalPath','RequiredFreeBytes','CancelBelowBytes','SourceCommit','SourceSnapshotSha256','ExecutableSha256','ProductExecutableSha256','ProductExecutablePath','RegistrationSha256','ProcessId','RequestSha256','ServerName','DatabaseId','DatabaseCreateDateTicks','SourceSha256','WindowsSid','WindowsName','StartedUtc')
    $allowedReceipt=$allowedMarker+@('MarkerSha256','Executed','AttemptMarkerDurable','Status','FailedBatches','SkippedBytes','TotalErrors','CleanupErrors','UncertainBatch','ActualTables','ActualRows','FailureCode','SecondaryFailureCode','ObjectVerificationBoundary','FieldHashEqualityVerified','ServiceCompletionValidated','CommandEnded','SessionReleased','TransactionCleanupConfirmed','LogComplete','IndependentVerificationCompleted','IndependentCatalogCaptured','MonitorComplete','LogPath','LastAcknowledgedBatch','SuccessfulBatches','ProcessedBytes','ScanBatchCount','MaxBatchCodeUnits','NativeJobId','EncodingName','SourceReadMode','FinishedUtc','PreRunLogUsedBytes','PreRunLogUsedAvailability','PreRunLogUsedUtc','TerminalLogUsedBytes','TerminalLogUsedAvailability','TerminalLogUsedUtc')
    foreach($key in $Marker.Keys){if($key -cnotin $allowedMarker){return New-TransferGuardDecision $false 'ReceiptShape'}}
    foreach($key in $Receipt.Keys){if($key -cnotin $allowedReceipt){return New-TransferGuardDecision $false 'ReceiptShape'}}
    foreach($key in @('Nonce','JobId','Server','RequiredFreeBytes','CancelBelowBytes','SourceCommit','SourceSnapshotSha256','ExecutableSha256','ProductExecutableSha256','ProductExecutablePath','RegistrationSha256','InputCanonicalPath','OutputCanonicalPath','RequestSha256','ProcessId','WindowsSid','WindowsName')){
        if(!$Receipt.ContainsKey($key) -or !$Marker.ContainsKey($key) -or $Receipt[$key] -cne $Expected[$key] -or $Marker[$key] -cne $Expected[$key]){return New-TransferGuardDecision $false 'ReceiptIdentityMismatch'}
    }
    foreach($key in @('ServerName','DatabaseName','DatabaseId','DatabaseCreateDateTicks','SourceSha256','WindowsSid','WindowsName')){
        if(!$Receipt.ContainsKey($key) -or !$Marker.ContainsKey($key) -or $Receipt[$key] -cne $Marker[$key]){return New-TransferGuardDecision $false 'ReceiptIdentityMismatch'}
    }
    if($Receipt.ServerName -cne 'JOHN\MSSQL2019' -or $Receipt.DatabaseName -cne 'Transfer' -or $Receipt.DatabaseId -isnot [long] -or $Receipt.DatabaseId -le 0L -or $Receipt.DatabaseCreateDateTicks -isnot [long] -or $Receipt.DatabaseCreateDateTicks -le 0L){return New-TransferGuardDecision $false 'ReceiptIdentityMismatch'}
    foreach($key in @('SourceSha256','RequestSha256','MarkerSha256','ExecutableSha256','ProductExecutableSha256','SourceSnapshotSha256','RegistrationSha256')){if($Receipt[$key] -isnot [string] -or $Receipt[$key] -cnotmatch '^[a-f0-9]{64}$'){return New-TransferGuardDecision $false 'ReceiptIdentityMismatch'}}
    if($Receipt.MarkerSha256 -cne $ActualMarkerSha256){return New-TransferGuardDecision $false 'MarkerHashMismatch'}
    if($Receipt.Status -cne 'Completed' -or $Receipt.FailedBatches -ne 0L -or $Receipt.SkippedBytes -ne 0L -or $Receipt.TotalErrors -ne 0L -or $Receipt.CleanupErrors -ne 0L -or $null -ne $Receipt.UncertainBatch -or $Receipt.FailureCode -cne '' -or $Receipt.SecondaryFailureCode -cne ''){return New-TransferGuardDecision $false 'TrialNotSuccessful'}
    foreach($proof in @('ServiceCompletionValidated','CommandEnded','SessionReleased','TransactionCleanupConfirmed','LogComplete','IndependentVerificationCompleted','IndependentCatalogCaptured','MonitorComplete')){
        if(!(Test-TransferTrue $Receipt[$proof])){return New-TransferGuardDecision $false ('ReceiptProofMissing:'+$proof)}
    }
    if($Receipt.ActualTables -isnot [long] -or $Receipt.ActualRows -isnot [long] -or $Receipt.ActualTables -ne 126L -or $Receipt.ActualRows -ne 3247785L){return New-TransferGuardDecision $false 'IndependentCountMismatch'}
    if($Receipt.ObjectVerificationBoundary -cne 'InventoryOnly;SourceDefinitionEqualityUnverified' -or $Receipt.FieldHashEqualityVerified -isnot [bool] -or $Receipt.FieldHashEqualityVerified){return New-TransferGuardDecision $false 'VerificationBoundaryMismatch'}
    foreach($phase in @('PreRun','Terminal')){
        $value=$Receipt[($phase+'LogUsedBytes')];$availability=$Receipt[($phase+'LogUsedAvailability')]
        if(($availability -ceq 'LogUsedAvailable' -and ($value -isnot [long] -or $value -lt 0L)) -or
            ($availability -ceq 'LogUsedUnavailable' -and $null -ne $value) -or $availability -cnotin @('LogUsedAvailable','LogUsedUnavailable') -or
            $Receipt[($phase+'LogUsedUtc')] -isnot [string] -or [string]::IsNullOrWhiteSpace($Receipt[($phase+'LogUsedUtc')])){return New-TransferGuardDecision $false 'UsedLogEvidenceInvalid'}
    }
    return New-TransferGuardDecision $true 'TrialVerifiedWithinReceiptBoundary'
}
function Get-TransferTrialEnvironment {
    param([hashtable]$Inherited,[hashtable]$Trial)
    $copy=@{};foreach($key in $Inherited.Keys){$copy[$key]=$Inherited[$key]}
    foreach($key in @('MYSQLPUNK_MSSQL_IMPORT_INTEGRATION','MYSQLPUNK_MSSQL_IMPORT_AUTH','MYSQLPUNK_MSSQL_IMPORT_PROFILE','MYSQLPUNK_MSSQL_IMPORT_FIXTURES','MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE','MYSQLPUNK_MSSQL_IMPORT_RUN_ID','MYSQLPUNK_MSSQL_IMPORT_WIZARD_PREVIEW','MYSQLPUNK_MSSQL_WIZARD_PREVIEW')){$copy[$key]=$null}
    foreach($key in $Trial.Keys){$copy[$key]=$Trial[$key]}
    return $copy
}
function Test-TransferTrialMonitorHealth {
    param([long]$FreeCBytes,[bool]$IoSucceeded)
    if(!$IoSucceeded){return New-TransferGuardDecision $false 'CancelMonitorIo'}
    if($FreeCBytes -lt 21474836480L){return New-TransferGuardDecision $false 'CancelLowSpace'}
    return New-TransferGuardDecision $true 'MonitorHealthy'
}
function New-TransferTrialMonitorRecord {
    param([guid]$Nonce, [datetime]$Utc, [int]$ProcessId, [long]$Sequence, [long]$ElapsedMilliseconds,
        [Nullable[long]]$ProcessCpuMilliseconds, [Nullable[long]]$PeakPrivateBytes, [Nullable[long]]$PeakWorkingSetBytes,
        [Nullable[long]]$DataAllocatedBytes, [Nullable[long]]$LogAllocatedBytes, [Nullable[long]]$LogUsedBytes,
        [long]$LastAcknowledgedBatch, [string]$Stage, [string]$MetricAvailability)
    # Caller serializes ONE JSONL record directly to a held C-only stream; no SQL text, row payload or connection string accepted.
    if ($Nonce -eq [guid]::Empty -or $ProcessId -le 0 -or $Sequence -lt 0L -or $ElapsedMilliseconds -lt 0L) { throw 'InvalidMonitorIdentity' }
    if ($Stage -cnotin @('Scanning','Executing','Verifying','Stopping','Finished')) { throw 'InvalidMonitorStage' }
    if ($MetricAvailability -cnotin @('Available','DatabaseMetricsUnavailable','ProcessMetricsUnavailable','MetricsUnavailable')) { throw 'InvalidMonitorAvailability' }
    foreach ($metric in @($ProcessCpuMilliseconds,$PeakPrivateBytes,$PeakWorkingSetBytes,$DataAllocatedBytes,$LogAllocatedBytes,$LogUsedBytes)) {
        if ($null -ne $metric -and $metric -lt 0L) { throw 'InvalidNegativeMonitorMetric' }
    }
    $processMissing=($null -eq $ProcessCpuMilliseconds -or $null -eq $PeakPrivateBytes -or $null -eq $PeakWorkingSetBytes)
    $databaseMissing=($null -eq $DataAllocatedBytes -or $null -eq $LogAllocatedBytes -or $null -eq $LogUsedBytes)
    if (($MetricAvailability -ceq 'Available' -and ($processMissing -or $databaseMissing)) -or
        ($MetricAvailability -ceq 'DatabaseMetricsUnavailable' -and $processMissing) -or
        ($MetricAvailability -ceq 'ProcessMetricsUnavailable' -and $databaseMissing)) { throw 'MonitorAvailabilityMismatch' }
    [pscustomobject][ordered]@{ schemaVersion=1; nonce=$Nonce.ToString('N'); utc=$Utc.ToUniversalTime().ToString('o'); processId=$ProcessId;
        sequence=$Sequence; elapsedMilliseconds=$ElapsedMilliseconds; processCpuMilliseconds=$ProcessCpuMilliseconds;
        peakPrivateBytes=$PeakPrivateBytes; peakWorkingSetBytes=$PeakWorkingSetBytes;
        dataAllocatedBytes=$DataAllocatedBytes; logAllocatedBytes=$LogAllocatedBytes; logUsedBytes=$LogUsedBytes;
        lastAcknowledgedBatch=$LastAcknowledgedBatch; stage=$Stage; metricAvailability=$MetricAvailability }
}
function Test-TransferTrialBuildSnapshot {
    param([hashtable]$Pin,[string]$ActualExecutableSha256,[string]$ActualProductExecutableSha256,[string]$ActualSourceSnapshotSha256,[string]$ActualRegistrationSha256,[bool]$RegistryContainsTrial)
    if(!$Pin -or !(Test-TransferTrue $Pin.FinalPinAccepted) -or !(Test-TransferTrue $Pin.TransferTrialRegistered) -or !$RegistryContainsTrial){return New-TransferGuardDecision $false 'BuildPinNotReleased'}
    foreach($entry in @(@('ExecutableSha256',$ActualExecutableSha256),@('ProductExecutableSha256',$ActualProductExecutableSha256),@('SourceSnapshotSha256',$ActualSourceSnapshotSha256),@('RegistrationSha256',$ActualRegistrationSha256))){
        if($Pin[$entry[0]] -isnot [string] -or $Pin[$entry[0]] -cnotmatch '^[a-f0-9]{64}$' -or $Pin[$entry[0]] -cne $entry[1]){return New-TransferGuardDecision $false 'BuildSnapshotChanged'}
    }
    return New-TransferGuardDecision $true 'BuildSnapshotAccepted'
}

function Test-TransferTrialMarkerPresence {
    param([hashtable]$Receipt,[bool]$MarkerExists,[int]$ChildExitCode)
    if($MarkerExists){return New-TransferGuardDecision $true 'TrialMarkerPresent'}
    if($Receipt -and $Receipt.Executed -is [bool] -and !$Receipt.Executed -and
        $Receipt.AttemptMarkerDurable -is [bool] -and !$Receipt.AttemptMarkerDurable -and
        $Receipt.Status -ceq 'NotStarted' -and $ChildExitCode -ne 0 -and $Receipt.FailureCode -is [string] -and
        $Receipt.FailureCode -cmatch '^Trial[A-Za-z0-9:]{1,148}$'){
        return New-TransferGuardDecision $false ('TrialNotStarted:'+$Receipt.FailureCode)
    }
    return New-TransferGuardDecision $false 'TrialAttemptMarkerMissing'
}
