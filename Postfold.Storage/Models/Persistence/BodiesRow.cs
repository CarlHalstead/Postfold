namespace Postfold.Storage.Models.Persistence;

internal sealed class BodiesRow
{
    public long AccountId { get; set; }
    public long MessageId { get; set; }
    public string? BlobId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long LastAccess { get; set; }
}
