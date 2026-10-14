namespace Postfold.Storage.Models.Persistence;

internal sealed class MembershipsRow
{
    public long AccountId { get; set; }
    public long MessageId { get; set; }
    public long FolderId { get; set; }
    public string SeenGeneration { get; set; } = string.Empty;
}
