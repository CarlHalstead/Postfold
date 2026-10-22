using System;

namespace Postfold.Core.Models.Accounts;

/// <summary>A transient bearer value bound to a local account and provider identity. Never persist as a password.</summary>
public sealed class AccessToken(AccountId account, string subject, string secret, DateTimeOffset expiresAt, string? tenant = null, string? resource = null, string? address = null)
{
    public AccountId Account { get; } = account;
    public string Subject { get; } = subject;
    public string Secret { get; } = secret;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public string? Tenant { get; } = tenant;
    public string? Resource { get; } = resource;
    /// <summary>Provider-returned sign-in address; a Microsoft mailbox must still pass protocol verification.</summary>
    public string? Address { get; } = address;

    public override string ToString()
    {
        return "[access token]";
    }
}
