using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Synchronisation;

/// <summary>Reports account sync activity and whether committed local mail should be queried again.</summary>
public sealed record SynchronisationUpdate(AccountId Account, bool Running, bool HasChanges, Failure Failure = Failure.None);
