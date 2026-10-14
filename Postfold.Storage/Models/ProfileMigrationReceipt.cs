namespace Postfold.Storage.Models;

/// <summary>Durable interruption evidence, never a substitute for EF history.</summary>
internal sealed record ProfileMigrationReceipt(string Target, string? Backup, bool Complete);
