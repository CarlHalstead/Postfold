using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record SearchRequest(AccountId? Account, bool Unified, string Terms, FolderId? Folder,
    bool UnreadOnly, int Limit, string? Continuation = null, bool InboxOnly = false);
