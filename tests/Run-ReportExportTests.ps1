param([string]$Configuration = 'Debug', [switch]$SkipBuild, [string]$ArtifactDirectory, [switch]$Run)
$ErrorActionPreference = 'Stop'
if (!$Run) {
    Write-Warning 'Report export tests are paused after an endpoint-protection quarantine. No test executable was compiled or started. Review the alert and source before explicitly selecting -Run.'
    return
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$SkipBuild) { & (Join-Path $repo 'build.bat') $Configuration; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
$output = Join-Path $repo "mySQLPunk\bin\$Configuration"
$csc = $env:CSC_EXE
if (!$csc) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) { $csc = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1 }
}
if (!$csc) {
    $csc = @('C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe', 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe', 'C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$csc) { throw 'C# compiler unavailable; set CSC_EXE.' }
$test = Join-Path $output 'ReportExportTests.exe'
$references = @('mySQLPunk.exe', 'Newtonsoft.Json.dll', 'System.Data.SQLite.dll', 'PdfSharp-gdi.dll', 'PdfSharp.Shared.dll', 'PdfSharp.System.dll') | ForEach-Object { '/r:' + (Join-Path $output $_) }
& $csc /nologo /platform:anycpu "/out:$test" $references /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Data.dll /r:System.Core.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'ReportExportTests.cs') (Join-Path $PSScriptRoot 'ReportExportTests.OpenXml.cs') (Join-Path $PSScriptRoot 'ReportExportTests.Pdf.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $output 'mySQLPunk.exe.config') -Destination "$test.config" -Force
& $test $repo $ArtifactDirectory
exit $LASTEXITCODE
