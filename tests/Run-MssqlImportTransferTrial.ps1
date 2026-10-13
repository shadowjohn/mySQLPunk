# One authorized SQL2019 Transfer trial; no automatic build, cleanup or retry.
param([switch]$Run,[string]$Server,[string]$Database,[string]$InputFile,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$Run){Write-Host 'SKIPPED: explicit -Run -Server localhost -Database Transfer -InputFile C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql -OutputDirectory <new empty C-path> required.';exit 0}
$repoRoot='C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import'
$testExe=Join-Path $repoRoot 'mySQLPunk\bin\CodexVerify\mySQLPunk.SmokeTests.exe'
$productExe=Join-Path $repoRoot 'mySQLPunk\bin\CodexVerify\mySQLPunk.exe'
if(!('Newtonsoft.Json.Linq.JObject' -as [type])){[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $testExe) 'Newtonsoft.Json.dll'))|Out-Null}
. (Join-Path $PSScriptRoot 'MssqlImportTransfer.Guards.ps1')
function Read-TrialBoundedBytes([string]$Path){
    $stream=[IO.File]::OpenRead($Path)
    try{
        if($stream.Length -le 0 -or $stream.Length -gt 1048576L){throw 'Bounded artifact size rejected.'}
        $bytes=[byte[]]::new([int]$stream.Length);$read=0
        while($read -lt $bytes.Length){$n=$stream.Read($bytes,$read,$bytes.Length-$read);if($n -eq 0){throw 'Artifact truncated.'};$read+=$n}
        if($stream.ReadByte() -ne -1){throw 'Artifact changed.'}
        return ,$bytes
    }finally{$stream.Dispose()}
}
function Hash-TrialBytes([byte[]]$Bytes){$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose()}}
function Assert-TrialPath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith('C:\',[StringComparison]::OrdinalIgnoreCase) -or ([IO.DriveInfo]::new('C')).DriveType -ne [IO.DriveType]::Fixed){throw 'Local fixed C-only output required.'}
    $current=$full
    while($current){if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse chain rejected.'};$current=[IO.Path]::GetDirectoryName($current)}
    return $full
}
$expectedInput='C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql'
$fixedAttempt='C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\.superpowers\sdd\implementation-plan\ImportTrials\JOHN_MSSQL2019-Transfer-Transfer_dump_20261013_165730.attempt.json'
if($Server -cne 'localhost' -or $Database -cne 'Transfer' -or [string]::IsNullOrWhiteSpace($InputFile) -or [string]::IsNullOrWhiteSpace($OutputDirectory)){throw 'Exact explicit trial arguments required.'}
# This pin cannot be created by the trial. Formal release supplies accepted source/registration manifests after build.
$pinPath=Join-Path $repoRoot '.superpowers/sdd/implementation-plan/task-7-build-pin.json'
$pin=ConvertFrom-TransferBoundedJson (Read-TrialBoundedBytes (Assert-TrialPath $pinPath))
if(!(Test-TransferTrue $pin.FinalPinAccepted) -or !(Test-TransferTrue $pin.TransferTrialRegistered)){throw 'Final source/build/registration pin not released.'}
if(!(Test-Path -LiteralPath $testExe -PathType Leaf) -or (Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.ExecutableSha256){throw 'Accepted executable missing or changed.'}
if($pin.ProductExecutablePath -cne $productExe -or !(Test-Path -LiteralPath $productExe -PathType Leaf) -or (Get-FileHash -LiteralPath (Assert-TrialPath $productExe) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.ProductExecutableSha256){throw 'Accepted actual product executable missing or changed.'}
foreach($manifestName in @('SourceSnapshot','Registration')){
    $manifestPath=Assert-TrialPath $pin[($manifestName+'Path')]
    $bytes=Read-TrialBoundedBytes $manifestPath
    if((Hash-TrialBytes $bytes) -cne $pin[($manifestName+'Sha256')]){throw 'Pinned source/registration manifest changed.'}
    $manifest=ConvertFrom-TransferBoundedJson $bytes
    foreach($name in $manifest.Keys){$null=Assert-TrialPath $name;if((Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest[$name]){throw 'Pinned source/registration file changed.'}}
}
$registryText=[IO.File]::ReadAllText((Join-Path $repoRoot 'tests/SmokeTests.cs'))
$compileText=[IO.File]::ReadAllText((Join-Path $repoRoot 'tests/Run-SmokeTests.ps1'))
$registryContainsTrial=([regex]::Matches($registryText,'Run\("MSSQL import Transfer trial",\s*MssqlImportTransferTrial,\s*ref passed\);').Count -eq 1 -and [regex]::Matches($compileText,'SmokeTests\.MssqlImport\.TransferTrial\.cs').Count -eq 1)
$decision=Test-TransferTrialBuildSnapshot $pin ((Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash.ToLowerInvariant()) ((Get-FileHash -LiteralPath $productExe -Algorithm SHA256).Hash.ToLowerInvariant()) $pin.SourceSnapshotSha256 $pin.RegistrationSha256 $registryContainsTrial
if(!$decision.Allowed){throw $decision.Code}
$inputFull=Assert-TrialPath $InputFile
if(![string]::Equals($inputFull,$expectedInput,[StringComparison]::OrdinalIgnoreCase)){throw 'Unauthorized source path.'}
if(!(Test-Path -LiteralPath $inputFull -PathType Leaf) -or (Get-Item -LiteralPath $inputFull).Length -ne 4478414151L){throw 'Source metadata mismatch.'}
$outputFull=Assert-TrialPath $OutputDirectory
if([string]::Equals($outputFull,$inputFull,[StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($outputFull.TrimEnd('\'),'C:\Users\johnho\Desktop',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must not touch input.'}
if(Test-Path -LiteralPath $outputFull){if(!(Test-Path -LiteralPath $outputFull -PathType Container) -or @(Get-ChildItem -LiteralPath $outputFull -Force).Count -ne 0){throw 'Fresh empty output required.'}}
$null=Assert-TrialPath $fixedAttempt
if(Test-Path -LiteralPath $fixedAttempt){throw 'This authorization attempt was already claimed. Preserve existing evidence; no automatic retry.'}
$requiredFreeBytes=68719476736L
if(([IO.DriveInfo]::new('C')).AvailableFreeSpace -lt $requiredFreeBytes){throw 'Insufficient C capacity for trial.'}
$foreign=@('MYSQLPUNK_MSSQL_IMPORT_INTEGRATION','MYSQLPUNK_MSSQL_IMPORT_AUTH','MYSQLPUNK_MSSQL_IMPORT_PROFILE','MYSQLPUNK_MSSQL_IMPORT_FIXTURES','MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE','MYSQLPUNK_MSSQL_IMPORT_RUN_ID','MYSQLPUNK_MSSQL_IMPORT_WIZARD_PREVIEW','MYSQLPUNK_MSSQL_WIZARD_PREVIEW')
$variables=@('MYSQLPUNK_MSSQL_IMPORT_TRIAL','MYSQLPUNK_MSSQL_IMPORT_SERVER','MYSQLPUNK_MSSQL_IMPORT_DATABASE','MYSQLPUNK_MSSQL_IMPORT_INPUT','MYSQLPUNK_MSSQL_IMPORT_OUTPUT','MYSQLPUNK_SMOKE_FILTER')+$foreign
$previous=@{};foreach($name in $variables){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$nonce=[guid]::NewGuid().ToString('N');$job=[guid]::NewGuid().ToString('D');$child=$null
try{
    [IO.Directory]::CreateDirectory($outputFull)|Out-Null
    $request=@{SchemaVersion=2L;Kind='Request';Nonce=$nonce;JobId=$job;Server='localhost';DatabaseName='Transfer';InputCanonicalPath=$inputFull;OutputCanonicalPath=$outputFull;RequiredFreeBytes=68719476736L;CancelBelowBytes=21474836480L;SourceCommit=$pin.SourceCommit;SourceSnapshotSha256=$pin.SourceSnapshotSha256;ExecutableSha256=$pin.ExecutableSha256;ProductExecutableSha256=$pin.ProductExecutableSha256;ProductExecutablePath=$productExe;RegistrationSha256=$pin.RegistrationSha256}
    $decision=Test-TransferTrialRequest $request;if(!$decision.Allowed){throw $decision.Code}
    $requestPath=Join-Path $outputFull 'request.json'
    $requestStream=[IO.FileStream]::new($requestPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$requestBytes=[Text.UTF8Encoding]::new($false).GetBytes(($request|ConvertTo-Json -Depth 5));$requestStream.Write($requestBytes,0,$requestBytes.Length);$requestStream.Flush($true)}finally{$requestStream.Dispose()}
    foreach($name in $foreign){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_MSSQL_IMPORT_TRIAL','1','Process')
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_MSSQL_IMPORT_SERVER',$Server,'Process')
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_MSSQL_IMPORT_DATABASE',$Database,'Process')
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_MSSQL_IMPORT_INPUT',$inputFull,'Process')
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_MSSQL_IMPORT_OUTPUT',$outputFull,'Process')
    [Environment]::SetEnvironmentVariable('MYSQLPUNK_SMOKE_FILTER','MSSQL import Transfer trial','Process')
    # No Run-SmokeTests build/retry inside this one-shot runner. Existing registered SUT test exe is the sole child.
    $productReadLock=$null;$exeReadLock=$null
    try{
        $productReadLock=[IO.File]::Open($productExe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $exeReadLock=[IO.File]::Open($testExe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if((Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.ExecutableSha256){throw 'Executable changed before launch.'}
        if((Get-FileHash -LiteralPath $productExe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.ProductExecutableSha256){throw 'Product executable changed before launch.'}
    $child=Start-Process -FilePath $testExe -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $outputFull 'stdout.log') -RedirectStandardError (Join-Path $outputFull 'stderr.log')
    }finally{if($exeReadLock){$exeReadLock.Dispose()};if($productReadLock){$productReadLock.Dispose()}}
    $child.Refresh()
    $finalPath=Join-Path $outputFull 'final.json'
    if(!(Test-Path -LiteralPath $finalPath -PathType Leaf)){throw 'Missing execution receipt; skipped or missing registration is not acceptance. Preserve marker/output.'}
    $receipt=ConvertFrom-TransferBoundedJson (Read-TrialBoundedBytes (Assert-TrialPath $finalPath))
    $claimState=Test-TransferTrialMarkerPresence $receipt (Test-Path -LiteralPath $fixedAttempt -PathType Leaf) $child.ExitCode
    if(!$claimState.Allowed){throw $claimState.Code}
    $markerBytes=Read-TrialBoundedBytes (Assert-TrialPath $fixedAttempt)
    $marker=ConvertFrom-TransferBoundedJson $markerBytes
    $expected=$request.Clone();$expected.ProcessId=[long]$child.Id;$expected.RequestSha256=Hash-TrialBytes $requestBytes
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{
        $sid=[byte[]]::new($identity.User.BinaryLength);$identity.User.GetBinaryForm($sid,0)
        $expected.WindowsSid=[BitConverter]::ToString($sid).Replace('-','').ToLowerInvariant();$expected.WindowsName=$identity.Name
    }finally{$identity.Dispose()}
    $decision=Test-TransferTrialCompletionReceipt $expected $marker $receipt (Hash-TrialBytes $markerBytes)
    if($child.ExitCode -ne 0 -or !$decision.Allowed){throw ('Trial did not pass this invocation: '+$decision.Code+'; preserve marker, DB and output; no retry.')}
    Write-Host ('Trial completed with independent footer counts. Local receipt: '+$finalPath)
    Write-Host 'Object catalog inventory is separate. Source object-definition and field-hash equality are not proven; human UI/sound and production remain pending.'
}finally{
    foreach($name in $variables){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}
    # No marker removal, user DB cleanup, setting changes, retry, process-wide SQL KILL or source deletion.
}
