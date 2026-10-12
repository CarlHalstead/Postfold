namespace Postfold.Core.Models.Operations;

public sealed record CommandSnapshot(OperationEntry Entry, string Subject);
