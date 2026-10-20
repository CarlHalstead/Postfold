using Postfold.Core.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Opens an authentication URL in the external system browser, never the mail renderer.</summary>
public interface ISystemBrowser
{
    Task<Failure> OpenAsync(Uri uri, CancellationToken cancellationToken);
}
