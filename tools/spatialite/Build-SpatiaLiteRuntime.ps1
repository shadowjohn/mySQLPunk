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

function Expand-SourceArchive([string]$ArchivePath, [string]$DestinationPath, [string]$ExpectedSourceDir) {
    if (Test-Path -LiteralPath $ExpectedSourceDir) {
        Remove-Item -LiteralPath $ExpectedSourceDir -Recurse -Force
    }

    $tarCandidates = @(
        (Join-Path $env:SystemRoot "System32\tar.exe"),
        (Join-Path $env:windir "System32\tar.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($tar in $tarCandidates) {
        Write-Host "使用 tar.exe 解壓 libspatialite source：$tar"
        & $tar -xf $ArchivePath -C $DestinationPath
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $ExpectedSourceDir)) {
            return
        }
    }

    Write-Host "tar.exe 解壓不可用，改用 PowerShell Expand-Archive。"
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $DestinationPath -Force
}

Expand-SourceArchive -ArchivePath $zipPath -DestinationPath $work -ExpectedSourceDir $srcDir

$msysWork = ConvertTo-MsysPath $work
$msysRuntime = ConvertTo-MsysPath $runtime

$pacman = @"
set -euo pipefail
pacman -S --needed --noconfirm \
  mingw-w64-x86_64-toolchain \
  mingw-w64-x86_64-sqlite3 \
  mingw-w64-x86_64-geos \
  mingw-w64-x86_64-proj \
  mingw-w64-x86_64-libfreexl \
  mingw-w64-x86_64-libxml2 \
  mingw-w64-x86_64-minizip \
  mingw-w64-x86_64-curl \
  mingw-w64-x86_64-librttopo \
  diffutils make unzip
"@

$build = @"
set -euo pipefail
export MSYSTEM=MINGW64
export CHOST=x86_64-w64-mingw32
export PATH=/mingw64/bin:/usr/bin:`$PATH
export CC=x86_64-w64-mingw32-gcc
export CXX=x86_64-w64-mingw32-g++
cd "$msysWork/libspatialite-5.1.0"
./configure --host=x86_64-w64-mingw32 --prefix=/mingw64 --enable-shared --disable-static
sed -i 's/ -ldl//g' src/Makefile
make -C src -j`$(nproc) LDFLAGS=-no-undefined
make -C src install

mkdir -p "$msysRuntime"
candidate=""
for file in /mingw64/lib/mod_spatialite*.dll /mingw64/bin/mod_spatialite*.dll; do
  if [ -f "`$file" ]; then
    cp "`$file" "$msysRuntime/mod_spatialite.dll"
    candidate="$msysRuntime/mod_spatialite.dll"
    break
  fi
done

if [ -z "`$candidate" ]; then
  echo "找不到 mod_spatialite runtime DLL" >&2
  exit 1
fi

for file in /mingw64/bin/libspatialite*.dll /mingw64/lib/libspatialite*.dll; do
  if [ -f "`$file" ]; then
    cp -n "`$file" "$msysRuntime/" || true
  fi
done

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
    if ($LASTEXITCODE -ne 0) {
        throw "MSYS2 pacman dependency installation failed with exit code $LASTEXITCODE."
    }
}

Write-Host "從原始碼編譯 libspatialite 並複製 runtime..."
& $bash -lc $build
if ($LASTEXITCODE -ne 0) {
    throw "MSYS2 libspatialite build failed with exit code $LASTEXITCODE."
}

$manifestPath = Join-Path $runtime "SPATIALITE_RUNTIME_MANIFEST.json"
$blockedRuntimeFiles = @(
    "sqlite3.exe",
    "libreadline8.dll",
    "libtermcap-0.dll"
)

foreach ($blockedRuntimeFile in $blockedRuntimeFiles) {
    $blockedPath = Join-Path $runtime $blockedRuntimeFile
    if (Test-Path -LiteralPath $blockedPath) {
        throw "Runtime 仍包含不應散布的檔案：$blockedRuntimeFile"
    }
}

$files = Get-ChildItem -LiteralPath $runtime -File |
    Where-Object { $_.Name -ne "SPATIALITE_RUNTIME_MANIFEST.json" } |
    Sort-Object Name |
    ForEach-Object {
    [pscustomobject]@{
        name = $_.Name
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        bytes = $_.Length
    }
}

if (($files | Where-Object { $_.name -eq "mod_spatialite.dll" }).Count -eq 0) {
    throw "Runtime 缺少 mod_spatialite.dll，無法載入 SpatiaLite extension。"
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
