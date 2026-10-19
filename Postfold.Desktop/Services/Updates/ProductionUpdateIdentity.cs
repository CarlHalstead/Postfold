using Postfold.Core.Policies.Updates;
using System.Security.Cryptography;

namespace Postfold.Desktop.Services.Updates;

/// <summary>Reviewed public identity only. Referencing it creates no updater, transport or state.</summary>
internal static class ProductionUpdateIdentity
{
    internal const string Channel = "beta";
    internal const string Discovery = "https://raw.githubusercontent.com/CarlHalstead/Postfold/main/updates/win-x64-beta.json";
    internal const string KeyId = "A022E28E638426A0901C393CE163EF06246066CB5E3496B6DCF4965560E4CFE4";
    internal const string PublicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAw9XFEc85ZiQiIIUxTEY26PSdfgCcFhn9ENbJf0D8xRKEBGmHOom2KpaOL0rGE1/55sW+F1otdtfE+80MzRSra6VfR/9hxFW/AbsoTas+MvHiw72yVPgZ3cW9EfdEp82Ccs+b34uflqzG537Uz9Y2imhVR7TxDnRnocpMOOxThVCAWuoVTy8NvECk0bcDETe/zuKY0gAZsMIL2DZjcJOtOJ+8AQGYz6IZH4fVYE3RVmk2b2uT7Muii4k5yp0o3H3YO8GHOJemDDmpTMLNngltcEofWczZyoWx9APTNWeofHkMdkbz46mmmi+c215HgLJ6AGPe3ZjEp1hj2IXqnmPA8qWeyc6wCxWQw1Z+SQBnT55FN/N7SV1h/R8uksSLQklUrnVVykhIvuCOMHMszoYFnKDryHhjo7llur7U/LE1FerT/G2C8p4J2wG7xFZt5nQAC3pWbdPIBTDC0zK6kWMsfqXCFgP0FRH+Cz/9vvJlVpCBBkzzM8nexEqnhCPDsvmVAgMBAAE=";

    internal static void Validate()
    {
        if (UpdateTrust.KeyId(PublicKey) != KeyId)
        {
            throw new CryptographicException("Production update identity mismatch.");
        }
    }
}
