namespace Postfold.Core.Models.Mailboxes;

public sealed record MembershipChange(string ProviderMessageKey, string CollectionKey, bool Present);
