# Contributing to Postfold

Use the .NET SDK pinned in `global.json`. See [README.md](README.md) for build and run instructions.

## Conventions

- Keep changes focused, readable and consistent with existing code. Avoid speculative abstractions and unnecessary dependencies.
- Preserve security, accessibility, cancellation and durable mail state.
- Keep projects at the repository root and code in specific role folders. Match namespaces to folders and filenames to types.
- Core stays framework-only. Protocols, Storage, Sync and UI reference Core only; Desktop composes them. Define application service ports in Core.
- Storage owns EF Core SQLite migrations. Never run developer migration commands against mail profiles or restore stale queue history.
- Measure performance changes before and after. Keep optimisations only when they help and correctness checks pass; preserve existing performance gates.

## Code style

Follow `.editorconfig` and these conventions:

- Four spaces, Allman braces, UTF-8 without BOM, LF endings and a final newline. Always brace control flow.
- Explicit types and `new Type(...)`; file-scoped namespaces; explicit, alphabetically sorted usings.
- One item per line in wrapped lists, enums and object initialisers. Keep simple auto-properties on one line.
- Explicit accessibility; order fields, constructors, properties, events and methods.
- `_camelCase` private instance fields, `PascalCase` members, `camelCase` locals and parameters, `I` interfaces and `Async` method suffixes.
- Document public interfaces and non-obvious contracts. Comments should explain why.
- Give XAML elements their own lines and wrapped attributes one line each.

Use required cancellation tokens on cancellable service APIs. Own, cancel and observe background work; avoid fire-and-forget and synchronous waits on async work. Use typed outcomes for expected failures.

## Validate

Test changed behaviour with NUnit, `Method_When_Then` names and clear Arrange/Act/Assert sections. Control time and configuration; avoid sleeps and live mail credentials. Use platform checks for native behaviour.

Run from the repository root:

```sh
dotnet restore Postfold.slnx --locked-mode
dotnet tool restore
dotnet build Postfold.slnx --configuration Release --no-restore
dotnet format Postfold.slnx --verify-no-changes --no-restore
dotnet format style Postfold.slnx --verify-no-changes --no-restore --severity info --diagnostics IDE0005
dotnet ef migrations has-pending-model-changes --project Postfold.Storage --startup-project Postfold.Storage --context ProfileDbContext --configuration Release --no-build
dotnet test Postfold.slnx --configuration Release --no-build --no-restore
pwsh -File eng/Verify-Source.ps1 -IncludeUntracked
```

Resolve warnings and validation failures. Keep ordinary and Windows RID dependency lock files current when changing dependencies.

## Submit

Use short, descriptive branch names and commit titles such as `fix: preserve draft on close`. Pull requests should explain the change and validation performed; reviewed changes use squash merges.

Keep credentials, private plans, agent configuration and signing keys out of source control and build artifacts. Commits, pushes, signing and releases require explicit authorisation.
