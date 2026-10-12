using Postfold.Core.Models.Accounts;

namespace Postfold.Storage.Models;

internal sealed record BlobOwnership(AccountId Account, bool Protected);
