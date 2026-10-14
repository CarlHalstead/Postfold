namespace Postfold.Storage.Models.Persistence;

internal sealed class MessagesRow
{
    public long MessageId { get; set; }
    public long AccountId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string? MessageIdHeader { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Participants { get; set; } = string.Empty;
    public long Date { get; set; }
    public long Size { get; set; }
    public long IsRead { get; set; }
    public long Flagged { get; set; }
    public long Availability { get; set; }
    public long Pending { get; set; }
    public string? NativeThreadId { get; set; }
}
