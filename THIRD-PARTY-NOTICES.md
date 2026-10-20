# Dependency notices

The Windows x64 distribution includes the complete resolved dependency inventory in
`ThirdPartyNotices/inventory.json` and full upstream licence/attribution texts in
`ThirdPartyNotices/managed-licences.txt` and `ThirdPartyNotices/native-helper-licences.txt`.
These files accompany the installed application and full package. Postfold itself is MPL-2.0;
see `POSTFOLD-LICENSE.txt` in the installation.

The inventory identifies exact resolved NuGet versions, package hashes, upstream source revisions where
provided, and files selected by the Windows publish. It retains non-shipping metapackages, build-only
dependencies and other-platform native assets as explicitly distinguishable graph entries.
The .NET 10.0.13 self-contained runtime's complete notices, Skia's embedded third-party notices,
HarfBuzz notices, ANGLE's native licence and WebView2 SDK terms are included verbatim with LF line endings.
The separately installed Windows WebView2 browser runtime is an OS prerequisite; it is not bundled.

| Shipping family | Resolved version | Licence |
|---|---|---|
| Avalonia desktop, Fluent, platform and rendering assemblies | 12.1.3 | MIT |
| Avalonia.Controls.WebView | 12.1.0 | MIT |
| Avalonia ANGLE native library | 2.1.27548.20260419 | BSD-3-Clause, upstream composite licence |
| AngleSharp / AngleSharp.Css | 1.7.2 / 1.0.2 | MIT |
| HtmlSanitizer | 9.2.1039 | MIT |
| MailKit / MimeKit | 4.18.1 | MIT |
| Microsoft.Identity.Client / Microsoft.IdentityModel.Abstractions | 4.91.0 / 8.14.0 | MIT; Microsoft Corporation |
| BouncyCastle.Cryptography | 2.7.0 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| MicroCom.Runtime | 0.11.6 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| SkiaSharp and Win32 native assets | 3.119.4 | MIT wrapper; embedded Skia/dependency notices included |
| HarfBuzzSharp and Win32 native assets | 8.3.1.3 | MIT wrapper; embedded HarfBuzz/dependency notices included |
| Microsoft.Web.WebView2 SDK | 1.0.4258.31 | Microsoft SDK terms and NOTICE included |
| System.Security.Cryptography.Pkcs / ProtectedData | 10.0.13 / 10.0.12 | MIT |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | MIT and included third-party notices |

Persistence components:

| Components | Version | Licence / attribution |
|---|---|---|
| Microsoft.EntityFrameworkCore, Abstractions, Relational, Sqlite, Sqlite.Core | 10.0.13 | MIT; © Microsoft Corporation. All rights reserved. |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.13 | MIT; © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions caching, configuration abstractions, dependency injection, dependency model, logging, options and primitives | 10.0.13 | MIT; © Microsoft Corporation. All rights reserved. |
| SQLitePCLRaw.bundle_e_sqlite3, core, provider.e_sqlite3, lib.e_sqlite3 | 2.1.13 | Apache-2.0; Copyright 2014–2024 SourceGear, LLC; author Eric Sink |
| Embedded SQLite | 3.53.3 | SQLite's public-domain dedication; the wrapper package retains its Apache-2.0 licence |

Upstream licence texts: [Microsoft MIT](https://github.com/dotnet/efcore/blob/v10.0.13/LICENSE.txt),
[SQLitePCLRaw Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT),
[SQLite dedication](https://www.sqlite.org/copyright.html).

Microsoft.EntityFrameworkCore.Design and repository-local dotnet-ef 10.0.13 are MIT development tools.
Design and its compiler/templating dependencies are private build dependencies and are excluded from the
desktop publish graph. They are not needed to migrate profiles at application startup.

## Update tooling

Velopack 1.2.161 and the development-only vpk 1.2.161 use the MIT licence, source revision
`92d6a1c91716729d449034df5c50307dcce39493`. The managed net10.0 package has no transitive packages.
The matched setup/stub/update native binaries are supplied by vpk 1.2.161. The native licence file contains
the complete resolved Cargo.lock closure of `velopack_bins` at that revision, including original crate
licences and attributions. This deliberately conservative licence-coverage superset also includes
development/build and non-Windows dependencies; it does not assert that every crate is linked in Windows.
MIT/Apache-2.0 alternatives are included; applicable embedded native notices are retained.
vpk, signtool, zstd.exe, WiX, AppImage helpers, compiler tooling, EF design-time tooling and the offline
Postfold.UpdateSigner are build tools and are not shipped. The native update helper is present for the
installer lifecycle; Postfold's update discovery/download/application remain inactive.

Copyright © 2021 Caelan Sayler
Copyright © 2024 Velopack Ltd.

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
