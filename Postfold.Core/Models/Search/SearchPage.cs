using Postfold.Core.Models.Messages;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Search;

public sealed record SearchPage(ImmutableArray<MessageSnapshot> Messages, string? Continuation);
