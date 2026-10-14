namespace Postfold.Storage.Models.Persistence;

internal sealed class ImapInstancesRow
{
    public long AccountId { get; set; }
    public long FolderId { get; set; }
    public long UidValidity { get; set; }
    public long Uid { get; set; }
    public long MessageId { get; set; }
    public string? ModSeq { get; set; }
}
