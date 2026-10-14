namespace Postfold.Storage.Models.Persistence;

internal sealed class SubmissionRecipientsRow
{
    public string SubmissionId { get; set; } = string.Empty;
    public long Ordinal { get; set; }
    public string Address { get; set; } = string.Empty;
    public long State { get; set; }
    public long? ReplyCode { get; set; }
}
