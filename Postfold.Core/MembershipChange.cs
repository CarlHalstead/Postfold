namespace Postfold.Core;

public sealed record MembershipChange(string ProviderMessageKey, string CollectionKey, bool Present);
