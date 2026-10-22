using Postfold.Core.Enums;

namespace Postfold.Core.Models.Accounts;

/// <summary>Availability of an owner-configured public client; a missing registration is never usable.</summary>
public sealed record ProviderAvailability(string ProviderKind, Failure Failure);
