using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed class Credential
{
    public Credential(string secret, bool bearerToken)
    {
        Secret = secret;
        BearerToken = bearerToken;
    }

    public string Secret
    {
        get;
    }
    public bool BearerToken
    {
        get;
    }

    public override string ToString()
    {
        return "[credential]";
    }
}
