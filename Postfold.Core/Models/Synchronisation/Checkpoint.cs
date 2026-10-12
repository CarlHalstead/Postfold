using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Synchronisation;

public sealed record Checkpoint(
    AccountId Account,
    string Scope,
    string Generation,
    string? Cursor,
    ulong ModSequence,
    bool Complete
);
