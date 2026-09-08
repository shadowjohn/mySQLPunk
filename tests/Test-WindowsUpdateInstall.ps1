#requires -Version 5.1
<#
Runs the real Inno Setup update on a disposable GitHub-hosted Windows runner.
The update script comes from the current .NET Framework application assembly.
This does not click the application's update button or test its download dialog.
Only the baseline application-options.json is seeded. The updated application
must migrate its options while preserving the previous version's settings file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaselineInstaller,
    [string]$BaselineVersion = '1.0.0.21',
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$ApplicationPath,
    [Parameter(Mandatory = $true)][string]$WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Refuse developer machines and persistent/self-hosted runners before any write.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Actual installation is permitted only on a disposable GitHub-hosted Windows runner.'
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required.' }
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
if (-not $WorkRoot.StartsWith($runnerTemp + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkRoot must be a new directory strictly beneath RUNNER_TEMP.'
}
if (Test-Path -LiteralPath $WorkRoot) { throw 'WorkRoot already exists; refusing to reuse it.' }
$ancestor = [IO.DirectoryInfo]$WorkRoot
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Reparse points are not allowed in the test directory ancestry: $($ancestor.FullName)"
    }
    $ancestor = $ancestor.Parent
}
foreach ($inputPath in @($BaselineInstaller, $InstallerPath, $ApplicationPath)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing input: $inputPath" }
}
$BaselineInstaller = (Get-Item -LiteralPath $BaselineInstaller).FullName
$InstallerPath = (Get-Item -LiteralPath $InstallerPath).FullName
$ApplicationPath = (Get-Item -LiteralPath $ApplicationPath).FullName
$baselineExpectedVersion = [version]$BaselineVersion.TrimStart('v')
$expectedVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($ApplicationPath).FileVersion
if ($expectedVersion -le $baselineExpectedVersion) { throw 'Current application must be newer than the baseline.' }
if (@(Get-Process -Name mySQLPunk -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'A mySQLPunk process already exists; refusing to let the installer close unrelated applications.'
}
$uninstallKeyName = '{B6F02DBB-A4AF-495F-B7F1-7E2AF6A80B38}_is1'
foreach ($registryBase in @('HKCU:\Software', 'HKLM:\Software', 'HKCU:\Software\WOW6432Node', 'HKLM:\Software\WOW6432Node')) {
    if (Test-Path -LiteralPath "$registryBase\Microsoft\Windows\CurrentVersion\Uninstall\$uninstallKeyName") {
        throw 'An existing mySQLPunk installation is registered; refusing to replace it.'
    }
}

$null = New-Item -ItemType Directory -Path $WorkRoot
$installDirectory = Join-Path $WorkRoot "實際 安裝 O'Brien"
$installedExe = Join-Path $installDirectory 'mySQLPunk.exe'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:ownedProcesses = New-Object 'System.Collections.Generic.List[object]'
$script:settingsFixtures = New-Object 'System.Collections.Generic.List[object]'
$evidence = [ordered]@{
    schemaVersion = 1
    scenario = 'actual-windows-installer-update'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    status = 'running'
    runner = @{ environment = $env:RUNNER_ENVIRONMENT; os = $env:RUNNER_OS; runId = $env:GITHUB_RUN_ID }
    workRoot = $WorkRoot
    installDirectory = $installDirectory
    baselineVersion = $baselineExpectedVersion.ToString()
    expectedVersion = $expectedVersion.ToString()
    expectedApplicationSha256 = (Get-FileHash -LiteralPath $ApplicationPath -Algorithm SHA256).Hash
    baselineInstallerSha256 = (Get-FileHash -LiteralPath $BaselineInstaller -Algorithm SHA256).Hash
    installerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
    limitations = @(
        'Invokes the real generated update script; does not operate the UI update button or download dialog.',
        'Cross-version migration is asserted for application-options.json only.',
        'Synthetic application profiles outside WorkRoot are left for the disposable runner to destroy.'
    )
    cleanupErrors = @()
}
$evidencePath = Join-Path $WorkRoot 'acceptance.json'
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
$baselineInstalled = $false
$uninstallCompleted = $false
$failure = $null

function Assert-TestPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($WorkRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the test directory: $fullPath"
    }
    $item = [IO.DirectoryInfo][IO.Path]::GetDirectoryName($fullPath)
    while ($null -ne $item -and $item.FullName.StartsWith($WorkRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unsafe reparse point: $($item.FullName)" }
        $item = $item.Parent
    }
    if ((Test-Path -LiteralPath $fullPath) -and ((Get-Item -LiteralPath $fullPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unsafe reparse point: $fullPath"
    }
    return $fullPath
}

function Quote-NativeArgument([string]$Value) {
    if ($Value.Contains('"') -or $Value.EndsWith('\')) { throw 'Unexpected native argument quoting.' }
    return '"' + $Value + '"'
}

function Start-OwnedProcess([string]$Executable, [string[]]$Arguments, [string]$Label, [string]$ScriptPath = '') {
    if (-not [string]::Equals($Executable, $windowsPowerShell, [StringComparison]::OrdinalIgnoreCase)) {
        $null = Assert-TestPath $Executable
    } else {
        $null = Assert-TestPath $ScriptPath
    }
    $startArguments = @{
        FilePath = $Executable
        WorkingDirectory = $WorkRoot
        WindowStyle = 'Hidden'
        PassThru = $true
        RedirectStandardOutput = (Join-Path $WorkRoot ($Label + '.stdout.log'))
        RedirectStandardError = (Join-Path $WorkRoot ($Label + '.stderr.log'))
    }
    if ($Arguments.Count -gt 0) { $startArguments.ArgumentList = $Arguments }
    $process = Start-Process @startArguments
    $script:ownedProcesses.Add(@{ Process = $process; Executable = $Executable; StartedTicks = $process.StartTime.ToUniversalTime().Ticks; ScriptPath = $ScriptPath })
    return $process
}

function Assert-ProcessIdentity($Process, [string]$ExpectedPath, [long]$StartedTicks = 0) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "Process $($Process.Id) has already exited." }
    # Get-Process does not retain a handle by default. Keep one while the process
    # is alive so ExitCode remains available after the updater-started app exits.
    $null = $Process.Handle
    $actualPath = $Process.MainModule.FileName
    if (-not [string]::Equals($actualPath, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process $($Process.Id) does not have the expected executable path."
    }
    if ($StartedTicks -ne 0 -and $Process.StartTime.ToUniversalTime().Ticks -ne $StartedTicks) {
        throw "Process $($Process.Id) identity changed."
    }
}

function Wait-ProcessExit($Process, [int]$TimeoutSeconds, [string]$Label) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $Process.WaitForExit(500)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "$Label did not finish within $TimeoutSeconds seconds." }
    }
    $Process.Refresh()
    if ($null -eq $Process.ExitCode) { throw "$Label exited, but its exit code could not be read." }
    return $Process.ExitCode
}

function Invoke-FrameworkHelper([string]$Mode, [string]$AssemblyPath, [string]$OutputPath, [int]$OldProcessId = 0) {
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Quote-NativeArgument $helperPath),
        '-Mode', $Mode, '-AssemblyPath', (Quote-NativeArgument $AssemblyPath), '-OutputPath', (Quote-NativeArgument $OutputPath),
        '-InstallerPath', (Quote-NativeArgument $testInstaller), '-InstalledExe', (Quote-NativeArgument $installedExe),
        '-ScriptDirectory', (Quote-NativeArgument $WorkRoot), '-OldProcessId', "$OldProcessId")
    $helperProcess = Start-OwnedProcess $windowsPowerShell $arguments "framework-$Mode-$OldProcessId" $helperPath
    if ((Wait-ProcessExit $helperProcess 45 'Windows PowerShell assembly helper') -ne 0) { throw 'Windows PowerShell assembly helper failed. See its stderr log.' }
    return Get-Content -LiteralPath $OutputPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Initialize-SettingsFixture($Metadata, [bool]$Seed = $true) {
    # WinForms falls back to the entry-point namespace when AssemblyCompany is empty.
    $company = [string]$Metadata.company
    if ([string]::IsNullOrWhiteSpace($company)) { $company = [string]$Metadata.entryNamespace }
    $product = [string]$Metadata.product
    $productVersion = [string]$Metadata.productVersion
    foreach ($part in @($company, $product, $productVersion)) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $part -eq '..') {
            throw 'Application metadata cannot safely identify its settings directory.'
        }
    }
    if ($product -ne 'mySQLPunk' -or $company -ne 'mySQLPunk') { throw 'Unexpected application profile identity.' }
    $profilePath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "$company\$product\$productVersion"
    $settingsPath = Join-Path $profilePath 'application-options.json'
    if (@($script:settingsFixtures | Where-Object { $_.path -eq $settingsPath }).Count -gt 0) { return }
    if (Test-Path -LiteralPath $profilePath) { throw "Application profile already exists: $profilePath" }
    if (-not $Seed) {
        $script:settingsFixtures.Add(@{ path = $settingsPath; version = $productVersion; seeded = $false; autoCheckUpdates = $false })
        return
    }
    $null = New-Item -ItemType Directory -Path $profilePath
    $fixture = [ordered]@{
        BoolValues = @{ AutoCheckUpdates = $false; AdvancedRegisterSqlFileOpen = $false; AdvancedRegisterUrlProtocol = $false; AiAssistantEnabled = $false }
        IntValues = @{ RecordLimit = 321 }
        StringValues = @{ ViewAiPanelVisibilityPreference = 'closed'; FileQueryDirectory = (Join-Path $WorkRoot 'queries'); FileLogDirectory = (Join-Path $WorkRoot 'logs'); FileExportDirectory = (Join-Path $WorkRoot 'exports'); AcceptanceFixture = 'synthetic-settings-preserved' }
    }
    [IO.File]::WriteAllText($settingsPath, ($fixture | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($true)))
    $script:settingsFixtures.Add(@{ path = $settingsPath; version = $productVersion; seeded = $true; autoCheckUpdates = $false })
}

function Assert-SettingsPreserved {
    foreach ($fixture in $script:settingsFixtures) {
        $settings = Get-Content -LiteralPath $fixture.path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($settings.BoolValues.AutoCheckUpdates -ne $false -or $settings.IntValues.RecordLimit -ne 321 -or $settings.StringValues.AcceptanceFixture -ne 'synthetic-settings-preserved') {
            throw 'Synthetic settings changed or automatic update checks became enabled.'
        }
    }
}

function Get-TestApplicationProcesses {
    return @(Get-Process -Name mySQLPunk -ErrorAction SilentlyContinue | Where-Object {
        try { [string]::Equals($_.MainModule.FileName, $installedExe, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}

function Wait-ApplicationWindow($Process, [int]$TimeoutSeconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Assert-ProcessIdentity $Process $installedExe
        $windows = @([UpdateAcceptanceWindows]::ForProcess($Process.Id))
        $mainWindow = @($windows | Where-Object { $_.ClassName.StartsWith('WindowsForms10.Window.') -and $_.Title -match 'mySQLPunk' -and $_.Enabled })
        if ($mainWindow.Count -eq 1 -and [UpdateAcceptanceWindows]::Responds($mainWindow[0].Handle)) { return $mainWindow[0] }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Application $($Process.Id) did not expose a responsive main window."
}

function Close-TestApplication($Process, $Window) {
    $null = Assert-TestPath $installedExe
    Assert-ProcessIdentity $Process $installedExe
    if (-not [UpdateAcceptanceWindows]::Close($Window.Handle, $Process.Id)) { throw 'Could not request normal WM_CLOSE on the verified application window.' }
    $exitCode = Wait-ProcessExit $Process 45 'Application close'
    if ($exitCode -ne 0) { throw "Application returned exit code $exitCode during normal close." }
}

function Invoke-TestUninstall {
    if (@(Get-TestApplicationProcesses).Count -ne 0) { throw 'Refusing to uninstall while the test application is still running.' }
    $uninstaller = Assert-TestPath (Join-Path $installDirectory 'unins000.exe')
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Expected test uninstaller is missing.' }
    $process = Start-OwnedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG=' + (Quote-NativeArgument (Join-Path $WorkRoot 'uninstall.log')))) 'uninstall'
    $exitCode = Wait-ProcessExit $process 180 'Uninstaller'
    if ($exitCode -ne 0 -or (Test-Path -LiteralPath $installedExe)) { throw "Uninstall did not remove the test executable; exit code $exitCode." }
    # Inno Setup may still be removing its temporary uninstaller after the
    # registered executable exits. Let those children finish before leak checks.
    # https://jrsoftware.org/ishelp/topic_uninstexitcodes.htm
    $cleanupStarted = [DateTime]::UtcNow
    do {
        $remaining = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($WorkRoot + '\', [StringComparison]::OrdinalIgnoreCase)
        })
        foreach ($candidate in $remaining) {
            $null = Assert-TestPath $candidate.ExecutablePath
            if ($candidate.CreationDate.ToUniversalTime() -lt [DateTime]::Parse($evidence.startedUtc).ToUniversalTime()) {
                throw 'Unexpected process predating the test was found during uninstall cleanup.'
            }
        }
        if ($remaining.Count -eq 0) { break }
        if (([DateTime]::UtcNow - $cleanupStarted).TotalSeconds -ge 30) { throw 'Test processes did not finish naturally after uninstall.' }
        Start-Sleep -Milliseconds 500
    } while ($true)
    $evidence.uninstall = @{ exitCode = $exitCode; executableRemoved = $true; childCleanupCompleted = $true; childCleanupWaitSeconds = ([DateTime]::UtcNow - $cleanupStarted).TotalSeconds }
}

try {
    $env:TEMP = Join-Path $WorkRoot 'temp'
    $env:TMP = $env:TEMP
    $null = New-Item -ItemType Directory -Path $env:TEMP
    $testBaselineInstaller = Join-Path $WorkRoot 'baseline-setup.exe'
    $testInstaller = Join-Path $WorkRoot 'current-setup.exe'
    Copy-Item -LiteralPath $BaselineInstaller -Destination $testBaselineInstaller
    Copy-Item -LiteralPath $InstallerPath -Destination $testInstaller
    $helperPath = Join-Path $WorkRoot 'framework-helper.ps1'
    $helper = @'
param([string]$Mode, [string]$AssemblyPath, [string]$OutputPath, [string]$InstallerPath, [string]$InstalledExe, [string]$ScriptDirectory, [int]$OldProcessId)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'This helper requires Windows PowerShell and .NET Framework.' }
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    if ($Mode -eq 'metadata') {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($AssemblyPath)
        $result = @{ company = $info.CompanyName; product = $info.ProductName; productVersion = $info.ProductVersion; fileVersion = $info.FileVersion; entryNamespace = $assembly.EntryPoint.DeclaringType.Namespace }
    } elseif ($Mode -eq 'generate') {
        $service = $assembly.GetType('mySQLPunk.lib.AppUpdateService', $true)
        $method = $service.GetMethod('WriteInstallerUpdateApplyScript')
        $path = $method.Invoke($null, [object[]]@($InstallerPath, $InstalledExe, $OldProcessId, $ScriptDirectory))
        $result = @{ scriptPath = $path; frameworkVersion = [Environment]::Version.ToString(); edition = $PSVersionTable.PSEdition }
    } else { throw 'Unknown helper mode.' }
    [IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json), (New-Object Text.UTF8Encoding($true)))
} catch { Write-Error $_; exit 1 }
'@
    [IO.File]::WriteAllText($helperPath, $helper, (New-Object Text.UTF8Encoding($true)))
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public sealed class UpdateAcceptanceWindow {
    public IntPtr Handle; public string Title; public string ClassName; public bool Enabled;
}
public static class UpdateAcceptanceWindows {
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    public static UpdateAcceptanceWindow[] ForProcess(int expectedProcessId) {
        var windows = new List<UpdateAcceptanceWindow>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter) {
            uint processId; GetWindowThreadProcessId(window, out processId);
            if (processId != expectedProcessId) return true;
            var title = new StringBuilder(1024); var className = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity); GetClassName(window, className, className.Capacity);
            windows.Add(new UpdateAcceptanceWindow { Handle = window, Title = title.ToString(), ClassName = className.ToString(), Enabled = IsWindowEnabled(window) });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static bool Responds(IntPtr window) {
        IntPtr result; return SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 2000, out result) != IntPtr.Zero;
    }
    public static bool Close(IntPtr window, int expectedProcessId) {
        uint processId; GetWindowThreadProcessId(window, out processId);
        return processId == expectedProcessId && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
    $installLog = Join-Path $WorkRoot 'baseline-install.log'
    $baselineProcess = Start-OwnedProcess $testBaselineInstaller @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', ('/DIR=' + (Quote-NativeArgument $installDirectory)), ('/LOG=' + (Quote-NativeArgument $installLog))) 'baseline-install'
    $installExitCode = Wait-ProcessExit $baselineProcess 240 'Baseline installer'
    $baselineInstalled = Test-Path -LiteralPath $installedExe -PathType Leaf
    if ($installExitCode -ne 0 -or -not $baselineInstalled) { throw "Baseline installation failed with exit code $installExitCode." }
    $actualBaselineVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion
    if ($actualBaselineVersion -ne $baselineExpectedVersion) { throw 'Installed baseline version did not match the requested version.' }
    $baselineHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    $evidence.baselineInstallation = @{ exitCode = $installExitCode; fileVersion = $actualBaselineVersion.ToString(); applicationSha256 = $baselineHash; log = $installLog }
    Initialize-SettingsFixture (Invoke-FrameworkHelper 'metadata' $installedExe (Join-Path $WorkRoot 'baseline-metadata.json'))
    Initialize-SettingsFixture (Invoke-FrameworkHelper 'metadata' $ApplicationPath (Join-Path $WorkRoot 'current-metadata.json')) $false
    $evidence.settingsFixtures = @($script:settingsFixtures.ToArray())
    $baselineSettings = @($script:settingsFixtures | Where-Object { $_.seeded })
    $currentSettings = @($script:settingsFixtures | Where-Object { -not $_.seeded })
    if ($baselineSettings.Count -ne 1 -or $currentSettings.Count -ne 1 -or (Test-Path -LiteralPath $currentSettings[0].path)) {
        throw 'Settings migration requires one baseline fixture and an absent current settings file.'
    }
    $syntheticFile = Join-Path $installDirectory '驗收使用者查詢.sql'
    [IO.File]::WriteAllText($syntheticFile, "-- Synthetic acceptance data only.`r`nSELECT '設定保留 O''Brien';`r`n", (New-Object Text.UTF8Encoding($true)))
    $syntheticHash = (Get-FileHash -LiteralPath $syntheticFile -Algorithm SHA256).Hash
    $oldApplication = Start-OwnedProcess $installedExe @() 'baseline-application'
    $oldWindow = Wait-ApplicationWindow $oldApplication
    $evidence.oldApplication = @{ processId = $oldApplication.Id; executablePath = $installedExe; windowTitle = $oldWindow.Title; responsive = $true }
    $generated = Invoke-FrameworkHelper 'generate' $ApplicationPath (Join-Path $WorkRoot 'generated-script.json') $oldApplication.Id
    $applyScript = Assert-TestPath $generated.scriptPath
    $applyProcess = Start-OwnedProcess $windowsPowerShell @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Quote-NativeArgument $applyScript)) 'apply-update' $applyScript
    $waitStarted = [DateTime]::UtcNow
    for ($sample = 0; $sample -lt 8; $sample++) {
        Start-Sleep -Milliseconds 500
        Assert-ProcessIdentity $oldApplication $installedExe
        $applyProcess.Refresh()
        if ($applyProcess.HasExited) { throw 'Updater exited while the baseline application was still running.' }
        if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne $baselineHash) { throw 'Updater replaced the executable before the baseline application exited.' }
    }
    $evidence.waitForOldApplication = @{ observedSeconds = ([DateTime]::UtcNow - $waitStarted).TotalSeconds; updaterStillRunning = $true; baselineExecutableUnchanged = $true }
    Close-TestApplication $oldApplication $oldWindow
    $evidence.oldApplication.normalClose = $true
    $baselineSettingsHash = (Get-FileHash -LiteralPath $baselineSettings[0].path -Algorithm SHA256).Hash
    $applyExitCode = Wait-ProcessExit $applyProcess 240 'Generated installer update'
    $evidence.update = @{ exitCode = $applyExitCode; scriptPath = $applyScript; scriptSha256 = (Get-FileHash -LiteralPath $applyScript -Algorithm SHA256).Hash; generatorFramework = $generated.frameworkVersion; generatorEdition = $generated.edition }
    if ($applyExitCode -ne 0) { throw "Generated update script failed with exit code $applyExitCode." }
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $restarted = @(Get-TestApplicationProcesses | Where-Object { $_.Id -ne $oldApplication.Id })
        if ($restarted.Count -eq 1) { break }
        if ($restarted.Count -gt 1) { throw 'Updater started more than one application instance.' }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $restartDeadline)
    if ($restarted.Count -ne 1) { throw 'Updater did not automatically restart the installed application.' }
    $newApplication = $restarted[0]
    $script:ownedProcesses.Add(@{ Process = $newApplication; Executable = $installedExe; StartedTicks = $newApplication.StartTime.ToUniversalTime().Ticks; ScriptPath = '' })
    $newWindow = Wait-ApplicationWindow $newApplication
    $actualVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion
    $actualHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    if ($actualVersion -ne $expectedVersion -or $actualHash -ne $evidence.expectedApplicationSha256) { throw 'Updated installed executable does not match the current build.' }
    if ((Get-FileHash -LiteralPath $syntheticFile -Algorithm SHA256).Hash -ne $syntheticHash) { throw 'Installer update modified synthetic user data.' }
    Assert-SettingsPreserved
    $evidence.newApplication = @{ processId = $newApplication.Id; executablePath = $newApplication.MainModule.FileName; windowTitle = $newWindow.Title; responsive = $true; automaticallyRestarted = $true; fileVersion = $actualVersion.ToString(); sha256 = $actualHash }
    Close-TestApplication $newApplication $newWindow
    $evidence.newApplication.normalClose = $true
    Assert-SettingsPreserved
    if ((Get-FileHash -LiteralPath $baselineSettings[0].path -Algorithm SHA256).Hash -ne $baselineSettingsHash) {
        throw 'Settings migration modified the previous version settings file.'
    }
    $evidence.settingsMigration = @{ fileName = 'application-options.json'; fromVersion = $baselineSettings[0].version; toVersion = $currentSettings[0].version; targetAbsentBeforeUpdate = $true; optionsPreserved = $true; sourceSha256 = $baselineSettingsHash; sourceUnchanged = $true }
    $evidence.syntheticData = @{ path = $syntheticFile; sha256 = $syntheticHash; preservedAfterUpdate = $true; settingsPreserved = $true }
    Invoke-TestUninstall
    $uninstallCompleted = $true
    if ((Get-FileHash -LiteralPath $syntheticFile -Algorithm SHA256).Hash -ne $syntheticHash) { throw 'Uninstall removed or modified the unowned synthetic user file.' }
    $evidence.syntheticData.preservedAfterUninstall = $true
    $evidence.status = 'passed'
} catch {
    $failure = $_
    $evidence.status = 'failed'
    $evidence.error = $_.Exception.Message
} finally {
    # Kill only processes whose full executable path and creation time match our
    # recorded child. This is failure cleanup, never evidence of a normal close.
    foreach ($owned in $script:ownedProcesses) {
        try {
            $owned.Process.Refresh()
            if (-not $owned.Process.HasExited) {
                Assert-ProcessIdentity $owned.Process $owned.Executable $owned.StartedTicks
                if ($owned.ScriptPath) { $null = Assert-TestPath $owned.ScriptPath } else { $null = Assert-TestPath $owned.Executable }
                $owned.Process.Kill()
                $null = Wait-ProcessExit $owned.Process 15 'Failure cleanup process'
            }
        } catch { $evidence.cleanupErrors += $_.Exception.Message }
    }
    # A failed setup may leave its extracted child running, and a failed restart
    # assertion may happen before the new application is added to our list.
    # TEMP/TMP are inside WorkRoot, so those executable paths are isolated too.
    $evidence.failureCleanupTerminations = @()
    for ($cleanupPass = 0; $cleanupPass -lt 2; $cleanupPass++) {
        try {
            $testProcesses = @(Get-CimInstance Win32_Process | Where-Object {
                $_.ExecutablePath -and $_.ExecutablePath.StartsWith($WorkRoot + '\', [StringComparison]::OrdinalIgnoreCase)
            })
            foreach ($candidate in $testProcesses) {
                $candidatePath = Assert-TestPath $candidate.ExecutablePath
                $candidateProcess = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $candidateProcess) { continue }
                Assert-ProcessIdentity $candidateProcess $candidatePath
                if ($candidateProcess.StartTime.ToUniversalTime() -lt [DateTime]::Parse($evidence.startedUtc).ToUniversalTime()) {
                    throw 'Refusing to stop a process that predates this isolated test.'
                }
                $evidence.failureCleanupTerminations += @{ processId = $candidateProcess.Id; executablePath = $candidatePath }
                $candidateProcess.Kill()
                $null = Wait-ProcessExit $candidateProcess 15 'Extracted installer/application cleanup'
            }
        } catch { $evidence.cleanupErrors += $_.Exception.Message }
        if ($cleanupPass -eq 0) { Start-Sleep -Milliseconds 500 }
    }
    if ($evidence.status -eq 'passed' -and $evidence.failureCleanupTerminations.Count -gt 0) {
        $evidence.status = 'failed'
        $evidence.cleanupErrors += 'Successful acceptance must not leave an application or installer requiring forced cleanup.'
    }
    if (-not $uninstallCompleted -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe') -PathType Leaf)) {
        try { Invoke-TestUninstall } catch { $evidence.cleanupErrors += $_.Exception.Message }
    }
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    $evidence.completedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($true)))
    Write-Output "Installation acceptance evidence: $evidencePath"
}
if ($null -ne $failure) { throw $failure }
if ($evidence.cleanupErrors.Count -gt 0) { throw 'Acceptance cleanup failed; see acceptance.json.' }
Write-Output ($evidence | ConvertTo-Json -Depth 10)
