using System;
using System.Net.Mail;

namespace Postfold.Core.Models.Accounts;

/// <summary>Retained public-client configuration and verified Gmail profile address; survives local token deletion.</summary>
public sealed record GoogleAccountBinding(
    string ClientId,
    string Address
)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ClientId) && ClientId.Length <= 512 &&
        !ClientId.Contains('\r') && !ClientId.Contains('\n') && !string.IsNullOrWhiteSpace(Address) && Address.Length <= 320 &&
        MailAddress.TryCreate(Address, out MailAddress? parsed) &&
        string.Equals(parsed.Address, Address, StringComparison.OrdinalIgnoreCase);
}
