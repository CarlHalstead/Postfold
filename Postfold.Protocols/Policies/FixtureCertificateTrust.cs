using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Postfold.Protocols.Policies;

// Internal test seam: a private fixture CA still requires a valid chain and matching endpoint hostname.
internal static class FixtureCertificateTrust
{
    internal static bool Validate(X509Certificate2 authority, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using X509Certificate2 leaf = new X509Certificate2(certificate);
        using X509Chain chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }
}
