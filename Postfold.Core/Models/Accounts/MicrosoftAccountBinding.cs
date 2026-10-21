using System;

namespace Postfold.Core.Models.Accounts;

/// <summary>Retained non-secret public-client configuration, MSAL home identity and Exchange resource tenant. Local credential deletion never removes this binding.</summary>
public sealed record MicrosoftAccountBinding(string ClientId, string HomeAccountId, string TenantId)
{
    public bool IsValid => Guid.TryParse(ClientId, out _) && Guid.TryParse(TenantId, out _) &&
        !string.IsNullOrWhiteSpace(HomeAccountId) && HomeAccountId.Length <= 512;
}
