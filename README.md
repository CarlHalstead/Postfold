# Postfold

Postfold is an open-source email client being developed with .NET and Avalonia, licensed under MPL-2.0.
The development client provides cached navigation, plain-text reading and durable local drafts/offline commands.
The private Windows generic client has synthetic/local-server and native qualification evidence. It is a development
build; provider OAuth, authenticated updates and release packaging belong to later stages.
Windows is the first product target, followed by Linux, macOS, Android and iOS.

## Development

Install the latest stable .NET 10 SDK (10.0.402 or newer). The root `global.json` applies to every project and solution in this repository and selects the latest installed .NET 10.0 feature band and patch; prereleases are excluded. CI installs the latest stable .NET 10.0 SDK. No mobile workloads are needed for the desktop foundation.
From the repository root:

```sh
dotnet restore Postfold.slnx --locked-mode
dotnet build Postfold.slnx --configuration Release --no-restore
dotnet format Postfold.slnx --verify-no-changes --no-restore
dotnet format style Postfold.slnx --verify-no-changes --no-restore --severity info --diagnostics IDE0005
dotnet test Postfold.slnx --configuration Release --no-build --no-restore
dotnet run --project Postfold.Desktop --configuration Release --no-build
```

Packages are pinned centrally and each project has a dependency lock file. Dependency changes require review,
an unlocked restore to regenerate locks, advisory checks and the validation commands above.

If a local profile cannot be opened, Postfold explains the failure and offers **Start fresh**. This creates a
separate empty profile and remembers it for future launches, preserving the previous accounts, cache, drafts
and queued sends in their original folder. Older profiles without EF migration history currently require
separate recovery; they are never reset automatically. An explicit `--profile` launch argument overrides the
remembered selection. Settings remain available while a profile is blocked; appearance changes then apply
only to the current session. **Open profiles folder** opens the containing folder in the OS-native file explorer.

The application projects sit at the root. Core has no package dependencies; Protocols, Storage, Sync and UI
reference Core only. Desktop composes all five. Foundation and engine tests verify boundaries, cached presentation,
stale selection, offline persistence, local TLS protocols, draft/outbox durability and synthetic headless interaction.

For inspection without credentials or network access:

```powershell
$env:PROCESSOR_ARCHITECTURE = 'AMD64' # Required by the currently qualified local SDK environment
dotnet restore Postfold.slnx --locked-mode
dotnet build Postfold.slnx -c Release --no-restore
dotnet run --project Postfold.Foundation.Tests -c Release --no-build -- --demo
```

The labelled demo seeds three synthetic accounts and 360 messages in a separate profile under
`%LOCALAPPDATA%/Postfold/synthetic-demo-v1`. It cannot connect or transmit. `--demo-scale` uses a different
synthetic profile with 100,000 messages; initial seeding takes longer and is not a startup measurement.
Demo and qualification code lives in the test project and is excluded from the desktop application.
The ordinary launch uses `%LOCALAPPDATA%/Postfold/profiles/default`, opens dismissible setup when no accounts
exist, and refreshes configured accounts after showing local mail.
To open a separate existing profile, launch the normal app with `--profile "C:/absolute/profile-directory"`
before any `mailto:` activation. The directory must already exist and the path must be absolute.
That profile supplies its own mail, credentials and preferences; normal account synchronisation still applies.

Select an account or inbox in the left pane, select cached mail in the centre, and read plain text on the right.
Search scope is the selected navigation entry; choose Unified inbox or All accounts explicitly.
Try read/flag commands, Ctrl+N for a separate compose window, Ctrl+F for local search, local draft autosave,
close/reopen and the Local drafts window. Draft/outbox lists require an account scope. Sending first queues a
frozen snapshot; Process queued outbox is a separate explicit action. Partial/uncertain work is held, and a
linked resend draft requires duplicate-delivery consent. Sent-copy retry does not submit again.
The reviewed sent-copy retry asks you to check the sent folder first, because saving again can duplicate the copy.
F6 and Shift+F6 cycle the visible panes. Message rows show their account name in unified views.
Appearance persists theme, density, font, size and right/bottom/hidden preview layouts. Windows owner checks cover
Narrator, pane focus, scaling, contrast and reduced motion. Compose supports plain text, attachments and removal;
Ctrl+S saves, Ctrl+Enter queues, and Ctrl+Shift+A chooses attachments. Attachment saving supports cancellation
through a user destination picker. Mailbox commands shows durable command states and preserved review evidence;
Forward windows let you select original attachments individually; selected content is preserved in the local draft.
Use Check attachments online before forwarding if only the plain-text part is cached and details are missing.
retrying held work requires an explicit review decision. Queued outbox work can be cancelled before submission.
Synchronise reconciles selected-account folders and then processes its durable offline commands.
Notifications contain generic text only. Tray behaviour is opt-in in Desktop settings; default close exits after
saving open compose windows. Windows synthetic native notification, tray and screen-reader checks have passed.

Manual setup tests independent IMAP/SMTP endpoints with validating TLS; no certificate bypass is offered.
Passwords use the Windows secure store unless Session only is deliberately selected.
Account credentials lets you re-enter independent passwords after restart without creating another account.
Session-only passwords are kept in memory; existing securely stored credentials are not removed by that choice.
Role selection follows discovery; unavailable roles remain unassigned. TLS rejection, authentication,
network policy and unavailable secure storage have distinct diagnostics. Cached mail is not
encrypted by Postfold. Windows HTML uses parser sanitisation, CSP, an isolated native profile and native network
blocking. Remote images require per-message consent and use the bounded application fetcher. Reader failure
returns to plain text and requires an application restart before HTML can be used again.
Images currently support PNG; other formats are blocked. HTML links remain inert.

Validation CI targets Windows x64, Ubuntu x64 and macOS Apple Silicon. Headless tests verify managed controls;
native renderer isolation, secure stores and accessibility require tests on the actual platform for this stage.
Installer qualification belongs to the later packaging stage.
Linux native startup needs X11 and font libraries; macOS needs a graphical session; Windows needs a desktop session.

Windows development qualification uses separate synthetic profiles and never reads real mail:

```powershell
dotnet .\Postfold.Foundation.Tests\bin\Release\net10.0\Postfold.Foundation.Tests.dll --qualify-client C:/path/to/private/client-observations.json
dotnet .\Postfold.Foundation.Tests\bin\Release\net10.0\Postfold.Foundation.Tests.dll --qualify-renderer C:/path/to/private/renderer-results.json
dotnet .\Postfold.Foundation.Tests\bin\Release\net10.0\Postfold.Foundation.Tests.dll --qualify-reader C:/path/to/private/reader-results.json
dotnet .\Postfold.Foundation.Tests\bin\Release\net10.0\Postfold.Foundation.Tests.dll --measure-client C:/path/to/private/client-performance.json
```

Use an existing writable private directory for the output paths. Client qualification opens controls for authored
setup responses, a received attachment, generic notifications, internal mailto activation and draft-save failure
injection. Its profile is `%LOCALAPPDATA%/Postfold/synthetic-qualification-v1`, with networking disabled.
Only explicitly checked observations are recorded. Authored HTML is available in this qualification profile.
The renderer harness records denial, context, resource-budget and lifecycle cases. The separate reader harness
uses in-process authored HTTP responses through the production fetch pipeline and measures the actual reader
without preceding hostile-fixture allocations. It terminates only its own isolated WebView browser to exercise
failure handling. Both harnesses close automatically. No external browser or real profile is used. A saved report
does not itself establish full qualification.
Performance uses three accounts and 100k synthetic messages, the actual native rendering loop, a 60-second idle
observation and application process-tree memory. Record cold/warm cache conditions and actual hardware; no fixed
CPU, RAM or storage specification is required. These diagnostic modes do not install or register anything.
`--measure-client-auto` records the same samples and exits automatically for repeatable development runs.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the complete project structure, styling and validation standards.
Use role folders, four spaces, Allman braces, explicit types, traditional object construction, file-scoped namespaces
and alphabetical usings. Keep UTF-8 without BOM, LF endings and a final newline. Nullable checking,
compiler warnings and the configured built-in style diagnostics fail the build; formatting must also pass.

Review must cover member ordering, one matching top-level type per file, readable wrapping, descriptive
names and acronyms, async ownership/cancellation, interface scope and dependency direction. Built-in analyzers
do not fully enforce these conventions. Generated exceptions are limited to generated sources in `obj`.
Use NUnit constraint assertions, `Method_When_Then` names and visible Arrange/Act/Assert sections.

Private plans, agent instructions, skills and local configuration must stay out of source control and artifacts.
Run `pwsh -File eng/Verify-Source.ps1` to check tracked source privacy and text conventions before proposing changes.
