using Postfold.Core.Enums;

namespace Postfold.Core.Models.Outbox;

public sealed record RecipientEvidence(string Address, RecipientState State, int? ReplyCode);
