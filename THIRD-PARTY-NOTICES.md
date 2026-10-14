# Persistence dependency notices

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
