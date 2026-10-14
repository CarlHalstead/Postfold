namespace Postfold.Storage.Models.Persistence;

internal sealed class SyncStateRow
{
    public long AccountId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Generation { get; set; } = string.Empty;
    public string? Cursor { get; set; }
    public string ModSequence { get; set; } = string.Empty;
    public long Complete { get; set; }
    public long LastSuccess { get; set; }
}
