namespace Postfold.Storage.Models.Persistence;

internal sealed class PendingOperationsRow
{
    public string OperationId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public long MessageId { get; set; }
    public long PayloadVersion { get; set; }
    public string Payload { get; set; } = string.Empty;
    public long State { get; set; }
    public long Revision { get; set; }
    public long Attempts { get; set; }
    public long Due { get; set; }
    public long Evidence { get; set; }
}
