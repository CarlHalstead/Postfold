using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IHtmlReadingService : IAsyncDisposable
{
    bool IsQualified
    {
        get;
    }
    Task<Failure> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);
    Task<Failure> LoadImagesAsync(MessageSnapshot message, CancellationToken cancellationToken);
}
