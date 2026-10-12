using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Messages;

public sealed record ImapIdentity(AccountId Account, string Mailbox, uint UidValidity, uint Uid);
