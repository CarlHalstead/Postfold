namespace Postfold.Core.Models.Accounts;

public sealed record AccountSetupRequest(
    string Name,
    MailEndpoint Incoming,
    MailEndpoint Outgoing,
    string IncomingPassword,
    string OutgoingPassword,
    string? DraftMailbox,
    string? Sender = null
)
{
    public override string ToString()
    {
        return "[account setup]";
    }
}
