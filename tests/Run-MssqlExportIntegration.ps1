param(
    [switch]$Run,
    [string]$Server,
    [ValidateSet('Core','Large','Concurrency','ClientProbe','FinalFix','Ssh')][string]$Profile = 'Core',
    [string]$OutputDirectory,
    [ValidateSet('Classic','Go')][string]$Client = 'Classic',
    [string]$SqlcmdPath,
    [string]$RestoreArtifact
)
$ErrorActionPreference = 'Stop'
if (!$Run) {
    Write-Host 'Opt-in isolated MSSQL acceptance. Supply -Run -Server "(localdb)\mySQLPunkExportTest_20261012" -Profile Core|Large|Concurrency -OutputDirectory <new empty C: fixture directory>.'
    Write-Host 'Uses Integrated Security; creates only token-owned GUID source/target databases; requires an already running authorized fixture and sqlcmd. Never reads saved connections or installs/starts a server.'
    return
}
if ($Profile -eq 'Ssh') { throw 'PENDING: an explicitly authorized SSH fixture and host-key policy are required. No connection attempted.' }
if ($Server -cne '(localdb)\mySQLPunkExportTest_20261012') { throw 'This acceptance runner is restricted to the explicitly authorized dedicated LocalDB instance.' }
$fixture = Get-Content -LiteralPath "$PSScriptRoot/../.superpowers/sdd/implementation-plan/fixture-environment.json" -Raw | ConvertFrom-Json
if (!$fixture.CreatedByTask -or $fixture.Instance -cne 'mySQLPunkExportTest_20261012') { throw 'Fixture authorization manifest mismatch.' }
$root = [IO.Path]::GetFullPath($fixture.FixtureRoot).TrimEnd('\') + '\'
if (!$root.StartsWith('C:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Bulk fixtures must remain on C:.' }
if (!$OutputDirectory) { throw 'Supply an explicit new/empty OutputDirectory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be a descendant of the authorized FixtureRoot.' }
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'OutputDirectory must be new or empty.' }
$ancestor = $output
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point fixture paths are not accepted.' }
    $ancestor = Split-Path -Parent $ancestor
}
$state = & sqllocaldb info 'mySQLPunkExportTest_20261012'
if ($LASTEXITCODE -ne 0 -or !($state -match 'LOCALDB#')) { throw 'Authorized LocalDB must already be running; runner does not start it.' }
$sqlcmd = if ($SqlcmdPath) { (Get-Item -LiteralPath $SqlcmdPath -ErrorAction Stop).FullName } else { (Get-Command sqlcmd -CommandType Application -ErrorAction Stop).Source }
if ($Client -eq 'Go' -and !$SqlcmdPath) { throw 'Go requires an explicit verified official SqlcmdPath; no automatic download.' }
if ($RestoreArtifact) {
    if ($Profile -ne 'Large') { throw 'RestoreArtifact is limited to the Large factor 10 profile.' }
    $RestoreArtifact = [IO.Path]::GetFullPath($RestoreArtifact)
    if (!$RestoreArtifact.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'RestoreArtifact must belong to the authorized fixture root.' }
    $artifactAncestor = $RestoreArtifact
    while ($artifactAncestor) {
        if ((Test-Path -LiteralPath $artifactAncestor) -and ((Get-Item -LiteralPath $artifactAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point restore artifacts are not accepted.' }
        $artifactAncestor = Split-Path -Parent $artifactAncestor
    }
    $artifactDir = Split-Path -Parent $RestoreArtifact
    $oldEvidence = Get-Content -LiteralPath (Join-Path $artifactDir 'evidence.json') -Raw | ConvertFrom-Json
    if ((Get-Content -LiteralPath (Join-Path $artifactDir 'owner.token') -Raw) -cne $oldEvidence.owner) { throw 'Original artifact ownership evidence mismatch.' }
    $oldArtifact = $oldEvidence.('artifact-' + [IO.Path]::GetFileName($RestoreArtifact))
    if (!$oldArtifact -or $oldArtifact.bytes -le 2147483648 -or (Get-FileHash -LiteralPath $RestoreArtifact -Algorithm SHA256).Hash -ine $oldArtifact.sha256) { throw 'Original large service artifact hash/length evidence mismatch.' }
}
if ((Get-PSDrive C).Free -lt 20GB) { throw 'At least 20 GiB free C: space is required.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
& $sqlcmd -? | Out-File (Join-Path $output 'sqlcmd-version.txt') -Encoding utf8
$peReader = [IO.BinaryReader]::new([IO.File]::OpenRead($sqlcmd))
try {
    $peReader.BaseStream.Position = 60
    $peOffset = $peReader.ReadInt32()
    $peReader.BaseStream.Position = $peOffset + 4
    $machine = $peReader.ReadUInt16()
    $architecture = switch ($machine) { 0x8664 { 'x64' } 0x014c { 'x86' } 0xAA64 { 'ARM64' } default { "PE-machine-$machine" } }
    @{ path=$sqlcmd; architecture=$architecture; fileVersion=(Get-Item -LiteralPath $sqlcmd).VersionInfo.FileVersion } | ConvertTo-Json | Out-File (Join-Path $output 'sqlcmd-identity.json') -Encoding utf8
} finally { $peReader.Dispose() }
$envNames = @('MYSQLPUNK_SMOKE_FILTER','MYSQLPUNK_MSSQL_INTEGRATION','MYSQLPUNK_MSSQL_INTEGRATION_SERVER','MYSQLPUNK_MSSQL_INTEGRATION_OUTPUT','MYSQLPUNK_MSSQL_INTEGRATION_FIXTURES','MYSQLPUNK_MSSQL_INTEGRATION_FACTOR','MYSQLPUNK_MSSQL_INTEGRATION_SQLCMD','MYSQLPUNK_MSSQL_INTEGRATION_CLIENT','MYSQLPUNK_MSSQL_INTEGRATION_RESTORE','MYSQLPUNK_MSSQL_INTEGRATION_PIPE')
$saved = @{}
foreach ($name in $envNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:MYSQLPUNK_SMOKE_FILTER = 'MSSQL export integration opt-in'
    $env:MYSQLPUNK_MSSQL_INTEGRATION = $Profile
    $env:MYSQLPUNK_MSSQL_INTEGRATION_SERVER = $Server
    $env:MYSQLPUNK_MSSQL_INTEGRATION_FIXTURES = Join-Path $PSScriptRoot 'MssqlExportFixtures'
    $env:MYSQLPUNK_MSSQL_INTEGRATION_SQLCMD = $sqlcmd
    $env:MYSQLPUNK_MSSQL_INTEGRATION_CLIENT = $Client
    $pipe = [regex]::Match(($state -join ' '), '\\\\\.\\pipe\\LOCALDB#[A-Fa-f0-9]+\\tsql\\query').Value
    if ($Client -eq 'Go' -and !$pipe) { throw 'Exact authorized instance pipe could not be resolved.' }
    $env:MYSQLPUNK_MSSQL_INTEGRATION_PIPE = $pipe
    $env:MYSQLPUNK_MSSQL_INTEGRATION_RESTORE = $RestoreArtifact
    $factors = @(1)
    if ($Profile -eq 'Large') { $factors = if ($RestoreArtifact) { @(10) } else { @(1,10) } }
    foreach ($factor in $factors) {
        $phaseDir = Join-Path $output ("{0}-{1}" -f $Profile,$factor)
        [IO.Directory]::CreateDirectory($phaseDir) | Out-Null
        $env:MYSQLPUNK_MSSQL_INTEGRATION_OUTPUT = $phaseDir
        $env:MYSQLPUNK_MSSQL_INTEGRATION_FACTOR = "$factor"
        # Independent process peaks; builds and executions are strictly sequential.
        # PowerShell 5 surfaces child stderr as ErrorRecord; retain it in the log before checking native exit.
        $ErrorActionPreference = 'Continue'
        try { & powershell.exe -NoProfile -File "$PSScriptRoot/Run-SmokeTests.ps1" -Configuration Release *> (Join-Path $output "run-$factor.log") }
        finally { $ErrorActionPreference = 'Stop' }
        $exitCode = $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $output "run-$factor.log") -Tail 12
        if ($exitCode -ne 0) { throw "Integration $Profile factor=$factor failed (exit $exitCode); inspect run-$factor.log. An AV block must be reported pending, never bypassed." }
    }
} finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
}
