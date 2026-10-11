using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MailEndpoint(string Host, int Port, TlsMode Tls, string Username, string CredentialKey);
