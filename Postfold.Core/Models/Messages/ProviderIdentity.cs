using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Messages;

public sealed record ProviderIdentity(AccountId Account, string Key, ImapIdentity? Imap);
