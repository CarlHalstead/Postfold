using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record ImapIdentity(AccountId Account, string Mailbox, uint UidValidity, uint Uid);
