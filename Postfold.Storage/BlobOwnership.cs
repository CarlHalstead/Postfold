using Postfold.Core;

namespace Postfold.Storage;

internal sealed record BlobOwnership(AccountId Account, bool Protected);
