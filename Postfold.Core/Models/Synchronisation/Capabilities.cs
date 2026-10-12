namespace Postfold.Core.Models.Synchronisation;

public sealed record Capabilities(
    bool CondStore,
    bool QuickResync,
    bool Idle,
    bool NativeMove,
    bool SelectiveExpunge,
    bool RemoteDrafts
);
