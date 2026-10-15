# Dependency notices

Postfold's EF Core SQLite persistence uses the following upstream components. This document covers persistence;
the complete application and native distribution inventory must also accompany any authorised release.

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
The native installer/updater and packaging helper inventory must additionally accompany a distribution;
the vpk vendor notice alone is not a complete application distribution inventory.

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
