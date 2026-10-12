using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Postfold.UI;

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
    private bool _failed;
    private string _imageOrigin = "https://postfold.invalid/unselected/";

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
        _core.ProcessFailed += ProcessFailed;
        _core.WebMessageReceived += WebMessageReceived;
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
    public int ServedImages
    {
        get; private set;
    }
    public int DeniedPermissions
    {
        get; private set;
    }
    internal int RetainedResponses => _responses.Count;
    internal long RetainedResponseBytes => _responseBytes;
    internal int ReceivedWebMessages
    {
        get; private set;
    }
    internal event Action<string>? DenialObserved;
    public bool ScriptsDisabled => !_core.Settings.IsScriptEnabled;
    public bool BridgesDisabled => !_core.Settings.IsWebMessageEnabled && !_core.Settings.AreHostObjectsAllowed;
    public bool HasFailed => _failed;
    internal event Action? NativeFailed;
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
        if (_failed)
        {
            throw new IOException("Native reader is unavailable after process failure.");
        }
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

    public async Task<bool> NavigateDocumentAsync(string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _navigation?.TrySetCanceled();
        TaskCompletionSource<bool> navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = navigation;
        NavigateDocument(html);
        try
        {
            return await navigation.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_navigation, navigation) && !_disposed)
            {
                BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            }

            throw;
        }
    }

    internal Task<string> InspectSyntheticScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.ExecuteScriptAsync(script);
    }

    internal Task ApplyAppearanceAsync(string family, double size, string foreground, string background,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed)
        {
            throw new IOException("Native reader is unavailable after process failure.");
        }

        string script = "document.body.style.fontFamily=" + JsonSerializer.Serialize(family) +
            ";document.body.style.fontSize=" + JsonSerializer.Serialize(size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px") +
            ";document.body.style.color=" + JsonSerializer.Serialize(foreground) +
            ";document.body.style.backgroundColor=" + JsonSerializer.Serialize(background) +
            ";document.documentElement.style.scrollBehavior='auto';";
        // Trusted host CSSOM assignments; message scripting and CSS remain disabled by native settings and CSP.
        return _core.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
    }

    public void BeginDocument(Guid context, IReadOnlyDictionary<string, byte[]> resources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (resources.Count > 32)
        {
            throw new ArgumentException("Too many document resources.", nameof(resources));
        }

        if (!_failed)
        {
            _core.Stop();
        }
        _navigation?.TrySetCanceled();
        _navigationId = null;
        foreach (MemoryStream response in _responses)
        {
            response.Dispose();
        }

        _responses.Clear();
        _responseBytes = 0;
        _resources.Clear();
        _imageOrigin = "https://postfold.invalid/" + context.ToString("N") + "/";
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
        try
        {
            if (!_failed)
            {
                _core.Stop();
            }
            _core.WebResourceRequested -= ResourceRequested;
            _core.NavigationStarting -= NavigationStarting;
            _core.FrameNavigationStarting -= FrameNavigationStarting;
            _core.NavigationCompleted -= NavigationCompleted;
            _core.NewWindowRequested -= NewWindowRequested;
            _core.DownloadStarting -= DownloadStarting;
            _core.PermissionRequested -= PermissionRequested;
            _core.ServerCertificateErrorDetected -= CertificateError;
            _core.ProcessFailed -= ProcessFailed;
            _core.WebMessageReceived -= WebMessageReceived;
        }
        catch (Exception exception) when (_failed && exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A failed browser closes its native controls before managed event cleanup can finish.
        }
        foreach (MemoryStream response in _responses)
        {
            response.Dispose();
        }
        _responses.Clear();
        _responseBytes = 0;
        DenialObserved = null;
        NativeFailed = null;
    }

    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!_disposed && !_failed && !_documentServed && _responses.Count < 128 && _document is not null &&
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
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                "Content-Security-Policy: " + HtmlPolicy.Csp.Replace("img-src 'none'", "img-src " + _imageOrigin,
                    StringComparison.Ordinal) + "\r\n");
            return;
        }

        if (!_disposed && !_failed && _responses.Count < 128 && _resources.TryGetValue(args.Request.Uri, out byte[]? bytes) &&
            string.Equals(args.Request.Method, "GET", StringComparison.Ordinal) &&
            args.ResourceContext == CoreWebView2WebResourceContext.Image && _responseBytes + bytes.Length <= 16777216)
        {
            ServedImages++;
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
        bool permitted = !_disposed && !_failed && _documentPermitted && !args.IsRedirected &&
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
            DenialObserved?.Invoke("navigation");
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
        DenialObserved?.Invoke("frame");
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        DeniedPopups++;
        DenialObserved?.Invoke("popup");
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        DeniedDownloads++;
        DenialObserved?.Invoke("download");
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        DeniedPermissions++;
        DenialObserved?.Invoke("permission");
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private void CertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.GpuProcessExited or
            CoreWebView2ProcessFailedKind.UtilityProcessExited)
        {
            return;
        }

        _failed = true;
        _documentPermitted = false;
        _documentUri = null;
        _document = null;
        _resources.Clear();
        _navigation?.TrySetResult(false);
        foreach (MemoryStream response in _responses)
        {
            response.Dispose();
        }
        _responses.Clear();
        _responseBytes = 0;
        NativeFailed?.Invoke();
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Count a prohibited effect without reading or retaining an untrusted payload.
        ReceivedWebMessages++;
    }
}
