using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.UI.Models;
using Postfold.UI.Resources;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMailApplication? _application;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _searching;
    private Task _load = Task.CompletedTask;
    private readonly List<Task> _reads = [];
    private readonly HashSet<(AccountId Account, MessageId Message, ProviderIdentity Provider)> _markingRead = [];
    private string _title = "Postfold";
    private string _terms = "";
    private string _status = "";
    private string _notificationStatus = "";
    private string _body = "";
    private string? _continuation;
    private MailScope? _scope;
    private MessageSnapshot? _selected;
    private MimeSnapshot? _content;
    private long _searchGeneration;
    private SearchRequest? _pageScope;
    private readonly TimeProvider _clock;
    private bool _unreadOnly;
    private bool _updatingRows;

    public MainWindowViewModel() : this(null)
    {
    }

    public MainWindowViewModel(IMailApplication? application, TimeProvider? clock = null)
    {
        _application = application;
        _clock = clock ?? TimeProvider.System;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(false));
        MoreCommand = new AsyncRelayCommand(() => RefreshAsync(true), () => _continuation is not null);
        RetrieveCommand = new AsyncRelayCommand(() => ReadSelectedAsync(true), () => Selected is not null);
        ReadCommand = new AsyncRelayCommand(() => MutateAsync(MutationKind.SetRead, null), CanMutate);
        FlagCommand = new AsyncRelayCommand(() => MutateAsync(MutationKind.SetFlagged, null), CanMutate);
        ArchiveCommand = new AsyncRelayCommand(
            () => MoveRoleAsync(MailboxRole.Archive),
            () => CanMoveRole(MailboxRole.Archive)
        );
        TrashCommand = new AsyncRelayCommand(
            () => MoveRoleAsync(MailboxRole.Trash),
            () => CanMoveRole(MailboxRole.Trash)
        );
        SynchroniseCommand = new AsyncRelayCommand(SynchroniseAsync, () => HasAccounts && _application is not null);
    }

    public ObservableCollection<MailScope> Scopes { get; } = [];
    public ObservableCollection<NavigationItemViewModel> Navigation { get; } = [];
    public bool UpdatingNavigation { get; private set; }
    public NavigationItemViewModel? NavigationSelection
    {
        get => Navigation.Concat(Navigation.SelectMany(item => item.Children)).FirstOrDefault(item => item.Scope == Scope);
        set
        {
            if (!UpdatingNavigation)
            {
                Scope = value?.Scope;
            }
        }
    }
    public ObservableCollection<MessageSnapshot> Messages { get; } = [];
    public ObservableCollection<MessageSummary> Rows { get; } = [];
    public ObservableCollection<AttachmentSnapshot> Attachments { get; } = [];
    public ObservableCollection<AccountConfiguration> Accounts { get; } = [];
    public ObservableCollection<MailboxSnapshot> Mailboxes { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand MoreCommand { get; }
    public IAsyncRelayCommand RetrieveCommand { get; }
    public IAsyncRelayCommand ReadCommand { get; }
    public IAsyncRelayCommand FlagCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand TrashCommand { get; }
    public IAsyncRelayCommand SynchroniseCommand { get; }

    public string Title
    {
        get => _title; set => SetProperty(ref _title, value);
    }

    public string Terms
    {
        get => _terms;
        set
        {
            if (SetProperty(ref _terms, value))
            {
                InvalidatePage();
            }
        }
    }

    public string NotificationStatus => _notificationStatus;
    public bool HasAccounts => Accounts.Count > 0;
    public bool IsEmpty => Rows.Count == 0;
    public bool HasStatus => !string.IsNullOrEmpty(Status) || !string.IsNullOrEmpty(NotificationStatus);
    public bool HasMore => _continuation is not null;
    public bool AllMail => !UnreadOnly;
    public string SearchScope => string.Join(" · ", new[]
    {
        Scope?.Account is AccountId account ? Accounts.FirstOrDefault(item => item.Id == account)?.Name : "",
        Scope?.Name.Trim(), UnreadOnly ? Strings.Unread : "", Strings.Get("CachedMail")
    }.Where(value => !string.IsNullOrEmpty(value)).Distinct());

    public bool UnreadOnly
    {
        get => _unreadOnly;
        set
        {
            if (SetProperty(ref _unreadOnly, value))
            {
                InvalidatePage();
                OnPropertyChanged(nameof(AllMail));
                OnPropertyChanged(nameof(SearchScope));
            }
        }
    }

    public MessageSummary? SelectedRow
    {
        get => Rows.FirstOrDefault(row => row.Message.Account == Selected?.Account && row.Message.Id == Selected.Id);
        set
        {
            if (!_updatingRows)
            {
                Selected = value?.Message;
            }
        }
    }

    public string ReaderSubject => Selected is null ? Strings.Get("SelectMessage") :
        string.IsNullOrWhiteSpace(Selected.Subject) ? Strings.Get("NoSubject") : Selected.Subject;
    public string ReaderSender => SenderPresentation.Create(Selected?.Participants ?? "").Name;
    public string ReaderAddress => SenderPresentation.Create(Selected?.Participants ?? "").Address;
    public string ReaderTime => Selected?.Date.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) ?? "";
    public string ReaderTo => Content is MimeSnapshot content && !content.ToRecipients.IsDefaultOrEmpty ?
        "To: " + string.Join("; ", content.ToRecipients.Select(address =>
            string.Equals(address, Accounts.FirstOrDefault(account => account.Id == Selected?.Account)?.Identity?.Sender,
                StringComparison.OrdinalIgnoreCase) ? Strings.Get("Me") : address)) : "";
    public bool HasSelection => Selected is not null;
    public string ReaderInitials => SelectedRow?.Initials ?? "";
    public string ReaderDate => Selected?.Date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) ?? "";
    public string ReaderAccount => Accounts.FirstOrDefault(account => account.Id == Selected?.Account)?.Name ?? "";
    public string ReaderRecipients => Content is MimeSnapshot content && !content.ToRecipients.IsDefault ?
        Strings.Recipients + ": " + string.Join("; ", content.ToRecipients) +
            (content.CcRecipients.IsDefaultOrEmpty ? "" : "\nCc: " + string.Join("; ", content.CcRecipients)) +
            (content.RecipientHeadersComplete ? "" : "\n" + Strings.Get("RecipientsLimited")) : "";
    public string ReadAction => Strings.Get(Selected?.Read == true ? "MarkUnread" : "MarkRead");
    public bool CanComposeFromMessage => Selected is not null && Content is not null;
    public bool CanReplyAll => CanComposeFromMessage && Content!.RecipientHeadersComplete;
    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentHeading => Attachments.Count.ToString(CultureInfo.CurrentCulture) + " " + Strings.Get("Attachments");

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public string Body
    {
        get => _body; private set => SetProperty(ref _body, value);
    }

    public MimeSnapshot? Content => _content;
    public long CachedMessageCount { get; private set; }

    public MailScope? Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                InvalidatePage();
                OnPropertyChanged(nameof(NavigationSelection));
                OnPropertyChanged(nameof(SearchScope));
                RegroupMessages();
            }
        }
    }

    public MessageSnapshot? Selected
    {
        get => _selected;
        set
        {
            bool opened = value is not null && (_selected?.Account != value.Account || _selected.Id != value.Id ||
                _selected.Provider != value.Provider);
            if (SetProperty(ref _selected, value))
            {
                _load = OpenSelectedAsync(opened);
                _reads.RemoveAll(item => item.IsCompletedSuccessfully || item.IsCanceled);
                _reads.Add(_load);
                NotifyCommands();
                OnPropertyChanged(nameof(SelectedRow));
                NotifyReader();
            }
        }
    }

    public bool CanMove => CanMutate() && Accounts.FirstOrDefault(item => item.Id == Selected!.Account)?.Capabilities is
        Capabilities capabilities && (capabilities.NativeMove || capabilities.SelectiveExpunge);
    public string MoveSupport => Selected is null ? "" :
        Accounts.FirstOrDefault(item => item.Id == Selected.Account)?.Capabilities is not Capabilities capabilities ?
        Strings.Get("MoveCapabilityUnknown") : !capabilities.NativeMove && !capabilities.SelectiveExpunge ?
        Strings.Get("MoveCapabilityUnsupported") : "";

    public async Task InitialiseAsync()
    {
        if (_application is null)
        {
            return;
        }

        MailScope? previous = Scope;
        Dictionary<AccountId, bool> expanded = Navigation.Where(item => item.Scope.IsAccount)
            .ToDictionary(item => item.Scope.Account!.Value, item => item.IsExpanded);
        CachedMessageCount = await _application.Catalog.CachedMessageCountAsync(_lifetime.Token);
        OnPropertyChanged(nameof(CachedMessageCount));
        ImmutableArray<AccountConfiguration> accounts = await _application.Catalog.AccountsAsync(_lifetime.Token);
        ImmutableArray<MailboxSnapshot> mailboxes = await _application.Catalog.MailboxesAsync(_lifetime.Token);
        UpdatingNavigation = true;
        Accounts.Clear();
        Mailboxes.Clear();
        Scopes.Clear();
        foreach (AccountConfiguration account in accounts)
        {
            Accounts.Add(account);
        }

        foreach (MailboxSnapshot mailbox in mailboxes)
        {
            Mailboxes.Add(mailbox);
        }

        foreach (AccountConfiguration account in Accounts)
        {
            Scopes.Add(new MailScope(account.Name, account.Id, null, false, account.Name));
            foreach (MailboxSnapshot mailbox in Mailboxes.Where(item => item.Account == account.Id)
                .OrderBy(item => item.Role == MailboxRole.Unresolved ? int.MaxValue : (int)item.Role)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Scopes.Add(new MailScope("    " + mailbox.Name, mailbox.Account, mailbox.Id, false, account.Name));
            }
        }
        OnPropertyChanged(nameof(HasAccounts));

        Scopes.Add(new MailScope(Strings.Get("UnifiedInbox"), null, null, true));
        Scopes.Add(new MailScope(Strings.Get("UnifiedSearch"), null, null, false));
        Navigation.Clear();
        foreach (MailScope scope in Scopes.Where(item => !item.IsFolder))
        {
            ImmutableArray<NavigationItemViewModel> children = scope.IsAccount ?
                Scopes.Where(item => item.IsFolder && item.Account == scope.Account)
                    .Select(item => new NavigationItemViewModel(item, [],
                        Mailboxes.First(mailbox => mailbox.Account == item.Account && mailbox.Id == item.Folder).Role))
                    .ToImmutableArray() : [];
            Navigation.Add(new NavigationItemViewModel(scope, children)
            {
                IsExpanded = scope.Account is not AccountId account || expanded.GetValueOrDefault(account, true)
            });
        }
        AccountId? firstAccount = Accounts.FirstOrDefault()?.Id;
        MailboxSnapshot? inbox = Mailboxes.FirstOrDefault(mailbox => mailbox.Account == firstAccount && mailbox.Role == MailboxRole.Inbox);
        Scope = Scopes.FirstOrDefault(item => item == previous) ??
            Scopes.FirstOrDefault(item => item.Account == firstAccount && item.Folder == inbox?.Id) ??
            Scopes.FirstOrDefault();
        NotifyCommands();
        OnPropertyChanged(nameof(NavigationSelection));
        UpdatingNavigation = false;
        await RefreshAsync(false);
    }

    public async Task RefreshAsync(bool next)
    {
        if (_application is null || Scope is null || next && _continuation is null)
        {
            return;
        }

        _searching?.Cancel();
        _searching?.Dispose();
        _searching = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _searching.Token;
        long generation = ++_searchGeneration;
        MessageSnapshot? selection = Selected;
        try
        {
            SearchRequest request = new SearchRequest(
                Scope.Account,
                Scope.Account is null,
                Terms,
                Scope.Folder,
                UnreadOnly,
                100,
                null,
                Scope.InboxOnly
            );
            if (next && request != _pageScope)
            {
                InvalidatePage();
                return;
            }

            Outcome<SearchPage> result = await _application.SearchAsync(request with
            {
                Continuation = next ? _continuation : null
            }, token);
            if (generation != _searchGeneration || token.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                Status = Strings.Describe(result.Failure);
                return;
            }

            if (!next)
            {
                for (int index = 0; index < result.Value!.Messages.Length; index++)
                {
                    if (index < Messages.Count)
                    {
                        if (Messages[index] != result.Value.Messages[index])
                        {
                            Messages[index] = result.Value.Messages[index];
                        }
                    }
                    else
                    {
                        Messages.Add(result.Value.Messages[index]);
                    }
                }
                while (Messages.Count > result.Value.Messages.Length)
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            }

            foreach (MessageSnapshot message in result.Value!.Messages)
            {
                if (!Messages.Any(item => item.Account == message.Account && item.Id == message.Id))
                {
                    Messages.Add(message);
                }
            }

            _pageScope = request;
            _continuation = result.Value.Continuation;
            MoreCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasMore));
            Selected = Messages.FirstOrDefault(item => item.Account == selection?.Account && item.Id == selection.Id);
            RegroupMessages();
            Status = "";
            OnPropertyChanged(nameof(IsEmpty));
            if (!next)
            {
                await RefreshUnreadCountsAsync(generation, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.IO.IOException)
        {
            Status = Strings.Describe(Failure.Unavailable);
        }
    }

    /// <summary>Opens a list message, including a second click after explicitly marking the selected message unread.</summary>
    public Task OpenMessageAsync(MessageSnapshot message)
    {
        if (Selected?.Account != message.Account || Selected.Id != message.Id || Selected.Provider != message.Provider)
        {
            Selected = message;
            return _load;
        }
        if (Selected.Read || _markingRead.Contains((message.Account, message.Id, message.Provider)))
        {
            return _load;
        }

        _load = OpenSelectedAsync(true);
        _reads.RemoveAll(item => item.IsCompletedSuccessfully || item.IsCanceled);
        _reads.Add(_load);
        return _load;
    }

    public async Task ReadSelectedAsync(bool retrieve)
    {
        _reading?.Cancel();
        _reading?.Dispose();
        _reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _reading.Token;
        MessageSnapshot? message = Selected;
        Body = "";
        Attachments.Clear();
        _content = null;
        NotifyReader();
        if (_application is null || message is null)
        {
            return;
        }

        try
        {
            Outcome<MimeSnapshot> result = await _application.ReadAsync(message, retrieve, token);
            if (token.IsCancellationRequested || Selected?.Id != message.Id || Selected.Account != message.Account)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                Body = Strings.Get("Missing");
                Status = Strings.Describe(result.Failure);
                return;
            }

            _content = result.Value;
            Body = result.Value!.PlainText ?? Strings.Get("HtmlUnavailable");
            foreach (AttachmentSnapshot attachment in result.Value.Attachments)
            {
                Attachments.Add(attachment);
            }
            NotifyReader();
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.IO.IOException)
        {
            Status = Strings.Describe(Failure.Unavailable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        List<Task> tasks = new List<Task>(_reads);
        foreach (IAsyncRelayCommand command in new[] { RefreshCommand, MoreCommand, RetrieveCommand, ReadCommand,
            FlagCommand, ArchiveCommand, TrashCommand, SynchroniseCommand })
        {
            if (command.ExecutionTask is Task task)
            {
                tasks.Add(task);
            }
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        _reading?.Dispose();
        _searching?.Dispose();
        _lifetime.Dispose();
    }

    public Task MoveAsync(MailboxSnapshot destination)
    {
        if (!CanMove || destination.Account != Selected?.Account || !destination.Selectable ||
            !destination.Writable || !Mailboxes.Contains(destination))
        {
            Status = Strings.Get("CommandUnsupported");
            return Task.CompletedTask;
        }

        return MutateAsync(MutationKind.Move, destination.RemoteKey);
    }

    /// <summary>Validates a drop against the exact selected message, its account and currently available writable folders.</summary>
    public bool CanDropMessage(MessageSnapshot message, MailboxSnapshot destination)
    {
        return Selected?.Account == message.Account && Selected.Id == message.Id && Selected.Provider == message.Provider &&
            (Scope?.Account is null || Scope.Account == message.Account) && CanMove &&
            destination.Account == message.Account && destination.Selectable && destination.Writable &&
            Mailboxes.Contains(destination) && destination.RemoteKey != message.Provider.Imap?.Mailbox;
    }

    public Task DropMessageAsync(MessageSnapshot message, MailboxSnapshot destination)
    {
        if (!CanDropMessage(message, destination))
        {
            Status = Strings.Get("CommandUnsupported");
            return Task.CompletedTask;
        }

        return MoveAsync(destination);
    }

    public bool CanMutate()
    {
        return Selected?.Provider.Imap is ImapIdentity identity && Mailboxes.Any(item =>
            item.Account == Selected.Account && item.RemoteKey == identity.Mailbox && item.Selectable && item.Writable);
    }

    public void ReportNotificationFailure()
    {
        SetProperty(ref _notificationStatus, Strings.Get("NotificationUnavailable"), nameof(NotificationStatus));
        OnPropertyChanged(nameof(HasStatus));
    }

    public void ReportSendStatus(string status)
    {
        Status = status;
    }

    public void ReportReadingResult(Failure result)
    {
        Status = result == Failure.None ? "" : Strings.Describe(result);
    }

    public Task CheckAttachmentsAsync()
    {
        Task task = CheckAttachmentsCoreAsync();
        _reads.RemoveAll(previous => previous.IsCompletedSuccessfully || previous.IsCanceled);
        _reads.Add(task);
        return task;
    }

    /// <summary>Re-evaluates local date headings after time-zone or midnight changes without fetching mail.</summary>
    public void RegroupMessages()
    {
        DateOnly today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        int weekOffset = ((int)today.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        DateOnly startOfWeek = today.AddDays(-weekOffset);
        string? previous = null;
        _updatingRows = true;
        try
        {
            for (int index = 0; index < Messages.Count; index++)
            {
                MessageSnapshot message = Messages[index];
                DateTimeOffset local = TimeZoneInfo.ConvertTime(message.Date, _clock.LocalTimeZone);
                DateOnly date = DateOnly.FromDateTime(local.DateTime);
                string group = date == today ? Strings.Get("Today") : date == today.AddDays(-1) ? Strings.Get("Yesterday") :
                    date >= startOfWeek && date < today ? Strings.Get("ThisWeek") : date.ToString("D", CultureInfo.CurrentCulture);
                MessageSummary row = new MessageSummary(
                    message,
                    group == previous ? "" : group,
                    Accounts.FirstOrDefault(account => account.Id == message.Account)?.Name ?? Strings.Get("Account"),
                    local.ToString(date == today ? "t" : date >= startOfWeek ? "ddd" : "d", CultureInfo.CurrentCulture),
                    Scope?.Account is null
                );
                previous = group;
                if (index >= Rows.Count)
                {
                    Rows.Add(row);
                }
                else if (Rows[index] != row)
                {
                    Rows[index] = row;
                }
            }
            while (Rows.Count > Messages.Count)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }
        }
        finally
        {
            _updatingRows = false;
        }
        OnPropertyChanged(nameof(SelectedRow));
    }

    private async Task OpenSelectedAsync(bool opened)
    {
        // Reading owns selection cancellation; an already requested read-state change owns the application lifetime.
        Task reading = ReadSelectedAsync(false);
        MessageSnapshot? message = Selected;
        if (opened && message is not null && !message.Read && CanMutate() && _application is not null &&
            _markingRead.Add((message.Account, message.Id, message.Provider)))
        {
            try
            {
                Failure result = await _application.MutateAsync(message, MutationKind.SetRead, true, null, _lifetime.Token);
                if (result == Failure.None)
                {
                    MessageSnapshot current = Messages.FirstOrDefault(item => item.Account == message.Account &&
                        item.Id == message.Id && item.Provider == message.Provider) ?? message;
                    MessageSnapshot read = current with
                    {
                        Read = true,
                        Pending = PendingState.Queued
                    };
                    int index = Messages.ToList().FindIndex(item => item.Account == message.Account && item.Id == message.Id &&
                        item.Provider == message.Provider);
                    if (index >= 0)
                    {
                        Messages[index] = read;
                        RegroupMessages();
                    }
                    if (Selected?.Account == message.Account && Selected.Id == message.Id && Selected.Provider == message.Provider)
                    {
                        SetProperty(ref _selected, read, nameof(Selected));
                        OnPropertyChanged(nameof(SelectedRow));
                        NotifyReader();
                    }
                    await RefreshUnreadCountsAsync(_searchGeneration, _lifetime.Token);
                }
                else if (Selected?.Account == message.Account && Selected.Id == message.Id)
                {
                    Status = Strings.Describe(result);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.IO.IOException)
            {
                if (Selected?.Account == message.Account && Selected.Id == message.Id)
                {
                    Status = Strings.Describe(Failure.Unavailable);
                }
            }
            finally
            {
                _markingRead.Remove((message.Account, message.Id, message.Provider));
            }
        }
        await reading;
    }

    private void NotifyReader()
    {
        OnPropertyChanged(nameof(ReaderSubject));
        OnPropertyChanged(nameof(ReaderSender));
        OnPropertyChanged(nameof(ReaderAddress));
        OnPropertyChanged(nameof(ReaderTime));
        OnPropertyChanged(nameof(ReaderTo));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ReaderInitials));
        OnPropertyChanged(nameof(ReaderDate));
        OnPropertyChanged(nameof(ReaderAccount));
        OnPropertyChanged(nameof(ReaderRecipients));
        OnPropertyChanged(nameof(ReadAction));
        OnPropertyChanged(nameof(CanComposeFromMessage));
        OnPropertyChanged(nameof(CanReplyAll));
        OnPropertyChanged(nameof(AttachmentHeading));
        OnPropertyChanged(nameof(HasAttachments));
    }

    private async Task RefreshUnreadCountsAsync(long generation, CancellationToken cancellationToken)
    {
        if (_application is null)
        {
            return;
        }

        IMailApplication application = _application;
        Task<(AccountId Account, ImmutableArray<MailboxUnreadCount> Counts, bool Available)>[] pending = Accounts.ToArray()
            .Select(account => ReadCountsAsync(account.Id)).ToArray();
        (AccountId Account, ImmutableArray<MailboxUnreadCount> Counts, bool Available)[] results = await Task.WhenAll(pending);
        if (generation != _searchGeneration || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach ((AccountId account, ImmutableArray<MailboxUnreadCount> counts, bool available) in results)
        {
            NavigationItemViewModel[] folders = Navigation.Where(item => item.Scope.Account == account)
                .SelectMany(item => item.Children).ToArray();
            foreach (NavigationItemViewModel folder in folders)
            {
                folder.UnreadCount = !available || counts.IsDefault ? null : counts.FirstOrDefault(count =>
                    count.Account == account && count.Folder == folder.Scope.Folder)?.Count;
            }

            if (!available)
            {
                Status = Strings.Get("CountsUnavailable");
            }
        }

        async Task<(AccountId Account, ImmutableArray<MailboxUnreadCount> Counts, bool Available)> ReadCountsAsync(AccountId account)
        {
            try
            {
                return (account, await application.Catalog.MailboxUnreadCountsAsync(account, cancellationToken), true);
            }
            catch (System.IO.IOException)
            {
                return (account, default, false);
            }
        }
    }

    private async Task MutateAsync(MutationKind kind, string? destination)
    {
        MessageSnapshot? message = Selected;
        if (_application is null || message is null || !CanMutate())
        {
            return;
        }

        bool value = kind == MutationKind.SetRead ? !message.Read : kind == MutationKind.SetFlagged && !message.Flagged;
        try
        {
            Failure result = await _application.MutateAsync(message, kind, value, destination, _lifetime.Token);
            await RefreshAsync(false);
            Status = result == Failure.None ? Strings.Get("Pending") : Strings.Describe(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task MoveRoleAsync(MailboxRole role)
    {
        MailboxSnapshot[] destinations = Mailboxes.Where(item => item.Account == Selected?.Account &&
            item.Role == role && item.Writable).ToArray();
        if (destinations.Length != 1)
        {
            Status = Strings.Describe(Failure.NeedsReview);
            return Task.CompletedTask;
        }

        return MutateAsync(MutationKind.Move, destinations[0].RemoteKey);
    }

    private async Task CheckAttachmentsCoreAsync()
    {
        MessageSnapshot? message = Selected;
        if (_application is null || message is null)
        {
            return;
        }

        try
        {
            Outcome<ImmutableArray<AttachmentSnapshot>> result = await _application.ReadAttachmentsAsync(
                message,
                true,
                _reading?.Token ?? _lifetime.Token
            );
            if (Selected?.Account != message.Account || Selected.Id != message.Id || Selected.Provider != message.Provider)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                Status = Strings.Describe(result.Failure);
                return;
            }

            Attachments.Clear();
            foreach (AttachmentSnapshot attachment in result.Value)
            {
                Attachments.Add(attachment);
            }

            Status = Strings.Get("AttachmentDetails");
            NotifyReader();
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.IO.IOException)
        {
            if (Selected?.Account == message.Account && Selected.Id == message.Id && Selected.Provider == message.Provider)
            {
                Status = Strings.Describe(Failure.Unavailable);
            }
        }
    }

    private bool CanMoveRole(MailboxRole role)
    {
        return CanMove && Mailboxes.Count(item => item.Account == Selected!.Account &&
            item.Role == role && item.Selectable && item.Writable) == 1;
    }

    private void NotifyCommands()
    {
        SynchroniseCommand.NotifyCanExecuteChanged();
        RetrieveCommand.NotifyCanExecuteChanged();
        ReadCommand.NotifyCanExecuteChanged();
        FlagCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        TrashCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(MoveSupport));
    }

    private void InvalidatePage()
    {
        _continuation = null;
        _pageScope = null;
        _searchGeneration++;
        _searching?.Cancel();
        MoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasMore));
    }

    private async Task SynchroniseAsync()
    {
        if (_application is null || !HasAccounts)
        {
            return;
        }

        try
        {
            AccountConfiguration[] accounts = Accounts.Where(account => Scope?.Account is null || account.Id == Scope.Account).ToArray();
            Failure result = Failure.None;
            foreach (AccountConfiguration account in accounts)
            {
                Failure next = await _application.SynchroniseAsync(account.Id, _lifetime.Token);
                if (result == Failure.None)
                {
                    result = next;
                }
            }
            await InitialiseAsync();
            Status = result == Failure.None ? "" : Strings.Describe(result);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
