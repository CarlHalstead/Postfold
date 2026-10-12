using Postfold.Core.Models.Messages;

namespace Postfold.Core.Models.Drafts;

public sealed record ComposeAttachment(string Name, string MediaType, BlobReference Content);
