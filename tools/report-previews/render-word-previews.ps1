param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$samples = Join-Path $PSScriptRoot '..\..\mySQLPunk\report-templates'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory for preview rendering.' }
New-Item -ItemType Directory -Path $output | Out-Null
$word = $null
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $word.AutomationSecurity = 3
    foreach ($stem in @('docx-classic', 'docx-delivery', 'docx-schema-specification')) {
        $path = [System.IO.Path]::GetFullPath((Join-Path $samples ($stem + '.docx')))
        $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $document = $null
        try {
            $document = $word.Documents.Open($path, $false, $true, $false)
            $document.ExportAsFixedFormat((Join-Path $output ($stem + '.pdf')), 17)
        }
        finally {
            if ($null -ne $document) {
                $document.Close(0)
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($document)
            }
        }
        $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($before -ne $after) { throw "Source changed during rendering: $stem" }
        Write-Output ($stem + ': source SHA-256 unchanged, Word PDF rendered')
    }
}
finally {
    if ($null -ne $word) {
        $word.Quit(0)
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
    }
}
