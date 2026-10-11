using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MailboxSnapshot(
    AccountId Account, FolderId Id, string RemoteKey, string Name, MailboxRole Role,
    bool Selectable, bool Writable, uint UidValidity);
