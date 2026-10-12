# Postfold

Postfold is an open-source email client being developed with .NET and Avalonia, licensed under MPL-2.0.
The development client provides cached navigation, plain-text reading and durable local drafts/offline commands.
It is not yet qualified for daily use: native HTML, accessibility, recovery UX and desktop integrations remain gated.
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
reference Core only. Desktop composes all five. Foundation and engine tests verify boundaries, cached presentation,
stale selection, offline persistence, local TLS protocols, draft/outbox durability and synthetic headless interaction.

For inspection without credentials or network access:

```powershell
$env:PROCESSOR_ARCHITECTURE = 'AMD64' # Required by the currently qualified local SDK environment
dotnet restore Postfold.slnx --locked-mode
dotnet build Postfold.slnx -c Release --no-restore
dotnet run --project Postfold.Desktop -c Release --no-build -- --demo
```

The labelled demo seeds three synthetic accounts and 360 messages in a separate profile under
`%LOCALAPPDATA%/Postfold/synthetic-demo-v1`. It cannot connect or transmit. `--demo-scale` uses a different
synthetic profile with 100,000 messages; initial seeding takes longer and is not a startup measurement.
The ordinary launch uses `%LOCALAPPDATA%/Postfold/profiles/default`; it does not connect on startup.

Select an account or inbox in the left pane, select cached mail in the centre, and read plain text on the right.
Search scope is the selected navigation entry; choose Unified inbox or All accounts explicitly.
Try read/flag commands, Ctrl+N for a separate compose window, Ctrl+F for local search, local draft autosave,
close/reopen and the Local drafts window. Draft/outbox lists require an account scope. Sending first queues a
frozen snapshot; Process queued outbox is a separate explicit action. Partial/uncertain work is held, and a
linked resend draft requires duplicate-delivery consent. Sent-copy retry does not submit again.
The reviewed sent-copy retry asks you to check the sent folder first, because saving again can duplicate the copy.
F6 and Shift+F6 cycle the visible panes. Message rows show their account name in unified views.
Appearance persists theme, density, font, size and right/bottom/hidden preview layouts. Native accessibility
qualification remains open. Compose supports separately saved plain-text signatures, an attachment list and removal;
Ctrl+S saves, Ctrl+Enter queues, and Ctrl+Shift+A chooses attachments. Attachment saving supports cancellation
through a user destination picker. Mailbox commands shows durable command states and preserved review evidence;
Forward windows let you select original attachments individually; selected content is preserved in the local draft.
Use Check attachments online before forwarding if only the plain-text part is cached and details are missing.
retrying held work requires an explicit review decision. Queued outbox work can be cancelled before submission.
Synchronise reconciles selected-account folders and then processes its durable offline commands.
Notifications contain generic text only. Tray behaviour is opt-in in Desktop settings; default close exits after
saving open compose windows. Native notification, tray and screen-reader qualification remains outstanding.

Manual setup tests independent IMAP/SMTP endpoints with validating TLS; no certificate bypass is offered.
Passwords use the Windows secure store unless Session only is deliberately selected.
Account credentials lets you re-enter independent passwords after restart without creating another account.
Session-only passwords are kept in memory; existing securely stored credentials are not removed by that choice.
Role selection follows discovery; unavailable roles remain unassigned. TLS rejection, authentication,
network policy and unavailable secure storage have distinct diagnostics. Cached mail is not
encrypted by Postfold. HTML/remote images remain disabled until real native isolation qualification passes.
Do not use the development client with a live mailbox until its remaining acceptance work is reviewed.

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
