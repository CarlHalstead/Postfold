using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public enum Failure
{
    None, Validation, NotFound, NeedsAuthentication, NeedsReview, Transient, Unavailable, Corrupt, NewerSchema, CursorExpired,
    TlsRejected, PolicyBlocked, CredentialStorageUnavailable, Unsupported
}
