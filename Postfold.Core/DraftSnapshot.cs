using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record DraftSnapshot(Guid Id, AccountId Account, long Revision, long BaseRevision,
    long PushedRevision, BlobReference Content, ProviderIdentity? Remote, string? RemoteVersion,
    bool Conflict, bool Held);
