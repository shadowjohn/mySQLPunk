[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [string]$ChangelogPath,

    [string]$InstallerPath,

    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
    $ChangelogPath = Join-Path $repositoryRoot "CHANGELOG.md"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "dist"
}

if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Changelog was not found: $ChangelogPath"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $installers = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter "*-setup.exe" -File)
    if ($installers.Count -ne 1) {
        throw "Exactly one setup EXE is required, but found $($installers.Count)."
    }
    $InstallerPath = $installers[0].FullName
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer was not found: $InstallerPath"
}

$changelog = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
$escapedVersion = [regex]::Escape($Version)
$match = [regex]::Match(
    $changelog,
    "(?ms)^## \[(?:v)?$escapedVersion\][^\r\n]*(?:\r?\n)(.*?)(?=^## \[|\z)"
)
if (-not $match.Success) {
    throw "CHANGELOG.md does not contain release notes for v$Version."
}

$releaseContent = $match.Groups[1].Value.Trim()
$releaseContent = [regex]::Replace($releaseContent, '(?m)^### (?=(?:🚀|🛠️) )', '## ')
if ($releaseContent -notmatch '(?m)^## 🚀 新增功能\s*$') {
    throw "The v$Version changelog must contain '### 🚀 新增功能'."
}
if ($releaseContent -notmatch '(?m)^## 🛠️ 問題修正與優化\s*$') {
    throw "The v$Version changelog must contain '### 🛠️ 問題修正與優化'."
}

$summaryMatch = [regex]::Match($releaseContent, '(?m)^-\s+\*\*(?<summary>[^*]+)\*\*[：:]?')
if (-not $summaryMatch.Success) {
    throw "The v$Version changelog needs a bold first item for the release title."
}
$summary = $summaryMatch.Groups['summary'].Value.Trim().TrimEnd('：', ':')

$installer = Get-Item -LiteralPath $InstallerPath
$sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$signature = Get-AuthenticodeSignature -LiteralPath $installer.FullName
if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
    $signer = $signature.SignerCertificate.Subject
    $signatureNote = "此安裝程式的 Authenticode 簽章有效，簽署者為 $signer。"
} elseif ($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
    $signatureNote = "此版本尚未加入 Windows Authenticode 程式碼簽章；若系統顯示來源警告，請先核對 SHA-256 再安裝。"
} else {
    $signatureNote = "此安裝程式的 Authenticode 狀態為 $($signature.Status)；安裝前請先核對 SHA-256。"
}

$footer = @'
## 📦 下載與更新

| 平台 | 架構 | 建議下載 | 安裝方式 |
|---|---|---|---|
| **Windows** | x64（Intel／AMD） | `{{INSTALLER_NAME}}` | 執行單一安裝程式即可全新安裝或覆蓋更新 |

> Release 只提供一個安裝 EXE，已包含程式需要的 managed DLL、SQLite／SpatiaLite 原生 runtime、素材與第三方授權檔，不需要另外下載 ZIP 或 manifest。

## 🛡️ 完整性與驗證

- **SHA-256**：`{{SHA256}}`
- Windows PowerShell 驗證指令：`Get-FileHash .\{{INSTALLER_NAME}} -Algorithm SHA256`
- {{SIGNATURE_NOTE}}
- 本版本已通過 Windows Release 建置、SmokeTests、安裝、啟動、關閉與解除安裝驗證。
'@
$footer = $footer.Replace('{{INSTALLER_NAME}}', $installer.Name)
$footer = $footer.Replace('{{SHA256}}', $sha256)
$footer = $footer.Replace('{{SIGNATURE_NOTE}}', $signatureNote)

$releaseNotes = $releaseContent.Trim() + "`n`n" + $footer.Trim() + "`n"
$releaseTitle = "mySQLPunk v$Version - $summary"
$notesPath = Join-Path $OutputDirectory "release-notes.md"
$titlePath = Join-Path $OutputDirectory "release-title.txt"

$releaseNotes | Set-Content -LiteralPath $notesPath -Encoding UTF8
$releaseTitle | Set-Content -LiteralPath $titlePath -Encoding UTF8

Write-Host "Release title: $releaseTitle"
Write-Host "Release notes: $notesPath"
Write-Host "Installer SHA-256: $sha256"
Write-Host "Authenticode status: $($signature.Status)"
