namespace Postfold.Storage.Models.Persistence;

internal sealed class IdentitiesRow
{
    public long IdentityId { get; set; }
    public long AccountId { get; set; }
    public string Address { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public long IsPrimary { get; set; }
}
