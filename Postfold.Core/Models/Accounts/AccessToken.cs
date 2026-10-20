using System;

namespace Postfold.Core.Models.Accounts;

/// <summary>A transient bearer value bound to a local account and provider identity. Never persist as a password.</summary>
public sealed class AccessToken(AccountId account, string subject, string secret, DateTimeOffset expiresAt, string? tenant = null)
{
    public AccountId Account { get; } = account;
    public string Subject { get; } = subject;
    public string Secret { get; } = secret;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public string? Tenant { get; } = tenant;

    public override string ToString()
    {
        return "[access token]";
    }
}
