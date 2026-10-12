using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using System;

namespace Postfold.Core.Models.Drafts;

public sealed record DraftVersionSnapshot(Guid Draft, AccountId Account, string Key, string Origin, BlobReference Content);
