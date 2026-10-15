# Contributing to Postfold

## Project structure

Application and test projects sit directly at the repository root. Do not introduce `src` or `tests` umbrella
directories. Keep project files, dependency locks and essential project metadata at each project root.
Only `Program.cs`, `App.cs` and `AssemblyInfo.cs` may be authored C# files at a project root.

Organise production code by specific role: `Interfaces`, `Models`, `Enums`, `Services`, `Views`, `ViewModels`,
`Converters`, `Controls`, `Builders`, `Policies`, `Adapters`, `Stores`, `Composition`, `Diagnostics` and `Resources`.
Create folders only when populated. Add subject subfolders where they distinguish a coherent group, such as
`Models/Accounts` or `Services/Work`. Avoid production `Helpers`, `Utilities` and generic `Classes` folders.

Namespaces match the project and relative folder path. Each top-level type has its own matching file.
Keep Avalonia views and their code-behind together under `Views`, using `ViewName.axaml` and `ViewName.axaml.cs`.
Code-only views use `ViewName.cs`. View models belong under `ViewModels`. Resource files and their C# wrappers
belong under `Resources`; preserve resource keys when reorganising them.
Private nested helper types may remain inside their sole owning class.

Test classes belong under `Tests/<Subject>`. Supporting types belong under `Fixtures`, `Fakes` or `Helpers`.
Linked test sources retain their originating namespace and use the corresponding folder in their link path.
Generated exceptions are limited to generated sources in `obj`, not whole projects.

Core is framework-only. Protocols, Storage, Sync and UI reference Core only; Desktop composes them.
UI uses Core application ports implemented by Sync. Sync must not reference concrete Storage or Protocols types.
Every injected application service has an interface; keep optional capability ports narrow.
EF Core SQLite owns schema and migrations in Storage; existing parameterised queries remain supported.
Windows publish restores use `-p:RuntimeIdentifier=win-x64 -p:SelfContained=true --locked-mode` and
the separate `packages.win-x64.lock.json` graphs. Ordinary validation uses `packages.lock.json`.
Keep both lock graphs and their LF endings current when changing dependencies.
Do not introduce speculative abstractions, MediatR, event sourcing, a plugin framework or a custom analyzer.

## C# style

Use Allman braces, four spaces, UTF-8 without BOM, LF endings and a final newline. Always brace control flow.
There is no fixed line-length limit. Wrap according to readability rather than a numeric threshold.
When a parameter or argument list wraps, put each item on its own indented line and its closing parenthesis
on a separate line. A multiline lambda or object argument alone does not require wrapping the surrounding list.

Put every enum value on its own line without blank lines between values or a trailing comma. Preserve enum order
and numeric values when changing formatting. Give values explicit numbers only when their contract requires it.
Object initialisers use Allman braces with each property assignment on its own line.

Use explicit types; `var` is allowed only for unnameable types. Use traditional `new Type(...)` construction;
collection expressions are allowed when their target type is explicit. Concise positional records and simple
primary constructors are allowed. Use records for intentional value equality and classes for identity or lifecycle.
Use `required` and `init` for actual immutable contracts.

Use file-scoped namespaces. Explicit using directives appear outside namespaces, sorted alphabetically in one
group, without special priority for `System`. Remove unused imports. Implicit usings are disabled; global usings
require a concrete shared need. Nullable reference checking remains enabled.

Declare accessibility explicitly. Order fields, constructors, properties, events and methods by kind; within each
kind order public, internal, protected and private. Keep relative order within an accessibility group unless another
change is needed. Do not change field-initialisation behaviour merely to reorder declarations.
Use one blank line between members and logical sections, while keeping simple property groups compact.
Do not add blank lines immediately inside braces or use authored `#region` blocks.

Write simple auto-properties on one line, including their braces and accessors:
`public long MessageId { get; set; }`. Apply the same rule to `get; init;`, get-only auto-properties
and auto-properties with initialisers. Properties with accessor block bodies use multiline Allman braces.
Methods and constructors use block bodies. Simple properties and accessors may use expression bodies.
Partial types are limited to generated code or tooling requirements. Prefer guard clauses and early returns.
Use LINQ for readable queries and loops for effects, complex control flow or measured performance needs.
Simple switch expressions, cohesive local functions and small local tuples are allowed.

Private instance fields use `_camelCase`; public members, constants and static readonly fields use `PascalCase`.
Parameters and locals use `camelCase`. Interfaces use an `I` prefix. Async methods end in `Async`.
Treat acronyms as words, such as `Imap` and `Id`. Use descriptive generic parameters where `T` is ambiguous.
Prefer named arguments for ambiguous boolean inputs. Names should describe responsibilities, not implementation
accidents. Preserve established domain terminology and persisted or external names during a styling cleanup.

Document public interfaces and non-obvious contracts with useful XML comments. Other comments explain rationale,
invariants, security or protocol behaviour; avoid restating the code or adding documentation boilerplate.

## XAML style

In multi-step dialogs, put a left-arrow back button beside the title. Use the shared page header,
hide the arrow on the first step, and provide an accessible name and tooltip.

Use the native title-bar X to close dialogs; do not add a duplicate Close button in their content.
Preserve save-before-close and failure recovery. Compose drafts save automatically after a typing pause,
with a concise footer explanation and truthful save status rather than a manual Save draft button.

Give each element its own line. Keep short opening tags together. When attributes wrap, put each attribute on its
own indented line. Use four spaces and retain meaningful attribute values, binding expressions and resource keys.
Update `x:Class`, XML namespace declarations and resource lookups whenever namespaces or paths change.

## Async and failures

`Task` is the default; `ValueTask` requires measurement or a platform contract. Cancellable service APIs have a
final required `CancellationToken`, passed through cancellable work. UI-independent libraries use
`ConfigureAwait(false)` where a context is unnecessary; UI code preserves its dispatcher context.
Background work has an owner, cancellation, completion observation and shutdown participation.
Do not use unobserved fire-and-forget work or synchronous waits on async work.

Expected provider, validation and submission failures use typed outcomes. Exceptions represent unexpected failures
or violated preconditions. Propagate cancellation rather than logging it as an ordinary failure.
Preserve security, error handling, accessibility and durability when refactoring.

## Tests and validation

Use NUnit constraint assertions, `Method_When_Then` names and visible Arrange/Act/Assert sections.
Assert state first and interactions only when essential. Parameterise equivalent inputs; separate distinct behaviours.
Use controlled time and configuration, without sleeps or arbitrary production-default assertions.
Normal tests use real SQLite, local servers or synthetic HTTP fixtures, never live mail credentials.
Native renderers, secure stores, accessibility and installers require appropriate platform checks.

Run the following from the repository root:

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

Compiler warnings and configured built-in style diagnostics fail validation. EditorConfig and `dotnet format`
enforce the supported syntax rules. The separate unused-import check includes IDE0005 at suggestion severity;
build enforcement of that diagnostic would require generated XML documentation. Source verification enforces
project-root placement, privacy and text encoding.
Review covers rules the existing tools do not fully enforce: role selection, matching namespaces and filenames,
member ordering, enum layout, wrapped lists, object initialisers, spacing, XAML layout, naming and documentation.
No analyzer package or custom analyzer is needed for these rules.

Private plans, agent instructions, skills and local configuration must remain outside source control and artifacts.
The EF design-time factory uses an in-memory target. Production profile admission owns migrations, backups and
exclusive access; do not run developer database-update commands against mail profiles. Migration downgrades
and automatic backup restoration are prohibited because queue history may follow irreversible mail effects.
Use short descriptive branch names and scope-free commit titles of the form `type: imperative description`,
approximately 72 characters with no trailing full stop. Allowed types are `feat`, `fix`, `docs`, `refactor`, `perf`,
`test`, `ci`, `build`, `chore`, `style` and `revert`. Use breaking markers only for actual compatibility changes.
Reviewed pull requests use squash merges. Passing validation does not itself authorise a commit, push or release.

## Offline update tooling

`Postfold.UpdateSigner` is an explicitly invoked, framework-only tool. It is not part of the desktop package
and ordinary builds do not run it. The current tooling does not enable application updates or qualify an installer.

```text
Postfold.UpdateSigner manifest document.json artifact-directory encrypted-key.pk8 document.sig
Postfold.UpdateSigner transition transition.json artifact-directory encrypted-old-key.pk8 transition.sig
```

The tool reads a hidden interactive passphrase, never a CLI/environment secret. It accepts bounded encrypted
PKCS#8, checks the exact manifest/feed/full-package bytes and package identity, signs RSA-PSS/SHA-256, and
verifies its output before writing a new detached signature. Existing output is never overwritten. Transition
signing additionally checks `activation.manifest.json`, `activation.manifest.sig` and their exact artifacts.
V1 uses 3072-bit RSA, exponent 65537, SHA-256 MGF1 and a 32-byte salt. Key IDs are SHA-256 of canonical DER
SPKI; client trust must begin at a reviewed public pin. No private key is shipped with source or generated by
this tool. Core's update models and policies define the bounded v1 manifest and transition contract.

Keep production signing offline and separately authorised. Store two encrypted backups on independent offline
media, with passphrase recovery separate. Test restore by comparing the public fingerprint and signing a
synthetic challenge in an isolated environment. Synthetic tests do not establish real media custody. CI must
never receive private update keys. Installer signing is a separate credential and approval boundary.
If a predecessor key is lost or compromised, suspend delivery and use a separately reviewed recovery installer
and trust-distribution process; never accept an unauthenticated replacement pin or reset replay protections.

The signed trust journal and atomic checkpoint must later live outside the installation. Torn or corrupt state
blocks updates; complete authenticated crash suffixes recover forward. Do not delete/recreate these files to
clear an error. An OS principal replacing both files with an older pair is outside this corruption model.
Application binaries and profile migration have separate owners: only admitted application startup may run
shipped forward EF migrations under the exclusive profile lease. Neither binary rollback nor trust recovery
authorises database restoration or stale outbox replay.
