namespace Postfold.Storage.Models.Persistence;

internal sealed class CollectionsRow
{
    public long FolderId { get; set; }
    public long AccountId { get; set; }
    public string RemoteKey { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Role { get; set; }
    public string Kind { get; set; } = string.Empty;
    public long Selectable { get; set; }
    public long Writable { get; set; }
    public long UidValidity { get; set; }
}
