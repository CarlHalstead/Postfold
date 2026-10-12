using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using System;

namespace Postfold.Core.Models.Drafts;

public sealed record DraftSnapshot(
    Guid Id,
    AccountId Account,
    long Revision,
    long BaseRevision,
    long PushedRevision,
    BlobReference Content,
    ProviderIdentity? Remote,
    string? RemoteVersion,
    bool Conflict,
    bool Held
);
