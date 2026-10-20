using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Authentication;

public sealed class SystemAuthenticationBrowser : ISystemBrowser
{
    public Task<Failure> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            uri.Host is not ("accounts.google.com" or "login.microsoftonline.com"))
        {
            return Task.FromResult(Failure.Validation);
        }

        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            return Task.FromResult(Failure.None);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(Failure.Unavailable);
        }
    }
}
