using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Outbox;
using Postfold.UI.Resources;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Controls;

internal sealed class SendIssuesView : StackPanel
{
    private readonly IMailApplication _application;
    private readonly AccountId _account;
    private readonly Action<ComposeContent, Guid, AccountId> _reviewMessage;
    private readonly CancellationToken _cancellationToken;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly StackPanel _details;
    private readonly TextBlock _empty;

    internal SendIssuesView(
        IMailApplication application,
        AccountId account,
        Action<ComposeContent, Guid, AccountId> reviewMessage,
        CancellationToken cancellationToken
    )
    {
        _application = application;
        _account = account;
        _reviewMessage = reviewMessage;
        _cancellationToken = cancellationToken;
        Spacing = 12;
        _list = new ListBox
        {
            Name = "SendIssues",
            MaxHeight = 240,
            ItemTemplate = new FuncDataTemplate<OutboxEntry>((entry, _) => entry is null ? null : new TextBlock
            {
                Text = DescribeEntry(entry),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            })
        };
        AutomationProperties.SetName(_list, Strings.Get("MailNeedingAttention"));
        _list.Classes.Add("mail");
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        _details = new StackPanel
        {
            Spacing = 8
        };
        _empty = new TextBlock
        {
            Text = Strings.Get("NoSendIssues"),
            TextWrapping = TextWrapping.Wrap
        };
        _list.SelectionChanged += (_, _) => ShowSelectedEntry();
        Children.Add(_list);
        Children.Add(_empty);
        Children.Add(_details);
        FormControls.AddActionButton(this, "RefreshSends", RefreshAsync, _status);
        Children.Add(_status);
    }

    internal bool HasIssues => _list.ItemCount > 0;

    internal async Task RefreshAsync()
    {
        ImmutableArray<OutboxEntry> entries = await _application.Catalog.OutboxAsync(_account, _cancellationToken);
        _list.ItemsSource = entries.IsDefault ? ImmutableArray<OutboxEntry>.Empty
            : entries.Where(entry => entry.State != SubmissionState.Completed).ToImmutableArray();
        _empty.IsVisible = !HasIssues;
        _details.Children.Clear();
    }

    private static string DescribeEntry(OutboxEntry entry)
    {
        string subject = string.IsNullOrWhiteSpace(entry.Submission.Subject) ? Strings.Get("NoSubject") : entry.Submission.Subject;
        string description = subject + "\n" + string.Join("; ", entry.Submission.Recipients) + "\n" + Strings.Get("SendStatus." + entry.State);
        if (entry.Evidence is { Failure: not (Failure.None or Failure.NeedsReview) } evidence)
        {
            description += "\n" + Strings.Describe(evidence.Failure);
        }
        return description;
    }

    private void ShowSelectedEntry()
    {
        _details.Children.Clear();
        if (_list.SelectedItem is not OutboxEntry entry)
        {
            return;
        }

        WrapPanel actions = new WrapPanel();
        _details.Children.Add(actions);
        switch (entry.State)
        {
            case SubmissionState.Queued:
                AddQueuedActions(actions, entry);
                break;
            case SubmissionState.Accepted:
            case SubmissionState.SentCopyPending:
                AddSentCopyAction(actions, entry);
                break;
            case SubmissionState.Uncertain:
            case SubmissionState.PartiallyAccepted:
            case SubmissionState.Rejected:
            case SubmissionState.Cancelled:
                AddReviewAction(actions, entry);
                break;
        }
    }

    private void AddQueuedActions(WrapPanel actions, OutboxEntry entry)
    {
        FormControls.AddActionButton(actions, "Send", async () =>
        {
            _status.Text = Strings.Get("Sending");
            _status.Text = Strings.Describe(await _application.SubmitAsync(_account, entry.Submission.Id, _cancellationToken));
            await RefreshAsync();
        }, _status);
        FormControls.AddActionButton(actions, "CancelSend", async () =>
        {
            _status.Text = Strings.Describe(await _application.CancelQueuedAsync(entry, _cancellationToken));
            await RefreshAsync();
        }, _status);
    }

    private void AddSentCopyAction(WrapPanel actions, OutboxEntry entry)
    {
        CheckBox consent = new CheckBox
        {
            Content = Strings.Get("SentCopyConsent"),
            IsVisible = entry.State == SubmissionState.SentCopyPending
        };
        _details.Children.Add(consent);
        FormControls.AddActionButton(actions, "SaveSentCopy", async () =>
        {
            if (entry.State == SubmissionState.SentCopyPending && consent.IsChecked != true)
            {
                _status.Text = Strings.Describe(Failure.NeedsReview);
                return;
            }

            Failure result;
            if (entry.State == SubmissionState.Accepted)
            {
                result = await _application.StoreSentCopyAsync(entry, _cancellationToken);
            }
            else
            {
                result = await _application.RetrySentCopyAsync(entry, _cancellationToken);
            }
            _status.Text = Strings.Describe(result);
            await RefreshAsync();
        }, _status);
    }

    private void AddReviewAction(WrapPanel actions, OutboxEntry entry)
    {
        bool uncertain = entry.State is SubmissionState.Uncertain or SubmissionState.PartiallyAccepted;
        if (entry.Evidence is not null)
        {
            _details.Children.Add(new TextBlock
            {
                Text = string.Join("\n", entry.Evidence.Recipients.Select(recipient =>
                    recipient.Address + " — " + Strings.Describe(recipient.State))),
                TextWrapping = TextWrapping.Wrap
            });
        }
        CheckBox consent = new CheckBox
        {
            Content = Strings.Get("ResendConsent"),
            IsVisible = uncertain
        };
        _details.Children.Add(consent);
        FormControls.AddActionButton(actions, "ReviewMessage", async () =>
        {
            if (uncertain && consent.IsChecked != true)
            {
                _status.Text = Strings.Describe(Failure.NeedsReview);
                return;
            }

            Outcome<ComposeContent> content = await _application.PrepareResendAsync(entry, _cancellationToken);
            if (content.IsSuccess)
            {
                _reviewMessage(content.Value!, entry.Submission.Id, _account);
                consent.IsChecked = false;
            }
            else
            {
                _status.Text = Strings.Describe(content.Failure);
            }
        }, _status);
    }
}
