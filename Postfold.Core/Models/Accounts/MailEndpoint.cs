using Postfold.Core.Enums;

namespace Postfold.Core.Models.Accounts;

public sealed record MailEndpoint(string Host, int Port, TlsMode Tls, string Username, string CredentialKey);
