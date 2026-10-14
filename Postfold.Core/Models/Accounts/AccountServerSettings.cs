namespace Postfold.Core.Models.Accounts;

/// <summary>Optional discovered endpoints; null means no compatible configuration was found.</summary>
public sealed record AccountServerSettings(MailEndpoint? Incoming, MailEndpoint? Outgoing);
