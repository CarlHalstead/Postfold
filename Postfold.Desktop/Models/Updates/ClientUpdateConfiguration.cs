using System;

namespace Postfold.Desktop.Models.Updates;

/// <summary>
/// Explicit immutable release location or reviewed location catalogue, pinned root and admitted profile facts.
/// StateDirectory is owned by the OS user outside installation; qualification supplies an isolated directory.
/// No security identity in this configuration may be learned from unsigned discovery metadata.
/// </summary>
internal sealed record ClientUpdateConfiguration(
    Uri Release,
    string PinnedRoot,
    string Channel,
    string InstalledVersion,
    int ProfileFormat,
    string Migration,
    string StateDirectory
);
