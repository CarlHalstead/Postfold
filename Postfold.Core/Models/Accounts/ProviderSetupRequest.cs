namespace Postfold.Core.Models.Accounts;

/// <summary>An explicit browser connection. An omitted address uses the provider's returned mailbox; Microsoft verifies it over TLS. When Synchronise is false, the caller owns subsequent initial sync.</summary>
public sealed record ProviderSetupRequest(
    string ProviderKind,
    string Address = "",
    MailEndpoint? Incoming = null,
    MailEndpoint? Outgoing = null,
    bool Synchronise = true
);
