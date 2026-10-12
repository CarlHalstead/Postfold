using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Mail;

namespace Postfold.Core;

public static class MailtoActivation
{
    public static Outcome<ComposeContent> Parse(string input)
    {
        if (input.Length > 8192 || !input.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            input.Contains('#') || input.Any(char.IsControl))
        {
            return Outcome<ComposeContent>.Fail(Failure.Validation);
        }

        for (int index = 0; index < input.Length; index++)
        {
            if (input[index] == '%' && (index + 2 >= input.Length || !Uri.IsHexDigit(input[index + 1]) ||
                !Uri.IsHexDigit(input[index + 2])))
            {
                return Outcome<ComposeContent>.Fail(Failure.Validation);
            }
        }

        string[] parts = input[7..].Split('?', 2);
        string addresses = Uri.UnescapeDataString(parts[0]);
        string subject = "";
        string body = "";
        HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parts.Length == 2)
        {
            foreach (string item in parts[1].Split('&'))
            {
                string[] pair = item.Split('=', 2);
                if (pair.Length != 2 || !keys.Add(pair[0]))
                {
                    return Outcome<ComposeContent>.Fail(Failure.Validation);
                }

                string value = Uri.UnescapeDataString(pair[1]);
                switch (pair[0].ToLowerInvariant())
                {
                    case "to":
                        addresses += "," + value;
                        break;
                    case "subject":
                        subject = value;
                        break;
                    case "body":
                        body = value;
                        break;
                    default:
                        return Outcome<ComposeContent>.Fail(Failure.Validation);
                }
            }
        }

        ImmutableArray<string> recipients = addresses.Split(',', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).ToImmutableArray();
        if (recipients.Length > 100 || recipients.Any(address => address.Any(char.IsControl) ||
            !MailAddress.TryCreate(address, out _)) || subject.Any(char.IsControl) ||
            body.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            return Outcome<ComposeContent>.Fail(Failure.Validation);
        }

        return Outcome<ComposeContent>.Success(new ComposeContent("", recipients, subject, body,
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N") + "@postfold.invalid"));
    }
}
