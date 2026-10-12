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

    public MainWindowViewModel() : this(null)
    {
    }

    public MainWindowViewModel(IMailApplication? application)
    {
        _application = application;
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
        SynchroniseCommand = new AsyncRelayCommand(SynchroniseAsync);
    }

    public ObservableCollection<MailScope> Scopes { get; } = [];
    public ObservableCollection<MessageSnapshot> Messages { get; } = [];
    public ObservableCollection<AttachmentSnapshot> Attachments { get; } = [];
    public ObservableCollection<AccountConfiguration> Accounts { get; } = [];
    public ObservableCollection<MailboxSnapshot> Mailboxes { get; } = [];
    public IAsyncRelayCommand RefreshCommand
    {
        get;
    }
    public IAsyncRelayCommand MoreCommand
    {
        get;
    }
    public IAsyncRelayCommand RetrieveCommand
    {
        get;
    }
    public IAsyncRelayCommand ReadCommand
    {
        get;
    }
    public IAsyncRelayCommand FlagCommand
    {
        get;
    }
    public IAsyncRelayCommand ArchiveCommand
    {
        get;
    }
    public IAsyncRelayCommand TrashCommand
    {
        get;
    }
    public IAsyncRelayCommand SynchroniseCommand
    {
        get;
    }

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

    public string Status
    {
        get => _status; private set => SetProperty(ref _status, value);
    }

    public string Body
    {
        get => _body; private set => SetProperty(ref _body, value);
    }

    public MimeSnapshot? Content => _content;
    public long CachedMessageCount
    {
        get; private set;
    }

    public MailScope? Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                InvalidatePage();
            }
        }
    }

    public MessageSnapshot? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                _load = ReadSelectedAsync(false);
                _reads.RemoveAll(item => item.IsCompletedSuccessfully || item.IsCanceled);
                _reads.Add(_load);
                NotifyCommands();
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
        CachedMessageCount = await _application.Catalog.CachedMessageCountAsync(_lifetime.Token);
        OnPropertyChanged(nameof(CachedMessageCount));
        Accounts.Clear();
        Mailboxes.Clear();
        Scopes.Clear();
        foreach (AccountConfiguration account in await _application.Catalog.AccountsAsync(_lifetime.Token))
        {
            Accounts.Add(account);
            Scopes.Add(new MailScope(account.Name + " — " + Strings.Get("Search"), account.Id, null, false));
        }

        foreach (MailboxSnapshot mailbox in await _application.Catalog.MailboxesAsync(_lifetime.Token))
        {
            Mailboxes.Add(mailbox);
            string accountName = Accounts.FirstOrDefault(account => account.Id == mailbox.Account)?.Name ??
                Strings.Get("Account");
            Scopes.Add(new MailScope(accountName + " / " + mailbox.Name, mailbox.Account, mailbox.Id, false));
        }

        Scopes.Add(new MailScope(Strings.Get("UnifiedInbox"), null, null, true));
        Scopes.Add(new MailScope(Strings.Get("UnifiedSearch"), null, null, false));
        AccountId? firstAccount = Accounts.FirstOrDefault()?.Id;
        MailboxSnapshot? inbox = Mailboxes.FirstOrDefault(mailbox => mailbox.Account == firstAccount && mailbox.Role == MailboxRole.Inbox);
        Scope = Scopes.FirstOrDefault(item => item == previous) ??
            Scopes.FirstOrDefault(item => item.Account == firstAccount && item.Folder == inbox?.Id) ??
            Scopes.FirstOrDefault();
        NotifyCommands();
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
                false,
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
                Messages.Clear();
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
            Selected = Messages.FirstOrDefault(item => item.Account == selection?.Account && item.Id == selection.Id);
            Status = Strings.Get(Messages.Count == 0 ? "NoResults" : "Ready");
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.IO.IOException)
        {
            Status = Strings.Describe(Failure.Unavailable);
        }
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

    public bool CanMutate()
    {
        return Selected?.Provider.Imap is ImapIdentity identity && Mailboxes.Any(item =>
            item.Account == Selected.Account && item.RemoteKey == identity.Mailbox && item.Selectable && item.Writable);
    }

    public void ReportNotificationFailure()
    {
        SetProperty(ref _notificationStatus, Strings.Get("NotificationUnavailable"), nameof(NotificationStatus));
    }

    public void ReportReadingResult(Failure result)
    {
        Status = Strings.Describe(result);
    }

    public Task CheckAttachmentsAsync()
    {
        Task task = CheckAttachmentsCoreAsync();
        _reads.RemoveAll(previous => previous.IsCompletedSuccessfully || previous.IsCanceled);
        _reads.Add(task);
        return task;
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
    }

    private async Task SynchroniseAsync()
    {
        AccountId? selectedAccount = Scope?.Account ?? Selected?.Account;
        if (_application is null || selectedAccount is not AccountId account)
        {
            Status = Strings.Describe(Failure.Validation);
            return;
        }

        try
        {
            Failure result = await _application.SynchroniseAsync(account, _lifetime.Token);
            await InitialiseAsync();
            Status = Strings.Describe(result);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
