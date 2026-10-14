namespace Postfold.Storage.Models.Persistence;

internal sealed class AttachmentsRow
{
    public long AttachmentId { get; set; }
    public long AccountId { get; set; }
    public long MessageId { get; set; }
    public string PartKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? BlobId { get; set; }
}
