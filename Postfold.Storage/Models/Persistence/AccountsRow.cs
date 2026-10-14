namespace Postfold.Storage.Models.Persistence;

internal sealed class AccountsRow
{
    public long AccountId { get; set; }
    public string ProviderKind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
    public string Capabilities { get; set; } = string.Empty;
    public string CredentialReference { get; set; } = string.Empty;
    public long Active { get; set; }
}
