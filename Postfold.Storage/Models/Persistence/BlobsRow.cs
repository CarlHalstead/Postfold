namespace Postfold.Storage.Models.Persistence;

internal sealed class BlobsRow
{
    public string BlobId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long Protected { get; set; }
    public long LastAccess { get; set; }
}
