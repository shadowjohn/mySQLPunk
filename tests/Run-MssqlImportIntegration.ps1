param(
    [switch]$Run,
    [string]$AuthorizationPath,
    [Alias('Profile')][ValidateSet('Core','Lob','Geometry','Faults','Memory','All')][string]$AcceptanceProfile='Core',
    [string]$OutputDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MssqlImportIntegration.Guards.ps1')
if(!$Run) {
    Write-Host 'Opt-in isolated native MSSQL importer acceptance. Supply -Run -AuthorizationPath <approved manifest> -Profile Core|Lob|Geometry|Faults|Memory|All -OutputDirectory <new empty C descendant>.'
    Write-Host 'Requires the exact already-running authorized LocalDB instance. No auto-start, saved connections or sqlcmd restore.'
    return
}
if([string]::IsNullOrWhiteSpace($AuthorizationPath) -or !(Test-Path -LiteralPath $AuthorizationPath -PathType Leaf)) {throw 'Explicit authorization manifest required.'}
$authorization=[IO.Path]::GetFullPath($AuthorizationPath)
$manifest=Get-Content -LiteralPath $authorization -Raw -ErrorAction Stop | ConvertFrom-Json
$approval=Assert-ImportAuthorizationEnvelope -Run $true -Manifest $manifest -Server $script:ImportExpectedServer -OutputDirectory $OutputDirectory -FreeCBytes (Get-PSDrive C).Free
$instanceOutput=& sqllocaldb.exe info 'mySQLPunkExportTest_20261012'
if($LASTEXITCODE -ne 0 -or !($instanceOutput -match 'LOCALDB#')) {throw 'Authorized LocalDB must already be running; runner never starts it.'}
[IO.Directory]::CreateDirectory($approval.Output) | Out-Null
$names=@('MYSQLPUNK_MSSQL_IMPORT_INTEGRATION','MYSQLPUNK_MSSQL_IMPORT_SERVER','MYSQLPUNK_MSSQL_IMPORT_OUTPUT','MYSQLPUNK_MSSQL_IMPORT_AUTH','MYSQLPUNK_MSSQL_IMPORT_PROFILE','MYSQLPUNK_MSSQL_IMPORT_FIXTURES','MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE','MYSQLPUNK_MSSQL_IMPORT_RUN_ID','MYSQLPUNK_SMOKE_FILTER')
$saved=@{};foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
try {
    $env:MYSQLPUNK_MSSQL_IMPORT_INTEGRATION='1'
    $env:MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE=$null
    $env:MYSQLPUNK_MSSQL_IMPORT_SERVER=$approval.Server
    $env:MYSQLPUNK_MSSQL_IMPORT_OUTPUT=$approval.Output
    $env:MYSQLPUNK_MSSQL_IMPORT_AUTH=$authorization
    $env:MYSQLPUNK_MSSQL_IMPORT_FIXTURES=Join-Path $PSScriptRoot 'MssqlExportFixtures'
    $env:MYSQLPUNK_SMOKE_FILTER='MSSQL import integration'
    $expectations=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MssqlImportFixtures/expected-cases.json') -Raw | ConvertFrom-Json
    $profiles=if($AcceptanceProfile -eq 'All'){@('Core','Lob','Geometry','Faults','Memory')}else{@($AcceptanceProfile)}
    $built=[bool]$SkipBuild
    foreach($one in $profiles) {
        $env:MYSQLPUNK_MSSQL_IMPORT_PROFILE=$one
        $env:MYSQLPUNK_MSSQL_IMPORT_RUN_ID=[Guid]::NewGuid().ToString('N')
        $log=Join-Path $approval.Output ('run-'+$one+'.log')
        $ErrorActionPreference='Continue'
        try {
            if(!$built){& (Join-Path $PSScriptRoot 'Run-SmokeTests.ps1') -Configuration Release *> $log;$built=$true}
            else{& (Join-Path $PSScriptRoot '../mySQLPunk/bin/CodexVerify/mySQLPunk.SmokeTests.exe') *> $log}
            $code=$LASTEXITCODE
        }finally{$ErrorActionPreference='Stop'}
        Get-Content -LiteralPath $log -Tail 10
        if($code -ne 0){throw "Native import profile $one failed (exit $code); inspect $log."}
        $receiptPath=Join-Path $approval.Output ($one+'/profile-receipt.json')
        if(!(Test-Path -LiteralPath $receiptPath -PathType Leaf)){throw 'No actual profile receipt; SKIP or unmatched tests cannot count as acceptance.'}
        $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        Assert-ImportProfileReceipt -Receipt $receipt -AcceptanceProfile $one -RunId $env:MYSQLPUNK_MSSQL_IMPORT_RUN_ID -ExpectedCases @($expectations.$one)
        $exe=Join-Path $PSScriptRoot '../mySQLPunk/bin/CodexVerify/mySQLPunk.SmokeTests.exe'
        if($receipt.executableSha256 -ine (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash){throw 'Profile executable receipt mismatch.'}
        foreach($case in $receipt.cases) {
            $casePath=Join-Path $approval.Output ($one+'/'+$case.caseId+'.json')
            if((Get-FileHash -LiteralPath $casePath -Algorithm SHA256).Hash -ine $case.receiptSha256){throw 'Case receipt hash mismatch.'}
        }
    }
}finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}}