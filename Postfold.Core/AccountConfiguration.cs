namespace Postfold.Core;

public sealed record AccountConfiguration(AccountId Id, string Name, MailEndpoint Incoming, MailEndpoint Outgoing,
    Capabilities? Capabilities = null, ComposeIdentity? Identity = null);
