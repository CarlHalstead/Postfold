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
powershell.exe -NoProfile -File eng/Verify-Source.ps1 -IncludeUntracked
```

Resolve warnings and validation failures. Keep ordinary and Windows RID dependency lock files current when changing dependencies.

## Versions and release tags

Use `major.minor.patch` for stable releases and `major.minor.patch-beta.N` for beta candidates.
Start N at 1 for each target version and increment it for each new candidate. Prefix release
tags with `v`, for example `v0.1.0-beta.1`. Never reuse released versions or move release tags.

Windows draft builds use `eng/Build-UpdateDraft.ps1` from clean, approved source. After owner-local
signing, `eng/New-UpdateBundle.ps1` assembles one ZIP using exact reviewed input hashes and the
public-key-only `Postfold.UpdateSigner verify` command. Run the assembler with Windows PowerShell
5.1; its required parameters bind source, version, audit, public key, verifier and readme bytes.

Before publication, run `eng/Test-InstalledUpdate.ps1` with the candidate draft, a private later-version
package from the same source, a fresh artifacts directory and the exact source commit. It exercises
production directory placement, native Windows cost policy, Settings download/restart, guarded native
apply, normal application reopening and preservation of dirty drafts/queued/uncertain work. Its isolated
package layout suppresses registry/shortcut writes and does not replace installer lifecycle qualification.
Retain the passing receipt bound to the candidate package hash; never publish an update-ineligible candidate.
The qualification host executes the candidate's exact packaged application DLLs and exports its actual
storage migration inventory. Release preparation requires this receipt and inventory, checks their package/
storage hashes, and uses their compatibility fields rather than hard-coded profile formats.

The signed release bundle's audited input directory contains only the full package, Setup, two Velopack indexes, feed,
manifest and signature. Its audit must include all seven exact file lengths and SHA-256 values.
The output includes a separate repository-metadata preparation whose three files exactly match
the ZIP copies. Publish those files at `updates/releases/v<version>/win-x64-beta/` only under
separate release approval, then update the location catalogue last. Versioned paths on `main`
are append-only by release procedure, not immutable hosting. CI remains unsigned and draft-only.
The updater reads metadata during Check and downloads the ZIP only under download policy; it
stages the signed full package and never executes bundled Setup. Existing inactive builds need
one manual bootstrap to the first approved activated ZIP-capable build. Installed Windows builds admit
the embedded native/source inventory after profile admission and start the existing policy-controlled
updater owner. Unpackaged builds remain inactive; invalid update admission reports a local failure while
mail stays usable. Signing, release publication and catalogue activation require separate approval.

Release tooling is reusable: never generate a script, signer project or signer binary per version.
Build `Postfold.UpdateSigner` once as a self-contained Windows tool at
`artifacts/tools/update-signer`, and reuse it until its source/runtime changes. Use
`eng/Invoke-UpdateRelease.ps1` for `Prepare`, `Check`, owner-operated `Sign` and `Bundle`.
`Prepare` consumes the reviewed draft receipt, approved version/source and independently selected
sequence; it generates release data and prints the review SHA-256. Pass that exact review hash to
later actions. All actions support Windows PowerShell 5.1. The signer reads the passphrase privately
without displaying characters. No password is supplied through arguments or environment variables.
Generated manifests, signatures, hashes and receipts are release outputs, never new release-specific code.
Review issued records before selecting a sequence; the beta suffix is not the signing sequence.

Build the reusable signer when its source or pinned runtime changes:

```powershell
dotnet publish Postfold.UpdateSigner -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishAot=false --disable-build-servers -o artifacts/tools/update-signer
```

For each approved beta release, run `Build-UpdateDraft.ps1` with its version/source/channel, then
`Invoke-UpdateRelease.ps1 -Action Prepare` with `-Version`, `-SourceCommit`, `-Sequence` and the reviewed
`-DraftAuditSha256`. Preparation uses the existing draft directory by default. Inspect its generated
`artifacts/update-release/<version>/release-review.json` and retain the printed hash. Use the same
script with `-Action Check`, `Sign` or `Bundle`, passing `-Version` and `-ReviewSha256`. Only the owner
runs `Sign` in a normal interactive Windows console. `Bundle` reuses `New-UpdateBundle.ps1`; it does
not publish. Existing outputs are preserved, and any new attempt requires a fresh output directory.
Signed compatibility must describe the candidate's qualified packaged migration history. Adding migrations
or changing support policy still requires explicit review independently of the application version.

## Submit

Use short, descriptive branch names and commit titles such as `fix: preserve draft on close`. Pull requests should explain the change and validation performed; reviewed changes use squash merges.

Keep credentials, private plans, agent configuration and signing keys out of source control and build artifacts. Commits, pushes, signing and releases require explicit authorisation.
