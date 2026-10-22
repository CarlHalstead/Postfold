using Postfold.Core.Enums;

namespace Postfold.Core.Models.Accounts;

/// <summary>A retained account can exist even when its initial synchronization fails.</summary>
public sealed record ProviderConnectionResult(
    Failure Failure,
    AccountId? Account = null,
    Failure Synchronisation = Failure.None,
    bool Duplicate = false,
    bool Cancelled = false
);
