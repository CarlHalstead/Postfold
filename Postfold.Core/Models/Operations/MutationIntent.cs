using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;

namespace Postfold.Core.Models.Operations;

public sealed record MutationIntent(
    OperationId Id,
    AccountId Account,
    MessageId Target,
    ProviderIdentity Provider,
    MutationKind Kind,
    bool Value,
    string? Destination,
    int PayloadVersion = 1
);
