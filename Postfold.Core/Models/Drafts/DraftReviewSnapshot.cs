using Postfold.Core.Models.Messages;
using System;

namespace Postfold.Core.Models.Drafts;

/// <summary>An exact local revision and retained remote observation; CopyId identifies one explicit create decision.</summary>
public sealed record DraftReviewSnapshot(DraftSnapshot Draft, string RemoteVersion, BlobReference RemoteContent, Guid CopyId);
