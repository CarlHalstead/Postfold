using System;
using System.Net.Mail;

namespace Postfold.Core.Models.Drafts;

public sealed record ComposeIdentity(string Sender)
{
    public bool IsValid => Sender is not null && Sender.Length <= 320 && !Sender.Contains('\r') && !Sender.Contains('\n') &&
        MailAddress.TryCreate(Sender, out MailAddress? address) &&
        string.Equals(address.Address, Sender, StringComparison.OrdinalIgnoreCase);
}
