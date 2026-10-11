namespace Postfold.Core;

public sealed record EngineLimits(int Workers, int QueueLimit, int RetryAttempts, int MetadataBatch,
    long MaximumMimeBytes, int MaximumMimeParts, int PrefetchDays, int PrefetchBatch, long CacheBudgetBytes);
