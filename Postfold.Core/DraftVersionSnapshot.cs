using System;

namespace Postfold.Core;

public sealed record DraftVersionSnapshot(Guid Draft, AccountId Account, string Key, string Origin, BlobReference Content);
