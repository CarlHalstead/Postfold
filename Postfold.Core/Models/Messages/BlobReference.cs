using Postfold.Core.Models.Accounts;
using System;

namespace Postfold.Core.Models.Messages;

public sealed record BlobReference(Guid Id, AccountId Account, long Length, string Sha256, bool Protected);
