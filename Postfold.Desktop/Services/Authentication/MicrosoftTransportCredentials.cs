using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Authentication;

/// <summary>Silent Exchange credential resolution for exactly one retained account and its explicit endpoints.</summary>
internal sealed class MicrosoftTransportCredentials(
    AccountConfiguration configuration,
    IAccountAuthenticator? authenticator,
    TimeProvider time
) : ITransportCredentials
{
    public async Task<Outcome<Credential>> ResolveAsync(AccountId account, MailEndpoint endpoint, CancellationToken cancellationToken)
    {
        MicrosoftAccountBinding? binding = configuration.MicrosoftBinding;
        if (account != configuration.Id || endpoint != configuration.Incoming && endpoint != configuration.Outgoing ||
            configuration.ProviderKind != "Microsoft" || binding is null || !binding.IsValid)
        {
            return Outcome<Credential>.Fail(Failure.NeedsReview);
        }

        if (authenticator is null)
        {
            return Outcome<Credential>.Fail(Failure.CredentialStorageUnavailable);
        }

        Outcome<AccessToken> acquired = await authenticator.AcquireAsync(false, cancellationToken).ConfigureAwait(false);
        if (!acquired.IsSuccess)
        {
            return Outcome<Credential>.Fail(acquired.Failure);
        }

        AccessToken token = acquired.Value!;
        if (token.Account != account || token.Subject != binding.HomeAccountId || token.Tenant != binding.TenantId ||
            token.Resource != "https://outlook.office.com" || token.ExpiresAt <= time.GetUtcNow() || string.IsNullOrWhiteSpace(token.Secret))
        {
            return Outcome<Credential>.Fail(Failure.NeedsReview);
        }

        return Outcome<Credential>.Success(new Credential(token.Secret, true));
    }
}
