param(
    [string]$Msys2Root = "C:\msys64",
    [string]$WorkDir = "$PSScriptRoot\work",
    [string]$RuntimeDir = "$PSScriptRoot\..\..\mySQLPunk\binary\sqlite3_ext",
    [string]$LibSpatialiteUrl = "https://www.gaia-gis.it/gaia-sins/libspatialite-sources/libspatialite-5.1.0.zip",
    [string]$ExpectedSha256 = "",
    [string]$SourceCacheDir = "$PSScriptRoot\cache",
    [string]$OfflinePackagePath = "",
    [switch]$PreferCachedSource,
    [switch]$SkipPacman,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function ConvertTo-MsysPath([string]$Path) {
    $full = Resolve-FullPath $Path
    if ($full -match "^([A-Za-z]):\\(.*)$") {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2] -replace "\\", "/"
        return "/" + $drive + "/" + $rest
    }
    return $full -replace "\\", "/"
}

$work = Resolve-FullPath $WorkDir
$runtime = Resolve-FullPath $RuntimeDir
$sourceCache = Resolve-FullPath $SourceCacheDir
$bash = Join-Path $Msys2Root "usr\bin\bash.exe"

if (!(Test-Path -LiteralPath $bash)) {
    throw "找不到 MSYS2 bash：$bash。請先安裝 MSYS2，或使用 -Msys2Root 指到安裝路徑。"
}

if (!(Test-Path -LiteralPath $work)) {
    New-Item -ItemType Directory -Path $work | Out-Null
}
if (!(Test-Path -LiteralPath $runtime)) {
    New-Item -ItemType Directory -Path $runtime | Out-Null
}
if (!(Test-Path -LiteralPath $sourceCache)) {
    New-Item -ItemType Directory -Path $sourceCache | Out-Null
}

$zipPath = Join-Path $work "libspatialite-5.1.0.zip"
$cacheZipPath = Join-Path $sourceCache "libspatialite-5.1.0.zip"
$srcDir = Join-Path $work "libspatialite-5.1.0"

function Copy-SourceArchive([string]$SourcePath, [string]$TargetPath, [string]$Reason) {
    if (!(Test-Path -LiteralPath $SourcePath)) {
        throw "找不到 SpatiaLite 來源 zip：$SourcePath"
    }
    Write-Host "$Reason：$SourcePath"
    Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Force
}

if ($OfflinePackagePath) {
    Copy-SourceArchive -SourcePath (Resolve-FullPath $OfflinePackagePath) -TargetPath $zipPath -Reason "使用離線 libspatialite 原始碼"
}
elseif ($PreferCachedSource -and (Test-Path -LiteralPath $cacheZipPath)) {
    Copy-SourceArchive -SourcePath $cacheZipPath -TargetPath $zipPath -Reason "使用快取 libspatialite 原始碼"
}
else {
    try {
        Write-Host "下載 libspatialite 原始碼..."
        Invoke-WebRequest -Uri $LibSpatialiteUrl -OutFile $zipPath
        Copy-Item -LiteralPath $zipPath -Destination $cacheZipPath -Force
        Write-Host "來源 zip 已快取：$cacheZipPath"
    }
    catch {
        if (Test-Path -LiteralPath $cacheZipPath) {
            Write-Host "下載失敗，改用快取 libspatialite 原始碼：$cacheZipPath"
            Copy-Item -LiteralPath $cacheZipPath -Destination $zipPath -Force
        }
        else {
            throw
        }
    }
}

$actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
if ($ExpectedSha256 -and $actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "libspatialite source SHA256 不符。Expected=$ExpectedSha256 Actual=$actualSha256"
}

if (!(Test-Path -LiteralPath $cacheZipPath)) {
    Copy-Item -LiteralPath $zipPath -Destination $cacheZipPath -Force
}

if (Test-Path -LiteralPath $srcDir) {
    Remove-Item -LiteralPath $srcDir -Recurse -Force
}
Expand-Archive -LiteralPath $zipPath -DestinationPath $work -Force

$msysWork = ConvertTo-MsysPath $work
$msysRuntime = ConvertTo-MsysPath $runtime

$pacman = @"
set -euo pipefail
pacman -S --needed --noconfirm \
  mingw-w64-x86_64-toolchain \
  mingw-w64-x86_64-sqlite3 \
  mingw-w64-x86_64-geos \
  mingw-w64-x86_64-proj \
  mingw-w64-x86_64-freexl \
  mingw-w64-x86_64-libxml2 \
  mingw-w64-x86_64-minizip \
  mingw-w64-x86_64-curl \
  mingw-w64-x86_64-librttopo \
  make unzip
"@

$build = @"
set -euo pipefail
export PATH=/mingw64/bin:/usr/bin:`$PATH
cd "$msysWork/libspatialite-5.1.0"
./configure --prefix=/mingw64 --enable-shared --disable-static
make -j`$(nproc)
make install

mkdir -p "$msysRuntime"
candidate=""
for file in /mingw64/bin/mod_spatialite.dll /mingw64/bin/libspatialite.dll; do
  if [ -f "`$file" ]; then
    candidate="`$file"
    cp "`$file" "$msysRuntime/"
  fi
done

if [ -z "`$candidate" ]; then
  echo "找不到 mod_spatialite.dll 或 libspatialite.dll" >&2
  exit 1
fi

ldd "`$candidate" | awk '/\/mingw64\/bin\// { print `$3 }' | while read dll; do
  if [ -f "`$dll" ]; then
    cp -n "`$dll" "$msysRuntime/" || true
  fi
done

cp -n /mingw64/share/proj/proj.db "$msysRuntime/" || true
rm -f "$msysRuntime/sqlite3.exe" "$msysRuntime"/libreadline*.dll "$msysRuntime"/libtermcap*.dll
"@

if (!$SkipPacman) {
    Write-Host "安裝/確認 MSYS2 相依套件..."
    & $bash -lc $pacman
}

Write-Host "從原始碼編譯 libspatialite 並複製 runtime..."
& $bash -lc $build

$manifestPath = Join-Path $runtime "SPATIALITE_RUNTIME_MANIFEST.json"
$files = Get-ChildItem -LiteralPath $runtime -File | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{
        name = $_.Name
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        bytes = $_.Length
    }
}

$manifest = [pscustomobject]@{
    source_url = $LibSpatialiteUrl
    source_sha256 = $actualSha256
    built_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    build_tool = "MSYS2 mingw64"
    files = $files
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath

if (!$KeepWorkDir) {
    Remove-Item -LiteralPath $work -Recurse -Force
}

Write-Host "完成。Runtime 已輸出到：$runtime"
Write-Host "Manifest：$manifestPath"
