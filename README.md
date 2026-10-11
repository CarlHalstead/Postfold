# Postfold

Postfold is an open-source email client being developed with .NET and Avalonia, licensed under MPL-2.0.
The current foundation opens a minimal desktop window; email functionality is not yet implemented.
Windows is the first product target, followed by Linux, macOS, Android and iOS.

## Development

Install the .NET SDK version specified in `global.json`. No mobile workloads are needed for the desktop foundation.
From the repository root:

```sh
dotnet restore Postfold.slnx --locked-mode
dotnet build Postfold.slnx --configuration Release --no-restore
dotnet format Postfold.slnx --verify-no-changes --no-restore
dotnet test Postfold.slnx --configuration Release --no-build --no-restore
dotnet run --project Postfold.Desktop --configuration Release --no-build
```

Packages are pinned centrally and each project has a dependency lock file. Dependency changes require review,
an unlocked restore to regenerate locks, advisory checks and the validation commands above.

The application projects sit at the root. Core has no package dependencies; Protocols, Storage, Sync and UI
reference Core only. Desktop composes all five. The single foundation test project verifies these boundaries,
desktop composition, MVVM binding, NUnit/NSubstitute integration and Avalonia headless input.

Validation CI targets Windows x64, Ubuntu x64 and macOS Apple Silicon. Headless tests verify managed controls;
native renderer isolation, secure stores, accessibility and installers require later platform-specific testing.
Linux native startup needs X11 and font libraries; macOS needs a graphical session; Windows needs a desktop session.

## Contributing

Use four spaces, Allman braces, explicit types, traditional object construction, file-scoped namespaces and
explicit System-first usings. Keep UTF-8 without BOM, LF endings and a final newline. Nullable checking,
compiler warnings and the configured built-in style diagnostics fail the build; formatting must also pass.

Review must cover member ordering, one matching top-level type per file, the 120-column guideline, descriptive
names and acronyms, async ownership/cancellation, interface scope and dependency direction. Built-in analyzers
do not fully enforce these conventions. Generated exceptions are limited to generated sources in `obj`.
Use NUnit constraint assertions, `Method_When_Then` names and visible Arrange/Act/Assert sections.

Private plans, agent instructions, skills and local configuration must stay out of source control and artifacts.
Run `pwsh -File eng/Verify-Source.ps1` to check tracked source privacy and text conventions before proposing changes.
