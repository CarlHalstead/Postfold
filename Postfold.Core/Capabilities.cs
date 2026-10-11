using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record Capabilities(bool CondStore, bool QuickResync, bool Idle, bool NativeMove,
    bool SelectiveExpunge, bool RemoteDrafts);
