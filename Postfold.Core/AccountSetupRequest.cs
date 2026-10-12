namespace Postfold.Core;

public sealed record AccountSetupRequest(string Name, MailEndpoint Incoming, MailEndpoint Outgoing,
    string IncomingPassword, string OutgoingPassword, bool SessionOnly, string? DraftMailbox)
{
    public override string ToString()
    {
        return "[account setup]";
    }
}
