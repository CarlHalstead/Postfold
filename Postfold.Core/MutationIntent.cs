using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MutationIntent(OperationId Id, AccountId Account, MessageId Target,
    ProviderIdentity Provider, MutationKind Kind, bool Value, string? Destination, int PayloadVersion = 1);
