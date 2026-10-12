using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Postfold.Core;
using Postfold.UI;

namespace Postfold.Desktop;

/// <summary>Lazy Windows reading surface. Production composition keeps it disabled until native qualification passes.</summary>
public sealed class WindowsHtmlReadingService : IHtmlReadingService
{
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _model;
    private readonly IMailApplication _application;
    private readonly IRemoteImageFetcher _fetcher;
    private readonly string _directory;
    private readonly bool _qualified;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly List<Task> _work = [];
    private readonly SemaphoreSlim _images = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _reads = new SemaphoreSlim(1, 1);
    private TaskCompletionSource<IWindowsWebView2PlatformHandle>? _ready;
    private CancellationTokenSource? _selection;
    private NativeWebView? _view;
    private WindowsHtmlIsolation? _isolation;
    private MessageSnapshot? _message;
    private Guid _context;
    private HtmlResourceDocument? _document;
    private bool _disposed;

    public WindowsHtmlReadingService(MainWindow window, IMailApplication application, IRemoteImageFetcher fetcher,
        string directory, bool qualified)
    {
        _window = window;
        _model = window.DataContext as MainWindowViewModel ?? throw new ArgumentException("Missing presentation model.", nameof(window));
        _application = application;
        _fetcher = fetcher;
        _directory = directory;
        _qualified = qualified;
        _model.PropertyChanged += SelectedChanged;
    }

    public bool IsQualified => _qualified && OperatingSystem.IsWindows();

    public Task<Failure> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken)
    {
        Task<Failure> task = ReadCoreAsync(message, retrieve, cancellationToken);
        Track(task);
        return task;
    }

    public Task<Failure> LoadImagesAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        Task<Failure> task = LoadImagesCoreAsync(message, cancellationToken);
        Track(task);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _model.PropertyChanged -= SelectedChanged;
        await _lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(_work.ToArray());
        }
        catch (OperationCanceledException)
        {
        }

        if (OperatingSystem.IsWindows())
        {
            _isolation?.Dispose();
        }

        _window.ReleaseHtmlSurface();
        _selection?.Dispose();
        _lifetime.Dispose();
        _images.Dispose();
        _reads.Dispose();
    }

    private async Task<Failure> ReadCoreAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken)
    {
        if (_disposed || !IsQualified || !OperatingSystem.IsWindows())
        {
            return Failure.Unavailable;
        }

        if (_model.Selected?.Account != message.Account || _model.Selected.Id != message.Id ||
            _model.Selected.Provider != message.Provider)
        {
            return Failure.NeedsReview;
        }

        Reset();
        _message = message;
        _context = Guid.NewGuid();
        Guid context = _context;
        _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_selection.Token, cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        bool entered = false;
        try
        {
            await _reads.WaitAsync(deadline.Token);
            entered = true;
            Outcome<MimeHtmlDocument> content = await _application.ReadHtmlAsync(message, retrieve, deadline.Token);
            if (!content.IsSuccess)
            {
                return content.Failure;
            }

            Outcome<HtmlResourceDocument> prepared = await Task.Run(() => Prepare(content.Value!, context), deadline.Token);
            if (!prepared.IsSuccess)
            {
                return prepared.Failure;
            }

            deadline.Token.ThrowIfCancellationRequested();
            if (!Current(message, context))
            {
                return Failure.NeedsReview;
            }

            _document = prepared.Value!;
            await EnsureNativeAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!Current(message, context))
            {
                return Failure.NeedsReview;
            }

            return await RenderAsync(context, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return Failure.Unavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            WebView2RuntimeNotFoundException or DllNotFoundException)
        {
            return Failure.Unavailable;
        }
        finally
        {
            if (entered)
            {
                _reads.Release();
            }
        }
    }

    private async Task<Failure> LoadImagesCoreAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        if (_disposed || !IsQualified || !OperatingSystem.IsWindows() || _selection is null || _document is null ||
            !Current(message, _context))
        {
            return Failure.NeedsReview;
        }

        Guid context = _context;
        HtmlResourceDocument document = _document;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_selection.Token, cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        bool entered = false;
        try
        {
            await _images.WaitAsync(deadline.Token);
            entered = true;
            if (!Current(message, context) || _document is null)
            {
                return Failure.NeedsReview;
            }

            document = _document;
            long pixels = document.Resources.Values.Sum(Pixels);
            long bytes = document.Resources.Values.Sum(value => (long)value.Length);
            foreach (HtmlImageRequest request in document.Plan.RemoteImages)
            {
                if (document.Resources.ContainsKey(request.ResourceId))
                {
                    continue;
                }

                Outcome<byte[]> fetched = await _fetcher.FetchAsync(request.Url, true, deadline.Token);
                if (!fetched.IsSuccess)
                {
                    return fetched.Failure;
                }

                deadline.Token.ThrowIfCancellationRequested();
                if (!Current(message, context))
                {
                    return Failure.NeedsReview;
                }

                byte[] image = fetched.Value!;
                long additionalPixels = Pixels(image);
                if (additionalPixels == 0 || pixels + additionalPixels > 4194304 || bytes + image.Length > 16777216)
                {
                    return Failure.Validation;
                }

                pixels += additionalPixels;
                bytes += image.Length;
                document.Resources.Add(request.ResourceId, image);
            }

            _document = document;
            return await RenderAsync(context, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return Failure.Unavailable;
        }
        finally
        {
            if (entered)
            {
                _images.Release();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task EnsureNativeAsync(CancellationToken cancellationToken)
    {
        if (_view is not null && _isolation is not null)
        {
            return;
        }

        if (_view is null)
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            NativeWebView view = new NativeWebView();
            TaskCompletionSource<IWindowsWebView2PlatformHandle> ready =
                new TaskCompletionSource<IWindowsWebView2PlatformHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
            string directory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            view.EnvironmentRequested += (_, args) =>
            {
                args.EnableDevTools = false;
                if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
                {
                    Directory.CreateDirectory(directory);
                    windows.UserDataFolder = directory;
                    windows.IsInPrivateModeEnabled = true;
                    windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                }
            };
            view.AdapterCreated += (_, _) =>
            {
                if (view.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle windows)
                {
                    ready.TrySetResult(windows);
                }
                else
                {
                    ready.TrySetException(new IOException("Native HTML isolation unavailable."));
                }
            };
            _view = view;
            _ready = ready;
            _window.SetHtmlSurface(view);
        }

        IWindowsWebView2PlatformHandle handle = await _ready!.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _isolation = new WindowsHtmlIsolation(handle);
    }

    [SupportedOSPlatform("windows")]
    private async Task<Failure> RenderAsync(Guid context, CancellationToken cancellationToken)
    {
        if (_isolation is null || _view is null || _document is null || _context != context)
        {
            return Failure.NeedsReview;
        }

        _isolation.BeginDocument(context, _document.Resources);
        _view.IsVisible = true;
        _window.SetHtmlSurface(_view);
        bool completed = await _isolation.NavigateDocumentAsync(_document.Plan.Html, cancellationToken);
        if (!completed && _context == context)
        {
            _view.IsVisible = false;
            _window.SetHtmlSurface(null);
        }

        return completed && _context == context ? Failure.None : Failure.Unavailable;
    }

    private static Outcome<HtmlResourceDocument> Prepare(MimeHtmlDocument content, Guid context)
    {
        Dictionary<string, string> identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, byte[]> resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long pixels = 0;
        foreach (InlineImage image in content.Images)
        {
            pixels += Pixels(image.Bytes);
            if (pixels > 4194304 || identifiers.ContainsKey(image.ContentId))
            {
                return Outcome<HtmlResourceDocument>.Fail(Failure.Validation);
            }

            Outcome<byte[]> normalized = PngImageNormalizer.Normalize(image.Bytes);
            if (!normalized.IsSuccess)
            {
                return Outcome<HtmlResourceDocument>.Fail(normalized.Failure);
            }

            string identifier = Guid.NewGuid().ToString("N");
            identifiers.Add(image.ContentId, identifier);
            resources.Add(identifier, normalized.Value!);
        }

        Outcome<HtmlRenderPlan> plan = HtmlPolicy.Plan(content.Html, context, identifiers);
        if (!plan.IsSuccess)
        {
            return Outcome<HtmlResourceDocument>.Fail(plan.Failure);
        }

        resources = resources.Where(resource => plan.Value!.CidResourceIds.Contains(resource.Key))
            .ToDictionary(resource => resource.Key, resource => resource.Value, StringComparer.Ordinal);
        return Outcome<HtmlResourceDocument>.Success(new HtmlResourceDocument(plan.Value!, resources));
    }

    private static long Pixels(byte[] bytes)
    {
        if (bytes.Length < 24)
        {
            return 0;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= 4096 && height is > 0 and <= 4096 ? (long)width * height : 0;
    }

    private bool Current(MessageSnapshot message, Guid context)
    {
        MessageSnapshot? selected = _model.Selected;
        return !_disposed && _context == context && _message?.Account == message.Account && _message.Id == message.Id &&
            selected?.Account == message.Account && selected.Id == message.Id && selected.Provider == message.Provider;
    }

    private void SelectedChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.Selected) && _message is not null &&
            !Current(_message, _context))
        {
            Reset();
        }
    }

    private void Reset()
    {
        _selection?.Cancel();
        _selection?.Dispose();
        _selection = null;
        _message = null;
        _context = Guid.NewGuid();
        _document = null;
        if (OperatingSystem.IsWindows())
        {
            _isolation?.BeginDocument(_context, new Dictionary<string, byte[]>());
        }

        if (_view is not null)
        {
            _view.IsVisible = false;
        }

        _window.SetHtmlSurface(null);
    }

    private void Track(Task task)
    {
        _work.RemoveAll(previous => previous.IsCompleted);
        _work.Add(task);
    }
}
