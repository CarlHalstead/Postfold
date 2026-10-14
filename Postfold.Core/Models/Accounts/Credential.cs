namespace Postfold.Core.Models.Accounts;

public sealed class Credential
{
    public Credential(string secret, bool bearerToken)
    {
        Secret = secret;
        BearerToken = bearerToken;
    }

    public string Secret { get; }
    public bool BearerToken { get; }

    public override string ToString()
    {
        return "[credential]";
    }
}
