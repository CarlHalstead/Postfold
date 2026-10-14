namespace Postfold.Storage.Models.Persistence;

internal sealed class DraftVersionsRow
{
    public string DraftId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public string VersionKey { get; set; } = string.Empty;
    public string BlobId { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public long Created { get; set; }
}
