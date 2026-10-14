namespace Postfold.Storage.Models.Persistence;

internal sealed class PreferencesRow
{
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public long PayloadVersion { get; set; }
}
