param([Parameter(Mandatory=$true)][string]$Directory, [string]$SdkPath)
$ErrorActionPreference = 'Stop'
if (!$SdkPath) { $SdkPath = Join-Path $PSScriptRoot '..\packages\DocumentFormat.OpenXml.2.20.0\lib\net46\DocumentFormat.OpenXml.dll' }
Add-Type -AssemblyName WindowsBase
Add-Type -Path $SdkPath
$validator = New-Object DocumentFormat.OpenXml.Validation.OpenXmlValidator
$errorCount = 0
foreach ($file in (Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -in '.docx', '.xlsx' })) {
    if ($file.Extension -eq '.docx') { $document = [DocumentFormat.OpenXml.Packaging.WordprocessingDocument]::Open($file.FullName, $false) }
    else { $document = [DocumentFormat.OpenXml.Packaging.SpreadsheetDocument]::Open($file.FullName, $false) }
    try {
        $errors = @($validator.Validate($document))
        $errorCount += $errors.Count
        Write-Output "$($file.Name): $($errors.Count) Open XML schema errors"
        foreach ($validationError in ($errors | Select-Object -First 15)) {
            Write-Output ($validationError.Description + ' at ' + $validationError.Path.XPath)
        }
    } finally { $document.Dispose() }
}
if ($errorCount) { exit 1 }
