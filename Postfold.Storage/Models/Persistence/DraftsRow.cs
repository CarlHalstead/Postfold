namespace Postfold.Storage.Models.Persistence;

internal sealed class DraftsRow
{
    public string DraftId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public long? IdentityId { get; set; }
    public long Revision { get; set; }
    public long BaseRevision { get; set; }
    public long PushedRevision { get; set; }
    public string BlobId { get; set; } = string.Empty;
    public string? RemoteIdentity { get; set; }
    public string? RemoteVersion { get; set; }
    public long Conflict { get; set; }
    public long Held { get; set; }
}
