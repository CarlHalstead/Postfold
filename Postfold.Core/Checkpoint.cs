using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record Checkpoint(AccountId Account, string Scope, string Generation, string? Cursor,
    ulong ModSequence, bool Complete);
