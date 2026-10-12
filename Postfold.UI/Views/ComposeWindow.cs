using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

public sealed class ComposeWindow : Window
{
    private readonly IMailApplication _application;
    private readonly AccountId _account;
    private readonly Guid _id;
    private readonly Guid? _predecessor;
    private readonly SemaphoreSlim _saving = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _actions = new SemaphoreSlim(1, 1);
    private readonly TextBox _sender;
    private readonly TextBox _recipients;
    private readonly TextBox _subject;
    private readonly TextBox _body;
    private readonly TextBox _signature;
    private readonly TextBlock _status = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly List<ComposeAttachment> _attachments = [];
    private readonly ListBox _attachmentList = new ListBox
    {
        MaxHeight = 120
    };
    private readonly DispatcherTimer _timer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };
    private bool _allowClose;
    private bool _frozen;
    private bool _conflict;
    private bool _stopping;
    private bool _closing;

    public ComposeWindow(
        IMailApplication application,
        AccountId account,
        ComposeContent content,
        Guid? id = null,
        bool conflict = false,
        Guid? predecessor = null,
        MessageSnapshot? original = null,
        ImmutableArray<AttachmentSnapshot> originalAttachments = default
    )
    {
        _application = application;
        _account = account;
        _id = id ?? Guid.NewGuid();
        _predecessor = predecessor ?? content.Predecessor;
        _conflict = conflict;
        Title = Strings.Compose;
        Width = 760;
        Height = 760;
        StackPanel form = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8
        };
        _sender = FormControls.AddField(form, "Sender", content.Sender);
        _recipients = FormControls.AddField(form, "Recipients", string.Join(";", content.Recipients));
        _subject = FormControls.AddField(form, "Subject", content.Subject);
        _body = FormControls.AddField(form, "Body", content.PlainText);
        _body.AcceptsReturn = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.MinHeight = 220;
        _signature = FormControls.AddField(form, "Signature", content.Signature);
        _signature.AcceptsReturn = true;
        if (!content.Attachments.IsDefaultOrEmpty)
        {
            _attachments.AddRange(content.Attachments);
        }

        FormControls.AddActionButton(form, "Attach", () => RunActionAsync(AttachAsync), _status);
        if (original is not null && !originalAttachments.IsDefaultOrEmpty)
        {
            ListBox originals = new ListBox
            {
                ItemsSource = originalAttachments,
                MaxHeight = 120
            };
            Avalonia.Automation.AutomationProperties.SetName(originals, Strings.Get("OriginalAttachments"));
            originals.ItemTemplate = new FuncDataTemplate<AttachmentSnapshot>((attachment, _) => new TextBlock
            {
                Text = attachment!.Name
            });
            form.Children.Add(new TextBlock
            {
                Text = Strings.Get("OriginalAttachments")
            });
            form.Children.Add(originals);
            FormControls.AddActionButton(form, "AttachOriginal", () => RunActionAsync(async () =>
            {
                if (originals.SelectedItem is AttachmentSnapshot attachment && !_frozen)
                {
                    await AttachOriginalAsync(original, attachment, CancellationToken.None);
                }
            }), _status);
        }
        Avalonia.Automation.AutomationProperties.SetName(_attachmentList, Strings.Get("ComposeAttachments"));
        _attachmentList.ItemTemplate = new FuncDataTemplate<ComposeAttachment>((attachment, _) => new TextBlock
        {
            Text = attachment!.Name + " (" + attachment.Content.Length.ToString(
                System.Globalization.CultureInfo.CurrentCulture
            ) + " " + Strings.Get("Bytes") + ")"
        });
        _attachmentList.ItemsSource = _attachments.ToArray();
        form.Children.Add(_attachmentList);
        FormControls.AddActionButton(form, "RemoveAttachment", () => RunActionAsync(async () =>
        {
            if (_attachmentList.SelectedItem is ComposeAttachment attachment && !_frozen)
            {
                _attachments.Remove(attachment);
                _attachmentList.ItemsSource = _attachments.ToArray();
                await FlushAsync();
            }
        }), _status);
        FormControls.AddActionButton(form, "Save", () => RunActionAsync(async () => { await FlushAsync(); }), _status);
        Button send = FormControls.AddActionButton(form, "Send", QueueAsync, _status);
        send.IsEnabled = !conflict;
        form.Children.Add(_status);
        Content = new ScrollViewer
        {
            Content = form
        };
        foreach (TextBox field in new[] { _sender, _recipients, _subject, _body, _signature })
        {
            field.TextChanged += (_, _) =>
            {
                _timer.Stop();
                if (!_frozen && !_stopping)
                {
                    _timer.Start();
                }
            };
        }

        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await FlushAsync();
        };
        Closing += async (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            BeginShutdown();
            if (await PrepareShutdownAsync())
            {
                ConfirmShutdown();
                Close();
            }
            else
            {
                ResumeEditing();
                _closing = false;
            }
        };
        Closed += (_, _) => _timer.Stop();
        Opened += (_, _) => _recipients.Focus();
        KeyDown += async (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && args.Key is Key.S or Key.Enter)
            {
                args.Handled = true;
                await ShortcutAsync(args.Key == Key.S ? async () => { await FlushAsync(); }
                : SendAsync);
            }
            else if (args.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && args.Key == Key.A)
            {
                args.Handled = true;
                await ShortcutAsync(AttachAsync);
            }
        };
        _status.Text = conflict ? Strings.Conflict : "";
    }

    public AccountId Account => _account;
    public Guid? SourceSubmission => _predecessor;

    public async Task<bool> FlushAsync()
    {
        if (_frozen)
        {
            return true;
        }

        _timer.Stop();
        await _saving.WaitAsync();
        try
        {
            _status.Text = Strings.Saving;
            Outcome<DraftSnapshot> saved = await _application.SaveDraftAsync(
                _account,
                _id,
                Snapshot(),
                CancellationToken.None
            );
            _conflict |= saved.Value?.Conflict == true || saved.Value?.Held == true;
            _status.Text = !saved.IsSuccess ? Strings.DraftFailed : _conflict ? Strings.Conflict : Strings.Saved;
            return saved.IsSuccess;
        }
        catch (IOException)
        {
            _status.Text = Strings.DraftFailed;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _status.Text = Strings.DraftFailed;
            return false;
        }
        finally
        {
            _saving.Release();
        }
    }

    public void BeginShutdown()
    {
        _stopping = true;
        IsEnabled = false;
        _timer.Stop();
    }

    public async Task<bool> PrepareShutdownAsync()
    {
        await _actions.WaitAsync();
        try
        {
            return await FlushAsync();
        }
        finally
        {
            _actions.Release();
        }
    }

    public void ConfirmShutdown()
    {
        _allowClose = true;
    }

    public void ResumeEditing()
    {
        _stopping = false;
        IsEnabled = true;
    }

    public Task QueueAsync()
    {
        return RunActionAsync(SendAsync);
    }

    public async Task<Failure> AttachOriginalAsync(
        MessageSnapshot original,
        AttachmentSnapshot attachment,
        CancellationToken cancellationToken
    )
    {
        if (_frozen || _stopping || original.Account != _account)
        {
            return Failure.Validation;
        }

        using MemoryStream content = new MemoryStream();
        Failure copied = await _application.SaveAttachmentAsync(original, attachment.Part, content, cancellationToken);
        if (copied != Failure.None)
        {
            _status.Text = Strings.Describe(copied);
            return copied;
        }

        content.Position = 0;
        Outcome<ComposeAttachment> imported = await _application.ImportAttachmentAsync(
            _account,
            attachment.Name,
            content,
            cancellationToken
        );
        if (!imported.IsSuccess)
        {
            _status.Text = Strings.Describe(imported.Failure);
            return imported.Failure;
        }

        _attachments.Add(imported.Value!);
        _attachmentList.ItemsSource = _attachments.ToArray();
        return await FlushAsync() ? Failure.None : Failure.Unavailable;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        await _actions.WaitAsync();
        try
        {
            if (!_stopping)
            {
                await action();
            }
        }
        finally
        {
            _actions.Release();
        }
    }

    private async Task ShortcutAsync(Func<Task> action)
    {
        try
        {
            await RunActionAsync(action);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status.Text = Strings.DraftFailed;
        }
    }

    private ComposeContent Snapshot()
    {
        return new ComposeContent(
            _sender.Text ?? "",
            (_recipients.Text ?? "").Split(
    ';',
    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
).ToImmutableArray(),
            _subject.Text ?? "",
            _body.Text ?? "",
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N") + "@postfold.invalid",
            _attachments.ToImmutableArray(),
            _signature.Text ?? "",
            _predecessor
        );
    }

    private async Task SendAsync()
    {
        if (_frozen || _conflict || !await FlushAsync())
        {
            return;
        }

        ComposeContent content = Snapshot();
        if (!MailAddress.TryCreate(content.Sender, out _) || content.Recipients.IsDefaultOrEmpty ||
            content.Recipients.Any(item => !MailAddress.TryCreate(item, out _) || item.Contains('\r') || item.Contains('\n')))
        {
            _status.Text = Strings.Describe(Failure.Validation);
            return;
        }

        Outcome<FrozenSubmission> queued = await _application.SendAsync(
            _account,
            content,
            _id,
            _predecessor,
            CancellationToken.None
        );
        if (queued.IsSuccess)
        {
            _frozen = true;
            _allowClose = true;
            Close();
        }
        else
        {
            _status.Text = Strings.Describe(queued.Failure);
        }
    }

    private async Task AttachAsync()
    {
        if (_frozen)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Attach,
            AllowMultiple = true
        });
        foreach (IStorageFile file in files)
        {
            await using Stream source = await file.OpenReadAsync();
            Outcome<ComposeAttachment> imported = await _application.ImportAttachmentAsync(
                _account,
                file.Name,
                source,
                CancellationToken.None
            );
            if (!imported.IsSuccess)
            {
                _status.Text = Strings.Describe(imported.Failure);
                return;
            }

            _attachments.Add(imported.Value!);
            _attachmentList.ItemsSource = _attachments.ToArray();
        }

        await FlushAsync();
    }
}
