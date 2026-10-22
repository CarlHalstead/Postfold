using Postfold.Core.Enums;
using Postfold.Core.Models.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Owns qualified isolated HTML reading and explicit per-message image consent.</summary>
public interface IHtmlReadingService : IAsyncDisposable
{
    bool IsQualified { get; }

    Task<Failure> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);

    /// <summary>Reveals inline images and fetches remote images only after explicit consent for the selected message.</summary>
    Task<Failure> LoadImagesAsync(MessageSnapshot message, CancellationToken cancellationToken);
}
