using Postfold.Core.Enums;

namespace Postfold.Core.Models.Messages;

public sealed record AttachmentSnapshot(
    string Part,
    string Name,
    string MediaType,
    long Length,
    ContentAvailability Availability = ContentAvailability.Available
);
