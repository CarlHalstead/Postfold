using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Synchronisation;

/// <summary>Reports account sync activity and committed changes; scheduled checks suppress activity presentation, but retain failures.</summary>
public sealed record SynchronisationUpdate(
    AccountId Account,
    bool Running,
    bool HasChanges,
    Failure Failure = Failure.None,
    bool ShowActivity = true
);
