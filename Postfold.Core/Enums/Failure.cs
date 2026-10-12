namespace Postfold.Core.Enums;

public enum Failure
{
    None,
    Validation,
    NotFound,
    NeedsAuthentication,
    NeedsReview,
    Transient,
    Unavailable,
    Corrupt,
    NewerSchema,
    CursorExpired,
    TlsRejected,
    PolicyBlocked,
    CredentialStorageUnavailable,
    Unsupported
}
