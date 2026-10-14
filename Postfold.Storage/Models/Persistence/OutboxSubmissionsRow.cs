namespace Postfold.Storage.Models.Persistence;

internal sealed class OutboxSubmissionsRow
{
    public string SubmissionId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public string? SourceDraftId { get; set; }
    public string BlobId { get; set; } = string.Empty;
    public string Frozen { get; set; } = string.Empty;
    public long State { get; set; }
    public long Revision { get; set; }
    public string? Evidence { get; set; }
    public long Created { get; set; }
    public string? Predecessor { get; set; }
}
