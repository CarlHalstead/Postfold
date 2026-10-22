using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Synchronisation;

namespace Postfold.Core.Models.Accounts;

public sealed record AccountConfiguration(
    AccountId Id,
    string Name,
    MailEndpoint? Incoming,
    MailEndpoint? Outgoing,
    Capabilities? Capabilities = null,
    ComposeIdentity? Identity = null,
    string ProviderKind = "Imap",
    MicrosoftAccountBinding? MicrosoftBinding = null,
    GoogleAccountBinding? GoogleBinding = null
)
{
    /// <summary>The retained mailbox address, independent of opaque provider login identities.</summary>
    public string Address => ProviderKind == "Microsoft" ? Incoming?.Username ?? "" :
        GoogleBinding?.Address ?? Identity?.Sender ?? Incoming?.Username ?? "";
}
