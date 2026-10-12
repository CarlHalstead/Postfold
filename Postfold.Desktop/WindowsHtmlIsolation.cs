using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Postfold.Desktop;

/// <summary>Native defence in depth. Creating this helper does not qualify or enable the HTML reader.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHtmlIsolation : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly List<MemoryStream> _responses = [];
    private readonly Dictionary<string, byte[]> _resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private bool _documentPermitted;
    private string? _documentUri;
    private bool _disposed;
    private long _responseBytes;
    private TaskCompletionSource<bool>? _navigation;
    private ulong? _navigationId;
    private byte[]? _document;
    private bool _documentServed;

    public WindowsHtmlIsolation(IWindowsWebView2PlatformHandle handle)
    {
        _core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
        CoreWebView2Settings settings = _core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        _core.WebResourceRequested += ResourceRequested;
        _core.NavigationStarting += NavigationStarting;
        _core.FrameNavigationStarting += FrameNavigationStarting;
        _core.NavigationCompleted += NavigationCompleted;
        _core.NewWindowRequested += NewWindowRequested;
        _core.DownloadStarting += DownloadStarting;
        _core.PermissionRequested += PermissionRequested;
        _core.ServerCertificateErrorDetected += CertificateError;
    }

    public int DeniedResources
    {
        get; private set;
    }
    public int DeniedNavigations
    {
        get; private set;
    }
    public int DeniedPopups
    {
        get; private set;
    }
    public int DeniedDownloads
    {
        get; private set;
    }
    public int StartedNavigations
    {
        get; private set;
    }
    public int ServedDocuments
    {
        get; private set;
    }
    public bool ScriptsDisabled => !_core.Settings.IsScriptEnabled;
    public bool BridgesDisabled => !_core.Settings.IsWebMessageEnabled && !_core.Settings.AreHostObjectsAllowed;
    public string? LastDeniedNavigation
    {
        get; private set;
    }
    public string? LastNavigationError
    {
        get; private set;
    }

    public void NavigateDocument(string html)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Encoding.UTF8.GetByteCount(html) > 262144)
        {
            throw new ArgumentException("Document exceeds the native reader bound.", nameof(html));
        }

        _document = Encoding.UTF8.GetBytes(html);
        _documentServed = false;
        _documentUri = "https://postfold.invalid/document/" + Guid.NewGuid().ToString("N");
        _documentPermitted = true;
        _core.Navigate(_documentUri);
    }

    public Task<bool> NavigateDocumentAsync(string html, CancellationToken cancellationToken)
    {
        _navigation?.TrySetCanceled();
        _navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NavigateDocument(html);
        return _navigation.Task.WaitAsync(cancellationToken);
    }

    internal Task<string> InspectSyntheticScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.ExecuteScriptAsync(script);
    }

    public void BeginDocument(Guid context, IReadOnlyDictionary<string, byte[]> resources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (resources.Count > 32)
        {
            throw new ArgumentException("Too many document resources.", nameof(resources));
        }

        _core.Stop();
        _navigation?.TrySetCanceled();
        _navigationId = null;
        foreach (MemoryStream response in _responses)
        {
            response.Dispose();
        }

        _responses.Clear();
        _responseBytes = 0;
        _resources.Clear();
        long total = 0;
        foreach (KeyValuePair<string, byte[]> resource in resources)
        {
            total += resource.Value.Length;
            if (resource.Key.Length is < 1 or > 64 || !System.Linq.Enumerable.All(resource.Key, char.IsAsciiLetterOrDigit) ||
                resource.Value.Length > 4194304 || total > 16777216)
            {
                throw new ArgumentException("Invalid document resource.", nameof(resources));
            }

            _resources.Add("https://postfold.invalid/" + context.ToString("N") + "/" + resource.Key,
                (byte[])resource.Value.Clone());
        }

        _documentPermitted = false;
        _documentUri = null;
        _document = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigation?.TrySetCanceled();
        _resources.Clear();
        _document = null;
        _core.Stop();
        _core.WebResourceRequested -= ResourceRequested;
        _core.NavigationStarting -= NavigationStarting;
        _core.FrameNavigationStarting -= FrameNavigationStarting;
        _core.NavigationCompleted -= NavigationCompleted;
        _core.NewWindowRequested -= NewWindowRequested;
        _core.DownloadStarting -= DownloadStarting;
        _core.PermissionRequested -= PermissionRequested;
        _core.ServerCertificateErrorDetected -= CertificateError;
        foreach (MemoryStream response in _responses)
        {
            response.Dispose();
        }
    }

    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!_disposed && !_documentServed && _responses.Count < 128 && _document is not null &&
            _responseBytes + _document.Length <= 16777216 &&
            string.Equals(args.Request.Uri, _documentUri, StringComparison.Ordinal) &&
            string.Equals(args.Request.Method, "GET", StringComparison.Ordinal) &&
            args.ResourceContext == CoreWebView2WebResourceContext.Document)
        {
            ServedDocuments++;
            _documentServed = true;
            _responseBytes += _document.Length;
            MemoryStream document = new MemoryStream(_document, false);
            _responses.Add(document);
            args.Response = _core.Environment.CreateWebResourceResponse(document, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
            return;
        }

        if (!_disposed && _responses.Count < 128 && _resources.TryGetValue(args.Request.Uri, out byte[]? bytes) &&
            string.Equals(args.Request.Method, "GET", StringComparison.Ordinal) &&
            args.ResourceContext == CoreWebView2WebResourceContext.Image && _responseBytes + bytes.Length <= 16777216)
        {
            _responseBytes += bytes.Length;
            MemoryStream response = new MemoryStream(bytes, false);
            _responses.Add(response);
            args.Response = _core.Environment.CreateWebResourceResponse(response, 200, "OK",
                "Content-Type: image/png\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
            return;
        }

        DeniedResources++;
        args.Response = _core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked",
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        StartedNavigations++;
        bool permitted = !_disposed && _documentPermitted && !args.IsRedirected &&
            string.Equals(args.Uri, _documentUri, StringComparison.Ordinal);
        args.Cancel = !permitted;
        if (permitted)
        {
            _documentPermitted = false;
            _navigationId = args.NavigationId;
        }
        if (!permitted)
        {
            DeniedNavigations++;
            LastDeniedNavigation = Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? denied) ? denied.Scheme : "invalid";
        }
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_navigationId != args.NavigationId)
        {
            return;
        }

        LastNavigationError = args.WebErrorStatus.ToString();
        _navigation?.TrySetResult(args.IsSuccess);
    }

    private void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = true;
        DeniedNavigations++;
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        DeniedPopups++;
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        DeniedDownloads++;
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private void CertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }
}
