using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private CancellationTokenSource _submissionCancellation = new CancellationTokenSource();
    private readonly Button _send;
    private readonly Button _close;
    private readonly WrapPanel _actionButtons;
    private readonly string _sender;
    private readonly AutoCompleteBox _recipients;
    private CancellationTokenSource _suggestionLifetime = new CancellationTokenSource();
    private readonly List<Task> _suggestions = [];
    private readonly TextBox _subject;
    private readonly TextBox _body;
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
    private bool _canSubmit = true;
    private Guid? _submission;

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
        MinWidth = 480;
        MinHeight = 480;
        StackPanel form = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        _sender = content.Sender;
        _recipients = new AutoCompleteBox
        {
            Name = "Recipients",
            Text = string.Join(";", content.Recipients),
            IsTextCompletionEnabled = false,
            FilterMode = AutoCompleteFilterMode.None,
            MinimumPopulateDelay = TimeSpan.FromMilliseconds(150),
            AsyncPopulator = PopulateRecipientsAsync,
            MaxDropDownHeight = 240
        };
        Avalonia.Automation.AutomationProperties.SetName(_recipients, Strings.Recipients);
        StackPanel recipients = new StackPanel { Spacing = 4 };
        TextBlock recipientLabel = new TextBlock { Text = Strings.Recipients, TextWrapping = TextWrapping.Wrap };
        recipientLabel.Classes.Add("caption");
        recipients.Children.Add(recipientLabel);
        recipients.Children.Add(_recipients);
        form.Children.Add(recipients);
        _subject = FormControls.AddField(form, "Subject", content.Subject);
        _body = FormControls.AddField(form, "Body", content.PlainText);
        _body.AcceptsReturn = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.MinHeight = 220;
        _body.Bind(TextBox.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BodyFontSize"));
        if (!content.Attachments.IsDefaultOrEmpty)
        {
            _attachments.AddRange(content.Attachments);
        }

        WrapPanel actions = new WrapPanel();
        _actionButtons = actions;
        FormControls.AddActionButton(actions, "Attach", () => RunActionAsync(AttachAsync), _status);
        if (original is not null && !originalAttachments.IsDefaultOrEmpty)
        {
            ListBox originals = new ListBox
            {
                ItemsSource = originalAttachments,
                MaxHeight = 120
            };
            Avalonia.Automation.AutomationProperties.SetName(originals, Strings.Get("OriginalAttachments"));
            originals.ItemTemplate = new FuncDataTemplate<AttachmentSnapshot>((attachment, _) => attachment is null ? null : new TextBlock
            {
                Text = attachment.Name
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
        _attachmentList.ItemTemplate = new FuncDataTemplate<ComposeAttachment>((attachment, _) => attachment is null ? null : new TextBlock
        {
            Text = attachment.Name + " (" + attachment.Content.Length.ToString(
                System.Globalization.CultureInfo.CurrentCulture
            ) + " " + Strings.Get("Bytes") + ")"
        });
        _attachmentList.ItemsSource = _attachments.ToArray();
        _attachmentList.IsVisible = _attachments.Count > 0;
        form.Children.Add(_attachmentList);
        Button remove = FormControls.AddActionButton(actions, "RemoveAttachment", () => RunActionAsync(async () =>
        {
            if (_attachmentList.SelectedItem is ComposeAttachment attachment && !_frozen)
            {
                _attachments.Remove(attachment);
                _attachmentList.ItemsSource = _attachments.ToArray();
                _attachmentList.IsVisible = _attachments.Count > 0;
                await FlushAsync();
            }
        }), _status);
        remove.IsEnabled = _attachments.Count > 0;
        _attachmentList.SelectionChanged += (_, _) => remove.IsEnabled = _attachmentList.SelectedItem is ComposeAttachment && !_frozen;
        Button save = FormControls.AddActionButton(actions, "Save", () => RunActionAsync(async () => { await FlushAsync(); }), _status);
        save.Classes.Remove("primary");
        Button send = FormControls.AddActionButton(actions, "Send", QueueAsync, _status);
        _send = send;
        send.IsEnabled = !conflict;
        _close = FormControls.AddActionButton(actions, "CloseCompose", () =>
        {
            Close();
            return Task.CompletedTask;
        }, _status);
        StackPanel footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(actions);
        footer.Children.Add(_status);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_status, Avalonia.Automation.AutomationLiveSetting.Polite);
        Content = FormControls.Page(Strings.Compose, form, footer);
        SizeChanged += (_, _) => _body.MinHeight = Math.Max(140, Bounds.Height - 380);
        _recipients.TextChanged += (_, _) => ScheduleSave();
        foreach (TextBox field in new[] { _subject, _body })
        {
            field.TextChanged += (_, _) =>
            {
                ScheduleSave();
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
        Closed += (_, _) =>
        {
            _timer.Stop();
            _submissionCancellation.Dispose();
            _suggestionLifetime.Cancel();
            _suggestionLifetime.Dispose();
        };
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
    public Guid DraftId => _id;
    public Guid? SourceSubmission => _predecessor;

    public event Action<string>? SendStatusChanged;

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
        _submissionCancellation.Cancel();
        _suggestionLifetime.Cancel();
        IsEnabled = false;
        _timer.Stop();
    }

    public async Task<bool> PrepareShutdownAsync()
    {
        await _actions.WaitAsync();
        try
        {
            await Task.WhenAll(_suggestions.ToArray());
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
        _suggestionLifetime.Dispose();
        _suggestionLifetime = new CancellationTokenSource();
        _submissionCancellation.Dispose();
        _submissionCancellation = new CancellationTokenSource();
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
        _attachmentList.IsVisible = _attachments.Count > 0;
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

    private void ScheduleSave()
    {
        _timer.Stop();
        if (!_frozen && !_stopping)
        {
            _timer.Start();
        }
    }

    private Task<IEnumerable<object>> PopulateRecipientsAsync(string? text, CancellationToken cancellationToken)
    {
        Task<IEnumerable<object>> task = ReadRecipientsAsync(text, cancellationToken);
        _suggestions.RemoveAll(previous => previous.IsCompleted);
        _suggestions.Add(task);
        return task;
    }

    private async Task<IEnumerable<object>> ReadRecipientsAsync(string? text, CancellationToken cancellationToken)
    {
        string input = text ?? "";
        (int start, int caret, int end) = RecipientRange(input);
        string prefix = input[start..caret].Trim();
        if (_frozen || _stopping || prefix.Length == 0)
        {
            return [];
        }

        // Keep the original token while keyboard navigation previews different suggestions.
        _recipients.TextSelector = (_, address) => input[..start] + (start > 0 ? " " : "") + address + input[end..];
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _suggestionLifetime.Token
        );
        try
        {
            ImmutableArray<string> addresses = await _application.Catalog.RecognisedRecipientsAsync(_account, prefix, lifetime.Token);
            string[] existing = (input[..start] + input[end..]).Split(';', StringSplitOptions.TrimEntries);
            return addresses.IsDefault ? [] : addresses.Where(address =>
                !existing.Contains(address, StringComparer.OrdinalIgnoreCase) && MailAddress.TryCreate(address, out _)).Cast<object>().ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (IOException)
        {
            _status.Text = Strings.Get("RecipientsUnavailable");
            return [];
        }
    }

    private (int Start, int Caret, int End) RecipientRange(string input)
    {
        TextBox? editor = _recipients.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        int caret = input == _recipients.Text ? Math.Clamp(editor?.CaretIndex ?? input.Length, 0, input.Length) : input.Length;
        int start = input[..caret].LastIndexOf(';') + 1;
        int separator = input.IndexOf(';', caret);
        return (start, caret, separator >= 0 ? separator : input.Length);
    }

    private ComposeContent Snapshot()
    {
        return new ComposeContent(
            _sender,
            (_recipients.Text ?? "").Split(
    ';',
    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
).ToImmutableArray(),
            _subject.Text ?? "",
            _body.Text ?? "",
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N") + "@postfold.invalid",
            _attachments.ToImmutableArray(),
            _predecessor
        );
    }

    private async Task SendAsync()
    {
        if ((_frozen && !_canSubmit) || _conflict || !await FlushAsync())
        {
            return;
        }

        _actionButtons.IsEnabled = false;
        try
        {
            if (_submission is null)
            {
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
                if (!queued.IsSuccess)
                {
                    _status.Text = Strings.Describe(queued.Failure);
                    return;
                }

                _submission = queued.Value!.Id;
                _frozen = true;
                _send.IsVisible = false;
                foreach (Button button in _actionButtons.Children.OfType<Button>())
                {
                    if (button != _send && button != _close)
                    {
                        button.IsEnabled = false;
                    }
                }
                _recipients.IsEnabled = false;
                foreach (TextBox field in new[] { _subject, _body })
                {
                    field.IsReadOnly = true;
                }
            }

            _status.Text = Strings.Get("Sending");
            _canSubmit = false;
            Failure failure = Failure.NeedsReview;
            try
            {
                failure = await _application.SubmitAsync(_account, _submission.Value, _submissionCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // The durable state determines whether cancellation happened before or during transmission.
            }

            OutboxEntry? entry = (await _application.Catalog.OutboxAsync(_account, CancellationToken.None))
                .FirstOrDefault(item => item.Submission.Id == _submission);
            _status.Text = entry is null ? Strings.Get("SendReview") : Strings.Get("SendStatus." + entry.State);
            if (entry?.State is SubmissionState.Queued or SubmissionState.Rejected)
            {
                _status.Text += " " + Strings.Describe(failure);
            }
            _send.IsVisible = entry?.State == SubmissionState.Queued;
            _canSubmit = entry?.State == SubmissionState.Queued;
            SendStatusChanged?.Invoke(_status.Text!);
            if (entry?.State == SubmissionState.Completed)
            {
                _allowClose = true;
                Close();
            }
        }
        catch (Exception exception) when (_frozen && exception is IOException or UnauthorizedAccessException)
        {
            _send.IsVisible = false;
            _status.Text = Strings.Get("SendReview");
            SendStatusChanged?.Invoke(_status.Text);
        }
        finally
        {
            _actionButtons.IsEnabled = true;
            if (_frozen && IsVisible && !_stopping)
            {
                (_canSubmit ? _send : _close).Focus();
            }
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
            _attachmentList.IsVisible = _attachments.Count > 0;
        }

        await FlushAsync();
    }
}
