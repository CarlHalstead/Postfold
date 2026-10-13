using Postfold.UI.Resources;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Mail;

namespace Postfold.UI.Models;

/// <summary>Formats cached sender text for display only; routing always uses the original MIME recipients.</summary>
public sealed record SenderPresentation(string Name, string Address, string Initials)
{
    public static SenderPresentation Create(string participants)
    {
        string name = participants.Trim();
        string address = participants.Trim();
        try
        {
            MailAddressCollection mailboxes = new MailAddressCollection();
            if (!string.IsNullOrWhiteSpace(participants))
            {
                mailboxes.Add(participants);
            }
            if (mailboxes.Count == 1)
            {
                MailAddress mailbox = mailboxes[0];
                name = string.IsNullOrWhiteSpace(mailbox.DisplayName) ? mailbox.Address : mailbox.DisplayName;
                address = mailbox.Address;
            }
        }
        catch (FormatException)
        {
            // Malformed and multi-sender headers remain visible verbatim; this presentation never routes replies.
        }

        if (name.Length == 0)
        {
            name = Strings.Get("UnknownSender");
        }

        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string initials = string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word)
            .ToUpper(CultureInfo.CurrentCulture)));
        return new SenderPresentation(name, address, initials);
    }
}
