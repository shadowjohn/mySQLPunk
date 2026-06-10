# SQLite / SpatiaLite Runtime Notices

This directory contains native runtime files used to load SpatiaLite support
from the SQLite provider.

## Provenance

The preferred way to rebuild this runtime is:

```powershell
.\tools\spatialite\Build-SpatiaLiteRuntime.ps1
```

That script builds SpatiaLite from the Gaia-SINS `libspatialite-5.1.0.zip`
source archive and writes `SPATIALITE_RUNTIME_MANIFEST.json` with source and
runtime file hashes. If the manifest is missing, treat the runtime as needing a
rebuild before a compliance-sensitive public release.

## Bundled Components

The runtime may include components from SQLite, SpatiaLite, GEOS, PROJ, FreeXL,
RTTOPO, libxml2, zlib, zstd, minizip, curl, OpenSSL, libjpeg, libtiff, libwebp,
libiconv, GCC runtime libraries, and MSYS2 MinGW runtime libraries.

These components have separate upstream licenses and notices. Release packages
must include the root `THIRD_PARTY_NOTICES.md` and bundled license files under
`THIRD_PARTY_LICENSES/` when available.

## GPL Readline Exclusion

`sqlite3.exe`, `libreadline8.dll`, and `libtermcap-0.dll` are intentionally not
part of the release runtime. The SQLite command-line shell imported GNU
Readline in the previously bundled MSYS2 build, and GNU Readline is licensed
under GPL-3.0-or-later. mySQLPunk does not need the SQLite command-line shell
to load `mod_spatialite.dll`, so the release excludes those files to avoid
unnecessary GPL compatibility risk.
