# Third-Party Notices

This file summarizes third-party code, packages, and assets distributed with
mySQLPunk release packages. It is not a substitute for the full license text of
each component. Release packages also include available license files under
`THIRD_PARTY_LICENSES/`.

## NuGet Packages

mySQLPunk uses NuGet packages listed in `mySQLPunk/packages.config`, including:

- Google.Protobuf 3.34.1 - BSD-3-Clause
- Microsoft.Bcl.AsyncInterfaces 10.0.7 - MIT
- Microsoft.Bcl.HashCode 6.0.0 - MIT
- Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2 - MIT
- Microsoft.Extensions.Logging.Abstractions 8.0.3 - MIT
- MySqlConnector 2.3.7 - MIT
- Newtonsoft.Json 13.0.4 - MIT
- Npgsql 8.0.3 - PostgreSQL License
- Oracle.ManagedDataAccess 23.26.200 - Oracle Free Distribution, Hosting, and Use Terms
- Stub.System.Data.SQLite.Core.NetFramework 1.0.119.0 - SQLite/System.Data.SQLite terms
- System.Data.SQLite.Core 1.0.119.0 - SQLite/System.Data.SQLite terms
- Microsoft .NET support libraries from the `System.*` and `Microsoft.*`
  package families - MIT or package-specific Microsoft/.NET notices

Oracle.ManagedDataAccess is redistributed unmodified. The Oracle license file
from the NuGet package must be included with release packages.

System.Data.SQLite states that the main System.Data.SQLite code and
documentation are dedicated to the public domain, with the LINQ SQL generation
directory under MS-PL.

## Native SQLite / SpatiaLite Runtime

The `binary/sqlite3_ext` runtime is used for SQLite/SpatiaLite support. It may
include native components from SQLite, SpatiaLite, GEOS, PROJ, FreeXL, RTTOPO,
libxml2, zlib, zstd, minizip, curl, OpenSSL, libjpeg, libtiff, libwebp,
libiconv, GCC runtime libraries, and MSYS2 MinGW runtime libraries.

Release packages intentionally exclude `sqlite3.exe`, `libreadline8.dll`, and
`libtermcap-0.dll`. The previously bundled SQLite command-line shell imported
GNU Readline, which is GPL-3.0-or-later. The application does not need the
SQLite command-line shell to load SpatiaLite.

For compliance-sensitive releases, rebuild the runtime with
`tools/spatialite/Build-SpatiaLiteRuntime.ps1` and include the generated
`SPATIALITE_RUNTIME_MANIFEST.json`.

The current MSYS2 rebuild includes RTTOPO-related runtime dependencies. The
libspatialite configure output treats RTTOPO/GCP-enabled builds as GPLv2+
compatible, so releases that ship those files must keep the corresponding
license notices and the runtime manifest with the package.

## Devicon Icons

The database brand icons in `mySQLPunk/image/brand_*.png` are derived from SVG files in the Devicon project:

https://github.com/devicons/devicon

Product names and logos may be trademarks of their respective owners.

The MIT License (MIT)

Copyright (c) 2015 konpa

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## OpenGameArt Progress Runner

`mySQLPunk/image/progress_runner.gif` is derived from OpenGameArt "Cat & Dog -
Free Sprites" by pzUH:

https://opengameart.org/content/cat-dog-free-sprites

The source asset is published under CC0 1.0 Universal:

https://creativecommons.org/publicdomain/zero/1.0/

The release also includes `mySQLPunk/image/progress_runner_LICENSE.txt`.

## mySQLPunk UI Icons

General UI icons under `mySQLPunk/image` are treated as mySQLPunk project
assets and should be distributed under the same project license unless a future
replacement adds a more specific third-party notice.
