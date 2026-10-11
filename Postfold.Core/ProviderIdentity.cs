using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record ProviderIdentity(AccountId Account, string Key, ImapIdentity? Imap);
