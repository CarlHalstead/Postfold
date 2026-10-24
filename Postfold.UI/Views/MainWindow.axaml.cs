using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.UI.Controls;
using Postfold.UI.Models;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

public sealed partial class MainWindow : Window
{
    private static readonly DataFormat<MessageSnapshot> MessageDragFormat = DataFormat.CreateInProcessFormat<MessageSnapshot>("Postfold.Message");
    private readonly ILauncher? _launcher;
    private readonly List<ComposeWindow> _compose = [];
    private readonly List<Task> _work = [];
    private CancellationTokenSource _lifetime = new CancellationTokenSource();
    private bool _stopping;
    private IClientAppearance? _appearance;
    private AppearanceSettings _settings = AppearanceSettings.Default;
    private IMailApplication? _application;
    private IHtmlReadingService? _htmlReader;
    private long _htmlReadGeneration;
    private IAccountSetup? _setup;
    private IAccountManagement? _management;
    private IClientIdentity? _identity;
    private IDesktopIntegration? _desktop;
    private IClientUpdates? _updates;
    private SettingsWindow? _settingsDialog;
    private GridLength _listWidth = new GridLength(0.36, GridUnitType.Star);
    private GridLength _readerWidth = new GridLength(0.45, GridUnitType.Star);
    private GridLength _listHeight = new GridLength(0.45, GridUnitType.Star);
    private GridLength _readerHeight = new GridLength(0.55, GridUnitType.Star);
    private GridLength _navigationWidth = new GridLength(0.19, GridUnitType.Star);
    private bool _changingFilter;
    private ScrollViewer? _pageScroll;
    private MainWindowViewModel? _pageModel;
    private readonly DispatcherTimer _dates = new DispatcherTimer
    {
        Interval = TimeSpan.FromMinutes(1)
    };
    private string? _layoutPreview;
    private AccountId? _removingAccount;
    private PointerPressedEventArgs? _dragPress;
    private MessageSnapshot? _dragMessage;
    private Point _dragStart;
    private Failure _startupFailure;
    private bool _canStartFresh;
    private Window? _profileFailureDialog;
    private string? _profilesDirectory;

    public MainWindow() : this(null)
    {
    }

    /// <summary>Allows the host or tests to supply a platform launcher; null uses the window's native launcher.</summary>
    public MainWindow(ILauncher? launcher)
    {
        _launcher = launcher;
        AvaloniaXamlLoader.Load(this);
        Wire("SetupButton", () =>
        {
            if (_setup is null && _startupFailure != Failure.None)
            {
                ShowProfileFailure();
            }
            else
            {
                OpenAccountSetup();
            }
            return Task.CompletedTask;
        });
        Wire("SettingsButton", () => { OpenSettings(); return Task.CompletedTask; });
        this.FindControl<Button>("SetupButton")!.IsEnabled = false;
        this.FindControl<Button>("SettingsButton")!.IsEnabled = false;
        this.FindControl<ListBox>("MessageList")!.TemplateApplied += (_, args) =>
        {
            if (_pageScroll is not null)
            {
                _pageScroll.PropertyChanged -= PageScrollChanged;
            }
            _pageScroll = args.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_pageScroll is not null)
            {
                _pageScroll.PropertyChanged += PageScrollChanged;
            }
            UpdatePagination();
        };
        DataContextChanged += (_, _) =>
        {
            if (_pageModel is not null)
            {
                _pageModel.PropertyChanged -= PagingStateChanged;
            }
            _pageModel = DataContext as MainWindowViewModel;
            if (_pageModel is not null)
            {
                _pageModel.PropertyChanged += PagingStateChanged;
            }
            UpdatePagination();
        };
        Closed += (_, _) =>
        {
            if (_pageModel is not null)
            {
                _pageModel.PropertyChanged -= PagingStateChanged;
            }
        };
        this.FindControl<DockPanel>("PreviewPane")!.SizeChanged += (_, _) => AdjustReaderViewport();
        ListBox messageList = this.FindControl<ListBox>("MessageList")!;
        messageList.AddHandler(PointerPressedEvent, MessagePointerPressed, RoutingStrategies.Tunnel);
        messageList.PointerMoved += MessagePointerMoved;
        messageList.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.A && args.KeyModifiers == KeyModifiers.Control && DataContext is MainWindowViewModel model)
            {
                model.SelectAllMessages();
                args.Handled = true;
            }
            else if (DataContext is MainWindowViewModel { AllMessagesSelected: true } selection &&
                args.Key is Key.Escape or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                selection.ClearMessageSelection();
            }
        }, RoutingStrategies.Tunnel);
        messageList.Tapped += (_, args) =>
        {
            if (args.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MessageSummary row &&
                DataContext is MainWindowViewModel model && !model.AllMessagesSelected && model.SelectedRows.Count == 1 &&
                model.SelectedRows[0].Message == row.Message)
            {
                Track(() => model.OpenMessageAsync(row.Message));
            }
        };
        messageList.KeyUp += (_, _) =>
        {
            if (DataContext is MainWindowViewModel { AllMessagesSelected: false, SelectedRows.Count: 1 } model)
            {
                Track(() => model.OpenMessageAsync(model.SelectedRows[0].Message));
            }
        };
        messageList.DoubleTapped += (_, _) => Track(EditSelectedDraftAsync);
        messageList.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && DataContext is MainWindowViewModel { IsDraftContext: true })
            {
                Track(EditSelectedDraftAsync);
                args.Handled = true;
            }
        };
        messageList.PointerReleased += (_, _) => { _dragPress = null; _dragMessage = null; };
        this.FindControl<DockPanel>("NavigationPane")!.SizeChanged += (_, _) => AdjustPaneToolbars();
        this.FindControl<DockPanel>("MessagePane")!.SizeChanged += (_, _) => AdjustPaneToolbars();
        TextBlock readLabel = ((StackPanel)this.FindControl<Button>("ReadButton")!.Content!).Children.OfType<TextBlock>().Single();
        readLabel.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty)
            {
                AdjustReaderViewport();
            }
        };
        _dates.Tick += (_, _) => (DataContext as MainWindowViewModel)?.RegroupMessages();
        Opened += (_, _) => _dates.Start();
        Closed += (_, _) => _dates.Stop();
        Opened += (_, _) =>
        {
            if (Application.Current?.PlatformSettings is Avalonia.Platform.IPlatformSettings platform)
            {
                platform.ColorValuesChanged += SystemColoursChanged;
            }
        };
        Closed += (_, _) =>
        {
            if (Application.Current?.PlatformSettings is Avalonia.Platform.IPlatformSettings platform)
            {
                platform.ColorValuesChanged -= SystemColoursChanged;
            }
        };
        SizeChanged += (_, _) =>
        {
            string preview = ResolvePreview();
            if (_layoutPreview != preview)
            {
                ReflowPreview(preview);
            }
        };
    }

    /// <summary>Requests a separate empty profile; the desktop host owns its creation and selection.</summary>
    public event EventHandler? FreshProfileRequested;

    public void SetHtmlSurface(Control? surface)
    {
        ContentControl html = this.FindControl<ContentControl>("HtmlSurface")!;
        StackPanel content = this.FindControl<StackPanel>("ReaderContent")!;
        StackPanel header = this.FindControl<StackPanel>("ReaderHeader")!;
        StackPanel attachments = this.FindControl<StackPanel>("ReaderAttachmentPanel")!;
        ScrollViewer htmlHeader = this.FindControl<ScrollViewer>("HtmlHeaderScroll")!;
        StackPanel footer = this.FindControl<StackPanel>("HtmlFooter")!;
        bool showing = surface is not null;
        if (showing)
        {
            html.Content = surface;
            content.Children.Remove(header);
            content.Children.Remove(attachments);
            htmlHeader.Content = header;
            if (!footer.Children.Contains(attachments))
            {
                footer.Children.Add(attachments);
            }
        }
        else
        {
            htmlHeader.Content = null;
            footer.Children.Remove(attachments);
            if (!content.Children.Contains(header))
            {
                content.Children.Insert(0, header);
                content.Children.Add(attachments);
            }
        }

        html.IsVisible = showing;
        this.FindControl<Grid>("HtmlReaderGrid")!.IsVisible = showing;
        this.FindControl<ScrollViewer>("PlainPreviewScroll")!.IsVisible = !showing;
        AdjustReaderViewport();
    }

    public void ReleaseHtmlSurface()
    {
        this.FindControl<ContentControl>("HtmlSurface")!.Content = null;
        SetHtmlSurface(null);
    }

    /// <summary>Shows a renderer failure without exposing another message or an alternate reading mode.</summary>
    public void ReportHtmlFailure(Failure failure)
    {
        TextBlock status = this.FindControl<TextBlock>("HtmlReadingStatus")!;
        status.IsVisible = true;
        status.Text = Strings.Describe(failure);
    }

    public void ConfigureIdentity(IClientIdentity identity)
    {
        _identity = identity;
    }

    public void ConfigureHtml(IHtmlReadingService reader)
    {
        _htmlReader = reader;
        this.FindControl<SelectableTextBlock>("PlainPreview")!.IsVisible = false;
        this.FindControl<MenuItem>("ImagesButton")!.IsEnabled = reader.IsQualified;
        Track(ReadHtmlAsync);
        Wire("ImagesButton", () =>
        {
            if (DataContext is MainWindowViewModel model && model.Selected is MessageSnapshot message)
            {
                StackPanel panel = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 8
                };
                panel.Children.Add(new TextBlock
                {
                    Text = Strings.Get("ImageConsent"),
                    TextWrapping = TextWrapping.Wrap
                });
                TextBlock status = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap
                };
                FormControls.AddActionButton(panel, "ConfirmImages", async () =>
                {
                    status.Text = Strings.Describe(await reader.LoadImagesAsync(message, _lifetime.Token));
                }, status);
                panel.Children.Add(status);
                ShowWindow(new Window
                {
                    Title = Strings.LoadImages,
                    Width = 540,
                    Height = 300,
                    Content = panel
                });
            }

            return Task.CompletedTask;
        });
    }

    public void Configure(
        IMailApplication application,
        IAccountSetup? setup,
        IDesktopIntegration? desktop = null,
        IAccountManagement? management = null,
        IClientUpdates? updates = null
    )
    {
        _management = management;
        _application = application;
        _setup = setup;
        _desktop = desktop;
        _updates = updates;
        Wire("ComposeButton", () => { OpenCompose(); return Task.CompletedTask; });
        Wire("EditDraftButton", EditSelectedDraftAsync);
        Wire("MessageEditDraftMenu", EditSelectedDraftAsync);
        foreach ((string name, string kind) in new[]
        {
            ("ReplyButton", "Reply"), ("ReplyAllButton", "ReplyAll"), ("ForwardButton", "Forward"),
            ("OverflowReplyAllButton", "ReplyAll"), ("OverflowForwardButton", "Forward")
        })
        {
            Wire(name, () => { OpenCompose(kind); return Task.CompletedTask; });
        }

        this.FindControl<RadioButton>("AllFilterButton")!.IsCheckedChanged += (_, _) =>
        {
            if (!_changingFilter && this.FindControl<RadioButton>("AllFilterButton")!.IsChecked == true)
            {
                Track(() => SelectFilterAsync(false));
            }
        };
        this.FindControl<RadioButton>("UnreadFilter")!.IsCheckedChanged += (_, _) =>
        {
            if (!_changingFilter && this.FindControl<RadioButton>("UnreadFilter")!.IsChecked == true)
            {
                Track(() => SelectFilterAsync(true));
            }
        };
        this.FindControl<TextBox>("SearchBox")!.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Track(RefreshFromControlsAsync);
            }
        };
        this.FindControl<Button>("SetupButton")!.IsEnabled = setup is not null;
        this.FindControl<Button>("SettingsButton")!.IsEnabled = true;
        Wire("WorkButton", () => { OpenWorkMenu(); return Task.CompletedTask; });
        Wire("DetailsButton", () => { ShowMessageDetails(); return Task.CompletedTask; });
        Wire("MoveButton", () => { Move(); return Task.CompletedTask; });
        this.FindControl<ListBox>("AttachmentList")!.DoubleTapped += (_, _) => Track(SaveAttachmentAsync);
        this.FindControl<ListBox>("AttachmentList")!.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Track(SaveAttachmentAsync);
                args.Handled = true;
            }
        };
        this.FindControl<TreeView>("ScopeList")!.SelectionChanged += (_, _) => Track(async () =>
        {
            if (DataContext is not MainWindowViewModel model || model.UpdatingNavigation)
            {
                return;
            }
            await RefreshFromControlsAsync(onlyIfInvalidated: true);
        });
        KeyDown += (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.N)
            {
                OpenCompose();
                args.Handled = true;
            }
            else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F)
            {
                this.FindControl<TextBox>("SearchBox")!.Focus();
                args.Handled = true;
            }
            else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.A &&
                this.FindControl<TreeView>("ScopeList")!.IsKeyboardFocusWithin && DataContext is MainWindowViewModel model)
            {
                model.SelectAllMessages();
                this.FindControl<ListBox>("MessageList")!.Focus();
                args.Handled = true;
            }
            else if (args.Key == Key.F6 && args.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                CyclePaneFocus(args.KeyModifiers == KeyModifiers.Shift);
                args.Handled = true;
            }
        };
    }

    /// <summary>Shows a profile startup failure without making unavailable account actions silently inert.</summary>
    public void ReportStartupFailure(
        Failure failure,
        bool canStartFresh = true,
        string? profilesDirectory = null
    )
    {
        _startupFailure = failure;
        _canStartFresh = canStartFresh;
        _profilesDirectory = profilesDirectory;
        if (DataContext is MainWindowViewModel model)
        {
            model.Title = "Postfold — " + Strings.Get("ProfileUnavailable");
            model.ReportSendStatus(Strings.Get(failure == Failure.NewerSchema ? "ProfileNewerHint" : "ProfileStartupHint"));
        }
        this.FindControl<Button>("SetupButton")!.IsEnabled = true;
        this.FindControl<Button>("SettingsButton")!.IsEnabled = true;
        ShowProfileFailure();
    }

    /// <summary>Prevents new account/settings actions while a selected profile is opening.</summary>
    public void BeginProfileStartup()
    {
        _startupFailure = Failure.None;
        _profileFailureDialog?.Close();
        _settingsDialog?.Close();
        this.FindControl<Button>("SetupButton")!.IsEnabled = false;
        this.FindControl<Button>("SettingsButton")!.IsEnabled = false;
    }

    /// <summary>Opens or activates the dismissible account dialog, including first-run startup.</summary>
    public void OpenAccountSetupIfNeeded()
    {
        if (DataContext is MainWindowViewModel model && !model.HasAccounts)
        {
            OpenAccountSetup();
        }
    }

    /// <summary>Opens or activates the account dialog without creating a second copy.</summary>
    public void OpenAccountSetup()
    {
        if (_setup is null)
        {
            return;
        }
        AccountSetupWindow? existing = OwnedWindows.OfType<AccountSetupWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        AccountSetupWindow window = new AccountSetupWindow(_setup);
        window.FontFamily = FontFamily;
        window.FontSize = FontSize;
        window.AccountAdded += account => Track(async () =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                await model.InitialiseAsync();
                model.Scope = model.Scopes.FirstOrDefault(scope => scope.Account == account &&
                    model.Mailboxes.Any(mailbox => mailbox.Id == scope.Folder && mailbox.Account == account &&
                        mailbox.Role == MailboxRole.Inbox)) ?? model.Scopes.FirstOrDefault(scope => scope.Account == account);
                await model.RefreshAsync(false);
                if (model.Accounts.FirstOrDefault(item => item.Id == account)?.ProviderKind is "Google" or "Microsoft")
                {
                    await model.SynchroniseAccountAsync(account);
                }
            }
        });
        window.Closed += (_, _) => Track(async () =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                await model.InitialiseAsync();
            }
        });
        ShowWindow(window);
    }

    public async Task LoadAppearanceAsync(IClientAppearance appearance)
    {
        _appearance = appearance;
        ApplyAppearance(await appearance.ReadAsync(_lifetime.Token));
    }

    /// <summary>Observes currently started window actions without changing their cancellation or durable-work state.</summary>
    public async Task WaitForActionsAsync()
    {
        await Task.WhenAll(_work.ToArray());
        await FormControls.DrainAsync(this);
        await Task.WhenAll(_work.ToArray());
    }

    public void CyclePaneFocus(bool backwards)
    {
        ContentControl html = this.FindControl<ContentControl>("HtmlSurface")!;
        Control preview = html.IsVisible && html.Content is Control surface ? surface : this.FindControl<SelectableTextBlock>("PlainPreview")!;
        Control[] panes = [this.FindControl<TreeView>("ScopeList")!, this.FindControl<ListBox>("MessageList")!, preview];
        panes = panes.Where(pane => pane.IsEffectivelyVisible).ToArray();
        if (panes.Length == 0)
        {
            return;
        }
        int current = Array.FindIndex(panes, pane => pane.IsKeyboardFocusWithin);
        int next = current < 0 ? backwards ? panes.Length - 1 : 0 :
            (current + (backwards ? -1 : 1) + panes.Length) % panes.Length;
        panes[next].Focus();
    }

    public void ApplyAppearance(AppearanceSettings settings)
    {
        if (!settings.IsValid)
        {
            return;
        }

        _settings = settings;
        if (Application.Current is Application application)
        {
            application.RequestedThemeVariant = MailTheme.Resolve(settings.Theme, application.PlatformSettings);
        }

        FontSize = settings.FontSize;
        Resources["SubjectFontSize"] = settings.FontSize * 26 / 16;
        Resources["BodyFontSize"] = settings.FontSize * 18 / 16;
        Resources["BodyLineHeight"] = settings.FontSize * 27 / 16;
        Resources["MetadataFontSize"] = settings.FontSize * 14 / 16;
        Resources["AvatarSize"] = settings.Density == "Compact" ? 36d : 44d;
        Resources["MessageMinimumHeight"] = settings.Density == "Compact" ? 76d : 96d;
        FontFamily = string.IsNullOrWhiteSpace(settings.Font) ? FontFamily.Default : new FontFamily(settings.Font);
        Resources["MessagePadding"] = settings.Density == "Compact" ? new Thickness(8, 4) : new Thickness(12, 10);
        foreach (Window child in OwnedWindows)
        {
            FormControls.ApplyTypography(child, FontSize, FontFamily);
        }

        ReflowPreview(ResolvePreview());
        AdjustReaderViewport();
        AdjustPaneToolbars();
    }

    public void OpenCompose(
        string? kind = null,
        ComposeContent? activation = null,
        Guid? id = null,
        bool conflict = false,
        Guid? predecessor = null,
        AccountId? targetAccount = null,
        bool held = false,
        DraftSnapshot? draft = null
    )
    {
        MainWindowViewModel? model = DataContext as MainWindowViewModel;
        if (kind is not null && (model?.CanComposeFromMessage != true))
        {
            model?.ReportReadingResult(Failure.NotFound);
            return;
        }
        if (kind == "ReplyAll" && model?.CanReplyAll != true)
        {
            model?.ReportReadingResult(Failure.Unsupported);
            return;
        }
        AccountId? requested = targetAccount ?? (kind is not null ? model?.Selected?.Account : model?.Scope?.Account) ??
            model?.Selected?.Account;
        AccountConfiguration? account = requested is AccountId explicitAccount ?
            model?.Accounts.FirstOrDefault(item => item.Id == explicitAccount) : model?.Accounts.FirstOrDefault();
        if (_application is null || account is null)
        {
            return;
        }
        if (_removingAccount == account.Id)
        {
            return;
        }

        Guid? source = predecessor ?? activation?.Predecessor;
        ComposeWindow? existing = _compose.FirstOrDefault(window => window.Account == account.Id &&
            (source is Guid parent && window.SourceSubmission == parent || id is Guid draftId && window.DraftId == draftId));
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        ComposeContent content = activation ?? new ComposeContent(
            account.Identity?.Sender ?? account.GoogleBinding?.Address ?? account.Outgoing?.Username ?? account.Address,
            [],
            "",
            "",
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N") + "@postfold.invalid"
        );
        if (kind is not null && model?.Selected is MessageSnapshot message)
        {
            content = content with
            {
                Subject = (kind is "Reply" or "ReplyAll" ? "Re: " : "Fwd: ") + message.Subject,
                PlainText = "\n\n" + string.Join("\n", (model.Content?.PlainText ?? "").Split('\n').Select(line => "> " + line)),
                Recipients = kind is "Reply" or "ReplyAll" && model.Content is MimeSnapshot original &&
                    !original.ReplyRecipients.IsDefault ? original.ReplyRecipients : []
            };
            if (kind == "ReplyAll" && model.Content is MimeSnapshot reply)
            {
                content = content with
                {
                    Recipients = content.Recipients.Concat(reply.ToRecipients.IsDefault ? [] : reply.ToRecipients)
                        .Concat(reply.CcRecipients.IsDefault ? [] : reply.CcRecipients)
                        .Where(address => !address.Equals(content.Sender, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
                };
            }
        }

        content = content with
        {
            Sender = account.Identity?.Sender ?? account.GoogleBinding?.Address ?? account.Outgoing?.Username ?? account.Address
        };

        ComposeWindow window = new ComposeWindow(
            _application,
            account.Id,
            content,
            id,
            conflict,
            predecessor,
            kind == "Forward" ? model?.Selected : null,
            kind == "Forward" && model is not null ? model.Attachments.ToImmutableArray() : [],
            held,
            draft
        );
        window.FontFamily = FontFamily;
        window.FontSize = FontSize;
        _compose.Add(window);
        window.SendStatusChanged += status => model?.ReportSendStatus(account.Name + " — " + status);
        window.Closed += (_, _) =>
        {
            _compose.Remove(window);
            this.FindControl<Button>("ComposeButton")!.Focus();
            Track(async () =>
            {
                await _application.WaitForDraftSyncAsync(account.Id, window.DraftId, _lifetime.Token);
                if (!_stopping && model is not null)
                {
                    await model.SynchroniseAccountAsync(account.Id);
                }
                await RefreshDraftListAsync(account.Id);
            });
        };
        ShowWindow(window);
    }

    /// <summary>Continues the selected folder draft using its existing durable identity and revision.</summary>
    public async Task EditSelectedDraftAsync()
    {
        if (_application is null || DataContext is not MainWindowViewModel { IsDraftContext: true, Selected: MessageSnapshot message } model ||
            model.AllMessagesSelected || model.SelectedRows.Count != 1)
        {
            return;
        }
        Outcome<DraftSnapshot> result = await _application.FindDraftAsync(message.Provider, _lifetime.Token);
        if (model.Selected?.Provider != message.Provider || !model.IsDraftContext)
        {
            return;
        }
        if (!result.IsSuccess)
        {
            model.ReportReadingResult(result.Failure);
            return;
        }
        DraftSnapshot draft = result.Value!;
        if (draft.Held || draft.Conflict)
        {
            await ReviewVersionsAsync(draft);
            return;
        }
        Outcome<ComposeContent> content = await _application.ReadDraftAsync(draft, _lifetime.Token);
        if (content.IsSuccess && model.Selected?.Provider == message.Provider && model.IsDraftContext)
        {
            OpenCompose(activation: content.Value, id: draft.Id, targetAccount: draft.Account, draft: draft);
        }
        else if (!content.IsSuccess)
        {
            model.ReportReadingResult(content.Failure);
        }
    }

    public async Task<bool> FlushComposeAsync()
    {
        _stopping = true;
        IsEnabled = false;
        Window[] children = OwnedWindows.ToArray();
        foreach (Window child in children)
        {
            child.IsEnabled = false;
            if (child is AccountSetupWindow setup)
            {
                setup.CancelSetup();
            }
            if (child is ProviderAccountWindow connection)
            {
                connection.CancelConnection();
            }
        }

        await _lifetime.CancelAsync();
        await Task.WhenAll(_work.ToArray());
        try
        {
            await FormControls.DrainAsync(this);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        ComposeWindow[] windows = _compose.ToArray();
        foreach (ComposeWindow window in windows)
        {
            window.BeginShutdown();
        }

        foreach (ComposeWindow window in windows)
        {
            if (!await window.PrepareShutdownAsync(synchronise: false))
            {
                foreach (ComposeWindow preserved in windows)
                {
                    preserved.ResumeEditing();
                }

                window.Activate();
                foreach (Window child in children)
                {
                    child.IsEnabled = true;
                }

                IsEnabled = true;
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
                _stopping = false;
                return false;
            }
        }

        foreach (ComposeWindow window in windows)
        {
            window.ConfirmShutdown();
        }

        return true;
    }

    public async Task SaveAttachmentAsync(IStorageProvider storage)
    {
        MainWindowViewModel? model = DataContext as MainWindowViewModel;
        if (_application is null || model?.Selected is not MessageSnapshot message ||
            this.FindControl<ListBox>("AttachmentList")!.SelectedItem is not AttachmentSnapshot attachment)
        {
            return;
        }

        string name = Path.GetFileName(attachment.Name.Replace('\\', '/'));
        name = string.Concat(name.Where(character => !char.IsControl(character) &&
            !Path.GetInvalidFileNameChars().Contains(character) && !"<>:\"|?*".Contains(character))).TrimEnd('.', ' ');
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            name = "attachment-" + name;
        }

        using IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Destination,
            SuggestedFileName = string.IsNullOrWhiteSpace(name) ? "attachment" : name
        });
        if (file is null || _lifetime.IsCancellationRequested || model.Selected?.Account != message.Account ||
            model.Selected.Id != message.Id || model.Selected.Provider != message.Provider)
        {
            return;
        }

        using CancellationTokenSource saving = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12
        };
        TextBlock status = new TextBlock
        {
            Text = Strings.Get("AttachmentSaving"),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(status);
        Button cancel = new Button
        {
            Content = Strings.Cancel
        };
        cancel.Click += (_, _) => saving.Cancel();
        panel.Children.Add(cancel);
        Window progress = new Window
        {
            Title = Strings.Destination,
            Width = 480,
            Height = 220,
            Content = panel
        };
        EventHandler<WindowClosingEventArgs> closing = (_, _) => saving.Cancel();
        progress.Closing += closing;
        ShowWindow(progress);
        try
        {
            await using Stream destination = await file.OpenWriteAsync();
            saving.Token.ThrowIfCancellationRequested();
            destination.SetLength(0);
            Failure result = await _application.SaveAttachmentAsync(message, attachment.Part, destination, saving.Token);
            status.Text = result == Failure.None ? Strings.Get("AttachmentSaved") : Strings.Describe(result);
        }
        catch (OperationCanceledException)
        {
            status.Text = Strings.Get("AttachmentCancelled");
        }
        catch (IOException)
        {
            status.Text = Strings.Describe(Failure.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            status.Text = Strings.Get("DestinationDenied");
        }
        finally
        {
            cancel.IsEnabled = false;
            progress.Closing -= closing;
        }
    }

    public async Task CommandsAsync(
        AccountId account
    )
    {
        if (_application is null || DataContext is not MainWindowViewModel model ||
            !model.Accounts.Any(item => item.Id == account))
        {
            return;
        }

        PendingWorkWindow window = await PendingWorkWindow.CreateAsync(
            _application,
            account,
            (content, predecessor, targetAccount) => OpenCompose(
                activation: content,
                predecessor: predecessor,
                targetAccount: targetAccount
            ),
            _lifetime.Token
        );
        ShowWindow(window);
    }

    public async Task DraftsAsync(
        AccountId account
    )
    {
        if (_application is null || DataContext is not MainWindowViewModel model || !model.Accounts.Any(item => item.Id == account))
        {
            return;
        }

        Window? existing = OwnedWindows.FirstOrDefault(item => item.Name == "AccountDrafts" + account.Value);
        if (existing is not null)
        {
            await RefreshDraftListAsync(account);
            existing.Activate();
            return;
        }

        ImmutableArray<DraftSnapshot> drafts = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
        Dictionary<Guid, string> subjects = [];
        foreach (DraftSnapshot draft in drafts)
        {
            Outcome<ComposeContent> content = await _application.ReadDraftAsync(draft, _lifetime.Token);
            subjects[draft.Id] = content.IsSuccess ? (string.IsNullOrWhiteSpace(content.Value!.Subject) ? Strings.Get("NoSubject") : content.Value.Subject) : Strings.Describe(content.Failure);
        }
        TextBlock status = new TextBlock

        {
            TextWrapping = TextWrapping.Wrap
        };
        ListBox list = new ListBox

        {
            Name = "AccountDraftList",
            Tag = subjects,
            ItemsSource = drafts,
            ItemTemplate = new FuncDataTemplate<DraftSnapshot>((draft, _) =>
                                    {
                                        if (draft is null)
                                        {
                                            return null;
                                        }
                                        string subject = subjects.GetValueOrDefault(draft.Id, Strings.Get("NoSubject"));
                                        Grid row = new Grid

                                        {
                                            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                                            ColumnSpacing = 8
                                        };
                                        StackPanel text = new StackPanel

                                        {
                                            Spacing = 4,
                                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                                        };
                                        text.Children.Add(new TextBlock

                                        {
                                            Text = subject,
                                            TextTrimming = TextTrimming.CharacterEllipsis
                                        });
                                        text.Children.Add(new TextBlock

                                        {
                                            Text = (draft.VersionCount > 0 ? Strings.Get("DraftVersion") + " " + draft.VersionCount : Strings.Get("WorkingDraft")) + " — " +
                                                Strings.Get(draft.DeletePending ? "DraftDeletePending" : draft.Conflict ? "Conflict" : draft.Held ? "DraftHeld" :
                                                    draft.PushedRevision < draft.FinalizedRevision ? "DraftPendingSync" : draft.PushedRevision == draft.Revision ? "DraftSynced" : "Saved"),
                                            TextWrapping = TextWrapping.Wrap
                                        });
                                        row.Children.Add(text);
                                        string editKey = draft.Held || draft.Conflict ? "ReviewVersions" : "OpenDraft";
                                        Button edit = FormControls.AddActionButton(row, editKey, async () =>
                                        {
                                            DraftSnapshot? current = (await _application.Catalog.DraftsAsync(account, _lifetime.Token)).SingleOrDefault(item => item.Id == draft.Id);
                                            if (current is null || current.DeletePending)
                                            {
                                                await RefreshDraftListAsync(account);
                                                return;
                                            }
                                            if (current.Held || current.Conflict)
                                            {
                                                await ReviewVersionsAsync(current);
                                                return;
                                            }
                                            Outcome<ComposeContent> content = await _application.ReadDraftAsync(current, _lifetime.Token);
                                            if (content.IsSuccess)
                                            {
                                                OpenCompose(activation: content.Value, id: current.Id, targetAccount: current.Account, draft: current);
                                            }
                                            else
                                            {
                                                status.Text = Strings.Describe(content.Failure);
                                            }
                                        }, status);
                                        Button remove = FormControls.AddActionButton(row, "DeleteDraft", async () =>
                                        {
                                            if (_compose.Any(item => item.Account == account && item.DraftId == draft.Id))
                                            {
                                                status.Text = Strings.Get("DraftOpenDeleteBlocked");
                                                return;
                                            }
                                            Failure result = await _application.DeleteDraftAsync(draft, _lifetime.Token);
                                            status.Text = result == Failure.None ? "" : Strings.Describe(result);
                                            await RefreshDraftListAsync(account);
                                        }, status);
                                        SettingsWindow.IconAction(edit, MailIcons.Rename, editKey, subject, 1, !draft.DeletePending);
                                        SettingsWindow.IconAction(remove, MailIcons.Close, "DeleteDraft", subject, 2, !draft.DeletePending);
                                        Border surface = new Border

                                        {
                                            Child = row
                                        };
                                        surface.Classes.Add("card");
                                        surface.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MailNavigationBrush"));
                                        return surface;
                                    })
        };
        list.Classes.Add("mail");
        StackPanel panel = new StackPanel

        {
            Margin = new Thickness(12),
            Spacing = 8
        };
        panel.Children.Add(list);
        StackPanel footer = new StackPanel

        {
            Spacing = 8
        };
        footer.Children.Add(status);
        Window window = new Window

        {
            Name = "AccountDrafts" + account.Value,
            Title = Strings.Drafts,
            Width = 760,
            Height = 480,
            Content = FormControls.Page(Strings.Drafts, panel, footer)
        };
        ShowWindow(window);
    }

    public void OpenSettings(string section = "Appearance", AccountId? account = null)
    {
        if (DataContext is not MainWindowViewModel model)
        {
            return;
        }
        if (_settingsDialog is null)
        {
            SettingsWindow window = new SettingsWindow(
                model,
                _settings,
                _appearance,
                _identity,
                _desktop,
                ApplyAppearance,
                selected => _setup is null ? Task.CompletedTask : CredentialsAsync(_setup, selected),
                selected => { if (_management is not null) { RenameAccount(_management, selected); } },
                selected => { if (_management is not null) { RemoveAccount(_management, selected); } },
                _management is not null,
                _setup is not null,
                _updates
            );
            _settingsDialog = window;
            window.Closed += (_, _) => _settingsDialog = null;
            window.SelectSection(section, account ?? model.Scope?.Account);
            ShowWindow(window);
        }
        else
        {
            if (account is not null || section != "Appearance")
            {
                _settingsDialog.SelectSection(section, account);
            }
            _settingsDialog.Activate();
        }
    }

    private void MessageMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (DataContext is not MainWindowViewModel model || !model.AllMessagesSelected && model.SelectedRows.Count == 0)
        {
            args.Cancel = true;
            return;
        }
        bool single = !model.AllMessagesSelected && model.SelectedRows.Count == 1;
        this.FindControl<MenuItem>("MessageEditDraftMenu")!.IsVisible = single && model.IsDraftContext;
        this.FindControl<MenuItem>("MessageReplyMenu")!.IsVisible = single && !model.IsDraftContext;
        this.FindControl<MenuItem>("MessageForwardMenu")!.IsVisible = single && !model.IsDraftContext;
        this.FindControl<MenuItem>("MessageTrashMenu")!.Header = model.TrashAction;
        this.FindControl<MenuItem>("MessageTrashMenu")!.IsEnabled = model.CanApplySelection(true);
        MenuItem read = this.FindControl<MenuItem>("MessageReadMenu")!;
        read.IsVisible = !model.IsDraftContext;
        read.Header = single ? model.ReadAction : Strings.Get("MarkRead");
        read.IsEnabled = model.CanApplySelection(false);
    }

    private void MessageReplyClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel { AllMessagesSelected: false, SelectedRows.Count: 1 })
        {
            OpenCompose("Reply");
        }
    }

    private void MessageForwardClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel { AllMessagesSelected: false, SelectedRows.Count: 1 })
        {
            OpenCompose("Forward");
        }
    }

    private void MessageTrashClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel model)
        {
            Track(() => model.ApplySelectionAsync(true, _lifetime.Token));
        }
    }

    private void MessageReadClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel model)
        {
            Track(() => model.ApplySelectionAsync(false, _lifetime.Token));
        }
    }

    private void MessagePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _dragPress = null;
        _dragMessage = null;
        if (DataContext is MainWindowViewModel selection && sender is ListBox list && args.Source is Visual target &&
            target.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MessageSummary clicked)
        {
            bool right = args.GetCurrentPoint(this).Properties.IsRightButtonPressed;
            bool selected = selection.SelectedRows.Contains(clicked);
            if (right && !selected || !right && (args.KeyModifiers & KeyModifiers.Control) == 0 && selection.AllMessagesSelected)
            {
                object? anchor = list.SelectedItem;
                selection.ClearMessageSelection();
                list.SelectedItem = (args.KeyModifiers & KeyModifiers.Shift) != 0 ? anchor : clicked;
            }
            else if (right && selected)
            {
                args.Handled = true;
            }
            if (right && !selection.AllMessagesSelected && selection.SelectedRows.Count == 1)
            {
                Track(() => selection.OpenMessageAsync(clicked.Message));
            }
        }
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed && args.Source is Visual source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MessageSummary row)
        {
            _dragPress = args;
            _dragMessage = row.Message;
            _dragStart = args.GetPosition(this);
        }
    }

    private void MessagePointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragPress is not PointerPressedEventArgs press || _dragMessage is not MessageSnapshot message)
        {
            return;
        }
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragPress = null;
            _dragMessage = null;
            return;
        }
        Point position = args.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 6 && Math.Abs(position.Y - _dragStart.Y) < 6)
        {
            return;
        }
        _dragPress = null;
        _dragMessage = null;
        if (DataContext is MainWindowViewModel model && model.Selected?.Account == message.Account &&
            model.Selected.Id == message.Id && model.SelectedRows.Count == 1 && !model.AllMessagesSelected && model.CanMove)
        {
            Track(async () =>
            {
                using DataTransfer data = new DataTransfer();
                data.Add(DataTransferItem.Create(MessageDragFormat, message));
                await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
            });
        }
    }

    private void FolderDragOver(object? sender, DragEventArgs args)
    {
        MailboxSnapshot? destination = DropDestination(sender, args);
        args.DragEffects = destination is null ? DragDropEffects.None : DragDropEffects.Move;
        args.Handled = true;
    }

    private void FolderDrop(object? sender, DragEventArgs args)
    {
        MailboxSnapshot? destination = DropDestination(sender, args);
        if (destination is not null && args.DataTransfer.TryGetValue(MessageDragFormat) is MessageSnapshot message &&
            DataContext is MainWindowViewModel model)
        {
            args.DragEffects = DragDropEffects.Move;
            Track(() => model.DropMessageAsync(message, destination));
        }
        else
        {
            args.DragEffects = DragDropEffects.None;
        }
        args.Handled = true;
    }

    private MailboxSnapshot? DropDestination(object? sender, DragEventArgs args)
    {
        if (_stopping || DataContext is not MainWindowViewModel model ||
            sender is not Control { DataContext: NavigationItemViewModel item } || !item.IsFolder ||
            args.DataTransfer.TryGetValue(MessageDragFormat) is not MessageSnapshot message)
        {
            return null;
        }
        MailboxSnapshot? destination = model.Mailboxes.FirstOrDefault(folder =>
            folder.Account == item.Scope.Account && folder.Id == item.Scope.Folder);
        return destination is not null && model.CanDropMessage(message, destination) ? destination : null;
    }

    private async Task CredentialsAsync(
        IAccountSetup setup,
        AccountId selectedAccount
    )
    {
        if (_application is null)
        {
            return;
        }

        ImmutableArray<AccountConfiguration> accounts = await _application.Catalog.AccountsAsync(_lifetime.Token);
        AccountConfiguration? selectedProvider = accounts.FirstOrDefault(item => item.Id == selectedAccount && item.ProviderKind != "Imap");
        if (selectedProvider is not null)
        {
            if (setup is not IProviderAccounts providers)
            {
                (DataContext as MainWindowViewModel)?.ReportReadingResult(Failure.Unsupported);
                return;
            }
            ShowWindow(new ProviderAccountWindow(providers, selectedProvider, async () =>
            {
                if (DataContext is MainWindowViewModel model)
                {
                    await model.InitialiseAsync();
                }
            }));
            return;
        }
        accounts = accounts.Where(item => item.ProviderKind == "Imap").ToImmutableArray();
        CancellationTokenSource credentialsLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken credentialsToken = credentialsLifetime.Token;
        StackPanel form = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        Window window = new Window
        {
            Title = Strings.Credentials,
            Width = 520,
            Height = 640,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = new ScrollViewer
            {
                Content = form
            }
        };
        ComboBox account = new ComboBox
        {
            ItemsSource = accounts,
            SelectedItem = accounts.FirstOrDefault(item => item.Id == selectedAccount),
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((item, _) => item is null ? null : new TextBlock
            {
                Text = item.Name
            })
        };
        Avalonia.Automation.AutomationProperties.SetName(account, Strings.Get("Account"));
        form.Children.Add(new TextBlock
        {
            Text = Strings.Get("CredentialsHint"),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(account);
        CredentialFields credentials = new CredentialFields(form);
        void SelectAccount()
        {
            credentials.Clear();
            if (account.SelectedItem is AccountConfiguration selected)
            {
                credentials.Shared.IsChecked = selected.Incoming?.Username == selected.Outgoing?.Username;
            }
        }
        account.SelectionChanged += (_, _) => SelectAccount();
        SelectAccount();
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(form, "SaveCredentials", async () =>
        {
            if (account.SelectedItem is not AccountConfiguration selected)
            {
                status.Text = Strings.Describe(Failure.Validation);
                return;
            }

            if (!credentials.Validate(status))
            {
                return;
            }

            try
            {
                Failure result = await setup.RestoreCredentialsAsync(
                    selected.Id,
                    credentials.ReceivingPassword.Text ?? "",
                    credentials.OutgoingPassword,
                    credentialsToken
                );
                status.Text = result == Failure.None ? Strings.Get("CredentialsSaved") : Strings.Describe(result);
            }
            finally
            {
                credentials.Clear();
            }
        }, status);
        form.Children.Add(status);
        window.Opened += (_, _) => account.Focus();
        window.Closed += (_, _) =>
        {
            credentials.Clear();
            credentialsLifetime.Cancel();
            credentialsLifetime.Dispose();
        };
        ShowWindow(window);
    }

    private void ShowProfileFailure()
    {
        if (_profileFailureDialog is not null)
        {
            _profileFailureDialog.Activate();
            return;
        }
        StackPanel form = new StackPanel();
        form.Children.Add(new TextBlock
        {
            Text = Strings.Get(_startupFailure == Failure.NewerSchema ? "ProfileNewerHint" : "ProfileStartupHint"),
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock status = new TextBlock { Name = "ProfileStartupStatus", TextWrapping = TextWrapping.Wrap };
        FormControls.AddActionButton(form, "StartFresh", () =>
        {
            FreshProfileRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, status).IsEnabled = _canStartFresh && FreshProfileRequested is not null;
        FormControls.AddActionButton(form, "OpenProfilesFolder", async () =>
        {
            if (_profilesDirectory is null)
            {
                return;
            }
            try
            {
                bool opened = await (_launcher ?? Launcher).LaunchDirectoryInfoAsync(new DirectoryInfo(_profilesDirectory));
                status.Text = opened ? "" : Strings.Get("OpenProfilesFolderFailed");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                status.Text = Strings.Get("OpenProfilesFolderFailed");
            }
        }, status).IsEnabled = _profilesDirectory is not null;
        form.Children.Add(status);
        Window window = new Window
        {
            Title = Strings.Get("ProfileUnavailable"),
            Width = 560,
            Height = 360,
            MinWidth = 420,
            MinHeight = 300,
            Content = form
        };
        _profileFailureDialog = window;
        window.Closed += (_, _) => _profileFailureDialog = null;
        ShowWindow(window);
    }

    private void OpenWorkMenu()
    {
        if (DataContext is not MainWindowViewModel model)
        {
            return;
        }
        Button button = this.FindControl<Button>("WorkButton")!;
        ContextMenu menu = new ContextMenu();
        List<MenuItem> accounts = [];
        foreach (AccountConfiguration account in model.Accounts)
        {
            MenuItem drafts = new MenuItem { Header = Strings.Drafts };
            MenuItem pending = new MenuItem { Header = Strings.Commands };
            drafts.Click += (_, _) => Track(() => DraftsAsync(account.Id));
            pending.Click += (_, _) => Track(() => CommandsAsync(account.Id));
            accounts.Add(new MenuItem { Header = account.Name, ItemsSource = new[] { drafts, pending } });
        }
        menu.ItemsSource = accounts;
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private void ShowMessageDetails()
    {
        if (DataContext is not MainWindowViewModel model || model.Selected is null)
        {
            return;
        }
        StackPanel content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new SelectableTextBlock
        {
            Text = model.ReaderSender + "\n" + model.ReaderAddress + "\n" + model.ReaderDate + "\n" +
                model.ReaderAccount + "\n\n" + model.ReaderRecipients,
            TextWrapping = TextWrapping.Wrap,
            Focusable = true
        });
        ShowWindow(new Window
        {
            Title = Strings.MessageDetails,
            Width = 560,
            Height = 420,
            MinWidth = 360,
            MinHeight = 280,
            Content = content
        });
    }

    private void SaveAttachmentClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { DataContext: AttachmentSnapshot attachment })
        {
            this.FindControl<ListBox>("AttachmentList")!.SelectedItem = attachment;
            Track(SaveAttachmentAsync);
        }
    }

    private void OpenMenuClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            button.ContextMenu?.Open(button);
        }
    }

    private void SearchHelpClicked(object? sender, RoutedEventArgs args)
    {
        StackPanel content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = Strings.SearchHint, TextWrapping = TextWrapping.Wrap });
        ShowWindow(new Window { Title = Strings.SearchScopeHint, Width = 480, Height = 280, Content = content });
    }

    private void AccountSettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { DataContext: NavigationItemViewModel item } && item.IsAccount)
        {
            OpenSettings("Accounts", item.Scope.Account);
        }
    }

    private Task SelectFilterAsync(bool unread)
    {
        if (DataContext is not MainWindowViewModel model || model.UpdatingNavigation || model.UnreadOnly == unread)
        {
            return Task.CompletedTask;
        }
        _changingFilter = true;
        try
        {
            model.UnreadOnly = unread;
            this.FindControl<RadioButton>("UnreadFilter")!.IsChecked = unread;
            this.FindControl<RadioButton>("AllFilterButton")!.IsChecked = !unread;
        }
        finally
        {
            _changingFilter = false;
        }
        return RefreshFromControlsAsync();
    }

    private string ResolvePreview()
    {
        double navigation = Math.Clamp(this.FindControl<DockPanel>("NavigationPane")!.Bounds.Width, 220, 320);
        double minimum = Math.Max(1050, navigation + 10 + 820 * _settings.FontSize / 16);
        return _settings.Preview == "Right" && Bounds.Width > 0 && Bounds.Width < minimum ? "Bottom" : _settings.Preview;
    }

    private void ReflowPreview(string position)
    {
        if (_layoutPreview == position)
        {
            return;
        }

        Grid panes = this.FindControl<Grid>("Panes")!;
        if (_layoutPreview == "Right")
        {
            _navigationWidth = panes.ColumnDefinitions[0].Width;
            _listWidth = panes.ColumnDefinitions[2].Width;
            _readerWidth = panes.ColumnDefinitions[4].Width;
        }
        else if (_layoutPreview == "Bottom")
        {
            _navigationWidth = panes.ColumnDefinitions[0].Width;
            _listHeight = panes.RowDefinitions[0].Height;
            _readerHeight = panes.RowDefinitions[2].Height;
        }
        double navigation = Math.Clamp(this.FindControl<DockPanel>("NavigationPane")!.Bounds.Width > 0 ?
            this.FindControl<DockPanel>("NavigationPane")!.Bounds.Width : 260, 220, 320);
        _layoutPreview = position;
        bool bottom = position == "Bottom";
        bool hidden = position == "Hidden";
        panes.ColumnDefinitions = new ColumnDefinitions(bottom || hidden ? "260,5,*" : "0.19*,5,0.36*,5,0.45*");
        panes.ColumnDefinitions[0].MinWidth = 220;
        panes.ColumnDefinitions[0].MaxWidth = 320;
        panes.ColumnDefinitions[0].Width = bottom || hidden ? new GridLength(navigation) : _navigationWidth;
        if (!bottom && !hidden)
        {
            panes.ColumnDefinitions[2].Width = _listWidth;
            panes.ColumnDefinitions[4].Width = _readerWidth;
        }
        panes.RowDefinitions = new RowDefinitions(bottom ? "0.45*,5,0.55*" : "*");
        if (bottom)
        {
            panes.RowDefinitions[0].Height = _listHeight;
            panes.RowDefinitions[2].Height = _readerHeight;
        }

        Control navigationPane = this.FindControl<DockPanel>("NavigationPane")!;
        Control navigationSplitter = this.FindControl<GridSplitter>("NavigationSplitter")!;
        Control messages = this.FindControl<DockPanel>("MessagePane")!;
        GridSplitter splitter = this.FindControl<GridSplitter>("PreviewSplitter")!;
        Control preview = this.FindControl<DockPanel>("PreviewPane")!;
        Grid.SetRowSpan(navigationPane, bottom ? 3 : 1);
        Grid.SetRowSpan(navigationSplitter, bottom ? 3 : 1);
        Grid.SetRow(messages, 0);
        Grid.SetColumn(preview, bottom || hidden ? 2 : 4);
        Grid.SetRow(preview, bottom ? 2 : 0);
        Grid.SetColumn(splitter, bottom || hidden ? 2 : 3);
        Grid.SetRow(splitter, bottom ? 1 : 0);
        splitter.ResizeDirection = bottom ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        if (bottom)
        {
            splitter.Classes.Add("horizontal");
        }
        else
        {
            splitter.Classes.Remove("horizontal");
        }
        preview.IsVisible = !hidden;
        splitter.IsVisible = !hidden;
    }

    private void AdjustReaderViewport()
    {
        DockPanel pane = this.FindControl<DockPanel>("PreviewPane")!;
        if (pane.Bounds.Width <= 0)
        {
            return;
        }

        bool draft = DataContext is MainWindowViewModel { IsDraftContext: true };
        this.FindControl<Button>("EditDraftButton")!.IsVisible = draft;
        string[] names = ["ReplyButton", "ReplyAllButton", "ForwardButton", "ReadButton", "TrashButton"];
        Button[] commands = names.Select(name => this.FindControl<Button>(name)!).ToArray();
        foreach (Button command in commands)
        {
            command.IsVisible = !draft || command.Name == "TrashButton";
            command.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        Button more = this.FindControl<Button>("ReaderMoreButton")!;
        more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double available = Math.Max(0, pane.Bounds.Width - 32 - more.DesiredSize.Width);
        Button edit = this.FindControl<Button>("EditDraftButton")!;
        edit.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double required = commands.Where(command => command.IsVisible).Sum(command => command.DesiredSize.Width) +
            (draft ? edit.DesiredSize.Width : 17);
        foreach (int index in new[] { 4, 3, 2, 1 })
        {
            if (required <= available)
            {
                break;
            }
            if (!commands[index].IsVisible)
            {
                continue;
            }
            required -= commands[index].DesiredSize.Width;
            if (commands[index].IsKeyboardFocusWithin)
            {
                more.Focus();
            }
            commands[index].IsVisible = false;
        }
        this.FindControl<Border>("ReaderActionDivider")!.IsVisible = !draft && (commands[3].IsVisible || commands[4].IsVisible);

        foreach ((int index, string menu) in new[]
        {
            (1, "OverflowReplyAllButton"), (2, "OverflowForwardButton"),
            (3, "OverflowReadButton"), (4, "OverflowTrashButton")
        })
        {
            this.FindControl<MenuItem>(menu)!.IsVisible = (!draft || index == 4) && !commands[index].IsVisible;
        }
        this.FindControl<ScrollViewer>("HtmlHeaderScroll")!.MaxHeight = Math.Max(64, Math.Min(240, pane.Bounds.Height * 0.3));
        this.FindControl<ScrollViewer>("HtmlAttachmentScroll")!.MaxHeight = Math.Max(48, Math.Min(180, pane.Bounds.Height * 0.24));
    }

    private void PagingStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.IsDraftContext) or nameof(MainWindowViewModel.TrashAction))
        {
            AdjustReaderViewport();
        }
        if (args.PropertyName == nameof(MainWindowViewModel.HasMore))
        {
            UpdatePagination();
        }
        if (args.PropertyName == nameof(MainWindowViewModel.Content) && _htmlReader is not null)
        {
            Track(ReadHtmlAsync);
        }
        if (args.PropertyName == nameof(MainWindowViewModel.Body) && _htmlReader is not null && _pageModel?.Content is null)
        {
            this.FindControl<TextBlock>("HtmlReadingStatus")!.Text = _pageModel?.Body;
        }
    }

    private async Task ReadHtmlAsync()
    {
        long generation = ++_htmlReadGeneration;
        if (_htmlReader is null || DataContext is not MainWindowViewModel model)
        {
            return;
        }

        TextBlock status = this.FindControl<TextBlock>("HtmlReadingStatus")!;
        status.IsVisible = true;
        status.Text = model.Content is null ? model.Body : Strings.Get("MessageLoading");
        if (model.Content is null || model.Selected is not MessageSnapshot message)
        {
            SetHtmlSurface(null);
            return;
        }

        Failure result = await _htmlReader.ReadAsync(message, false, _lifetime.Token);
        if (generation == _htmlReadGeneration && model.Selected?.Account == message.Account && model.Selected.Id == message.Id &&
            model.Selected.Provider == message.Provider)
        {
            status.IsVisible = result != Failure.None;
            status.Text = result == Failure.None ? string.Empty : Strings.Describe(result);
            model.ReportReadingResult(result);
        }
    }

    private void PageScrollChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == ScrollViewer.OffsetProperty || args.Property == ScrollViewer.ViewportProperty ||
            args.Property == ScrollViewer.ExtentProperty)
        {
            UpdatePagination();
        }
    }

    private void UpdatePagination()
    {
        bool nearEnd = _pageScroll is not null &&
            _pageScroll.Offset.Y + _pageScroll.Viewport.Height + 64 * FontSize / 16 >= _pageScroll.Extent.Height;
        this.FindControl<Button>("LoadMoreButton")!.IsVisible = _pageModel?.HasMore == true && nearEnd;
    }

    private void AdjustPaneToolbars()
    {
        DockPanel pane = this.FindControl<DockPanel>("MessagePane")!;
        StackPanel filters = this.FindControl<StackPanel>("ListFilters")!;
        bool stacked = pane.Bounds.Width < 420 * FontSize / 16;
        Grid.SetRow(filters, stacked ? 1 : 0);
        Grid.SetColumn(filters, stacked ? 0 : 1);
        Grid.SetColumnSpan(filters, stacked ? 3 : 1);
    }

    private void AccountHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Visual source && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }
        if (sender is not Grid { TemplatedParent: TreeViewItem node, DataContext: NavigationItemViewModel { IsAccount: true } } header ||
            !args.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        args.Handled = true;
        node.Focus(NavigationMethod.Pointer);
        node.SetCurrentValue(TreeViewItem.IsExpandedProperty, !node.IsExpanded);
    }

    private void RenameAccountClicked(object? sender, RoutedEventArgs args)
    {
        if (_management is not null && sender is MenuItem { DataContext: NavigationItemViewModel item } && item.Scope.IsAccount)
        {
            RenameAccount(_management, item.Scope.Account);
        }
    }

    private void RenameAccount(IAccountManagement management, AccountId? selected = null)
    {
        if (DataContext is not MainWindowViewModel model || model.Accounts.Count == 0)
        {
            return;
        }
        StackPanel panel = new StackPanel { Spacing = 12 };
        ComboBox accounts = new ComboBox
        {
            Name = "RenameAccountSelection",
            ItemsSource = model.Accounts.ToArray(),
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => account is null ? null : new TextBlock { Text = account.Name }),
            SelectedItem = model.Accounts.FirstOrDefault(account => account.Id == (selected ?? model.Scope?.Account)) ?? model.Accounts[0]
        };
        Avalonia.Automation.AutomationProperties.SetName(accounts, Strings.Get("RenameAccount"));
        panel.Children.Add(accounts);
        TextBox name = FormControls.AddField(panel, "RenameName");
        name.MaxLength = 200;
        name.Text = ((AccountConfiguration)accounts.SelectedItem!).Name;
        accounts.SelectionChanged += (_, _) => name.Text = (accounts.SelectedItem as AccountConfiguration)?.Name;
        TextBlock status = new TextBlock
        {
            Name = "RenameAccountStatus",
            TextWrapping = TextWrapping.Wrap
        };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        Window window = new Window
        {
            Title = Strings.Get("RenameAccount"),
            Width = 480,
            Height = 320,
            MinWidth = 360,
            MinHeight = 260,
            Content = panel
        };
        FormControls.AddActionButton(panel, "SaveAccountName", async () =>
        {
            if (accounts.SelectedItem is not AccountConfiguration account)
            {
                return;
            }
            Failure result = await management.RenameAsync(account.Id, name.Text ?? "", _lifetime.Token);
            if (result != Failure.None)
            {
                status.Text = Strings.Describe(result);
                return;
            }
            await model.InitialiseAsync();
            window.Close();
        }, status);
        FormControls.AddActionButton(panel, "Cancel", () =>
        {
            window.Close();
            return Task.CompletedTask;
        }, status);
        panel.Children.Add(status);
        ShowWindow(window);
        name.Focus();
        name.SelectAll();
    }

    private void RemoveAccount(
        IAccountManagement management,
        AccountId? selected = null
    )
    {
        if (DataContext is not MainWindowViewModel model || model.Accounts.Count == 0)
        {
            return;
        }
        StackPanel panel = new StackPanel

        {
            Spacing = 12
        };
        ComboBox accounts = new ComboBox

        {
            Name = "RemoveAccountSelection",
            ItemsSource = model.Accounts.ToArray(),
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => account is null ? null : new TextBlock

            {
                Text = account.Name + " — " + account.Address
            }),
            SelectedItem = model.Accounts.FirstOrDefault(account => account.Id == (selected ?? model.Scope?.Account))
        };
        Avalonia.Automation.AutomationProperties.SetName(accounts, Strings.RemoveAccount);
        panel.Children.Add(accounts);
        panel.Children.Add(new TextBlock

        {
            Text = Strings.Get("RemoveAccountHint"),
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock status = new TextBlock

        {
            Name = "RemoveAccountStatus",
            TextWrapping = TextWrapping.Wrap
        };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        Window window = new Window

        {
            Title = Strings.RemoveAccount,
            Width = 560,
            Height = 420,
            MinWidth = 360,
            MinHeight = 300,
            Content = panel
        };
        FormControls.AddActionButton(panel, "ConfirmRemoveAccount", async () =>
        {
            if (accounts.SelectedItem is not AccountConfiguration account)
            {
                return;
            }
            if (_compose.Any(compose => compose.Account == account.Id))
            {
                status.Text = Strings.Get("RemoveAccountComposeOpen");
                return;
            }
            if (_removingAccount is not null)
            {
                status.Text = Strings.Describe(Failure.Unavailable);
                return;
            }
            _removingAccount = account.Id;
            IsEnabled = false;
            Failure result;
            try
            {
                result = await management.RemoveAsync(account.Id, _lifetime.Token);
            }
            finally
            {
                _removingAccount = null;
                IsEnabled = !_stopping;
            }
            if (result != Failure.None)
            {
                status.Text = result == Failure.NeedsReview ? Strings.Get("RemoveAccountWorkBlocked") : Strings.Describe(result);
                return;
            }
            await model.InitialiseAsync();
            window.Close();
        }, status);
        FormControls.AddActionButton(panel, "Cancel", () =>
        {
            window.Close();
            return Task.CompletedTask;
        }, status);
        panel.Children.Add(status);
        ShowWindow(window);
    }

    private void ShowWindow(Window window)
    {
        FormControls.ApplyTypography(window, FontSize, FontFamily);
        StackPanel? form = window.Content as StackPanel ?? (window.Content as ScrollViewer)?.Content as StackPanel;
        if (form is not null)
        {
            if (window.Content is ScrollViewer scroll)
            {
                scroll.Content = null;
            }
            window.Content = null;
            form.Margin = new Thickness(24, 12);
            form.Spacing = 12;
            Button[] buttons = form.Children.OfType<Button>().ToArray();
            StackPanel? footer = null;
            if (buttons.Length is > 0 and <= 3)
            {
                footer = new StackPanel { Spacing = 8 };
                WrapPanel actions = new WrapPanel();
                foreach (Button button in buttons)
                {
                    form.Children.Remove(button);
                    button.Margin = new Thickness(0, 0, 8, 8);
                    actions.Children.Add(button);
                }
                footer.Children.Add(actions);
                TextBlock? status = form.Children.OfType<TextBlock>().LastOrDefault(block =>
                    block.Name?.EndsWith("Status", StringComparison.Ordinal) == true);
                if (status is not null)
                {
                    form.Children.Remove(status);
                    footer.Children.Add(status);
                }
            }
            window.Content = FormControls.Page(window.Title ?? "", form, footer);
        }
        IInputElement? previous = FocusManager?.GetFocusedElement();
        window.Closed += (_, _) => previous?.Focus();
        window.Show(this);
    }

    private void SystemColoursChanged(object? sender, Avalonia.Platform.PlatformColorValues values)
    {
        Dispatcher.UIThread.Post(() => ApplyAppearance(_settings));
    }

    private Task RefreshFromControlsAsync()
    {
        return RefreshFromControlsAsync(onlyIfInvalidated: false);
    }

    private Task RefreshFromControlsAsync(bool onlyIfInvalidated)
    {
        if (DataContext is not MainWindowViewModel model || model.UpdatingNavigation)
        {
            return Task.CompletedTask;
        }

        model.Scope = (this.FindControl<TreeView>("ScopeList")!.SelectedItem as NavigationItemViewModel)?.Scope ?? model.Scope;
        model.Terms = this.FindControl<TextBox>("SearchBox")!.Text ?? "";
        return model.RefreshAsync(next: false, onlyIfInvalidated: onlyIfInvalidated);
    }

    private void Wire(string name, Func<Task> action)
    {
        EventHandler<RoutedEventArgs> handler = (_, _) => Track(action);
        Control? control = this.FindControl<Control>(name);
        if (control is Button button)
        {
            button.Click += handler;
        }
        else if (control is MenuItem item)
        {
            item.Click += handler;
        }
        else
        {
            throw new InvalidOperationException("Missing action control: " + name);
        }
    }

    private void Track(Func<Task> action)
    {
        if (_stopping)
        {
            return;
        }

        _work.RemoveAll(item => item.IsCompleted);
        Task task = RunAsync();
        _work.Add(task);
        async Task RunAsync()
        {
            try
            {
                await action();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (_stopping)
                {
                    if (DataContext is MainWindowViewModel model)
                    {
                        model.ReportSendStatus(Strings.DraftFailed);
                    }
                }
                else
                {
                    ShowWindow(new Window
                    {
                        Title = Strings.DraftFailed,
                        Content = new TextBlock
                        {
                            Text = Strings.DraftFailed
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SaveAttachmentAsync()
    {
        await SaveAttachmentAsync(StorageProvider);
    }

    private void Move()
    {
        if (DataContext is not MainWindowViewModel model || !model.CanMutate())
        {
            return;
        }

        MailboxSnapshot[] destinations = model.Mailboxes.Where(item => item.Account == model.Selected!.Account &&
            item.Selectable && item.Writable).ToArray();
        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12
        };
        ComboBox folders = new ComboBox
        {
            ItemsSource = destinations,
            SelectedIndex = -1,
            ItemTemplate = new FuncDataTemplate<MailboxSnapshot>((item, _) => item is null ? null : new TextBlock
            {
                Text = item.Name
            })
        };
        Avalonia.Automation.AutomationProperties.SetName(folders, Strings.Get("Folder"));
        panel.Children.Add(folders);
        TextBlock status = new TextBlock();
        FormControls.AddActionButton(panel, "Move", async () =>
        {
            if (folders.SelectedItem is MailboxSnapshot destination)
            {
                await model.MoveAsync(destination);
                status.Text = model.Status;
            }
        }, status);
        panel.Children.Add(status);
        ShowWindow(new Window
        {
            Title = Strings.Move,
            Width = 480,
            Height = 240,
            Content = panel
        });
    }

    private async Task RefreshDraftListAsync(
        AccountId account
    )
    {
        Window? window = OwnedWindows.FirstOrDefault(item => item.Name == "AccountDrafts" + account.Value);
        ListBox? list = window?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(item => item.Name == "AccountDraftList");
        if (_application is null || list?.Tag is not Dictionary<Guid, string> subjects)
        {
            return;
        }
        Guid? selected = (list.SelectedItem as DraftSnapshot)?.Id;
        ImmutableArray<DraftSnapshot> drafts = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
        Dictionary<Guid, string> refreshed = [];
        foreach (DraftSnapshot draft in drafts)
        {
            Outcome<ComposeContent> content = await _application.ReadDraftAsync(draft, _lifetime.Token);
            refreshed[draft.Id] = content.IsSuccess ? (string.IsNullOrWhiteSpace(content.Value!.Subject) ? Strings.Get("NoSubject") : content.Value.Subject) : Strings.Describe(content.Failure);
        }
        subjects.Clear();
        foreach (KeyValuePair<Guid, string> subject in refreshed)
        {
            subjects.Add(subject.Key, subject.Value);
        }
        list.ItemsSource = drafts;
        list.SelectedItem = drafts.FirstOrDefault(item => item.Id == selected);
    }

    private async Task ReviewVersionsAsync(
        DraftSnapshot draft
    )
    {
        if (_application is null)
        {
            return;
        }

        DraftReviewSnapshot? review = null;
        Failure reviewFailure = Failure.None;
        if (draft.Remote is not null && (draft.Held || draft.Conflict) && DataContext is MainWindowViewModel model &&
            model.Accounts.Any(item => item.Id == draft.Account && item.ProviderKind == "Google"))
        {
            Outcome<DraftReviewSnapshot> prepared = await _application.ReviewDraftAsync(draft, _lifetime.Token);
            review = prepared.Value;
            reviewFailure = prepared.Failure;
        }
        ImmutableArray<DraftVersionSnapshot> versions = await _application.Catalog.DraftVersionsAsync(
            draft.Account,
            draft.Id,
            _lifetime.Token
        );
        StackPanel panel = new StackPanel

        {
            Margin = new Thickness(12),
            Spacing = 8
        };
        panel.Children.Add(new TextBlock

        {
            Text = Strings.Get("VersionReviewHint"),
            TextWrapping = TextWrapping.Wrap
        });
        ComboBox choice = new ComboBox

        {
            ItemsSource = versions.Select((item, index) => Strings.Get(item.Origin) + " " + (index + 1)).ToArray(),
            SelectedIndex = -1
        };
        Avalonia.Automation.AutomationProperties.SetName(choice, Strings.Get("ReviewVersions"));
        panel.Children.Add(choice);
        TextBox preview = FormControls.AddField(panel, "Preview");
        preview.IsReadOnly = true;
        preview.AcceptsReturn = true;
        preview.TextWrapping = TextWrapping.Wrap;
        preview.MinHeight = 240;
        TextBlock status = new TextBlock

        {
            Name = "DraftReviewStatus",
            Text = reviewFailure == Failure.None ? "" : Strings.Describe(reviewFailure),
            TextWrapping = TextWrapping.Wrap
        };
        ComposeContent? selected = null;
        choice.SelectionChanged += (_, _) => Track(async () =>
        {
            selected = null;
            int index = choice.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            Outcome<ComposeContent> content = await _application.ReadDraftAsync(draft with
            {
                Content = versions[index].Content
            }, _lifetime.Token);
            if (choice.SelectedIndex != index)
            {
                return;
            }

            selected = content.Value;
            preview.Text = content.IsSuccess ? content.Value!.Subject + "\n\n" + content.Value.PlainText :
                Strings.Describe(content.Failure);
            if (content.IsSuccess && !content.Value!.Attachments.IsDefaultOrEmpty)
            {
                preview.Text += "\n\n" + Strings.Get("Attachments") + ":\n" +
                    string.Join("\n", content.Value.Attachments.Select(item => item.Name));
            }
        });
        choice.SelectedIndex = Array.FindIndex(versions.ToArray(), item => item.Content == draft.Content);
        FormControls.AddActionButton(panel, "UseReviewedVersion", () =>
        {
            if (selected is not null)
            {
                OpenCompose(activation: selected, targetAccount: draft.Account);
                status.Text = Strings.Get("VersionsPreserved");
            }

            return Task.CompletedTask;
        }, status);
        Window window = new Window

        {
            Title = Strings.Get("ReviewVersions"),
            Width = 760,
            Height = 640
        };
        CancellationTokenSource reviewLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        window.Closed += (_, _) =>
        {
            reviewLifetime.Cancel();
            // FormControls tracks the action on this owner; FlushComposeAsync drains it before profile shutdown.
            reviewLifetime.Dispose();
        };
        if (review is not null)
        {
            panel.Children.Add(new TextBlock

            {
                Text = Strings.Get("CopyReviewedDraftHint"),
                TextWrapping = TextWrapping.Wrap
            });
            bool decided = false;
            FormControls.AddActionButton(panel, "CopyReviewedDraft", async () =>
            {
                if (decided)
                {
                    return;
                }
                decided = true;
                Failure result = await _application.CopyReviewedDraftAsync(review, reviewLifetime.Token);
                status.Text = Strings.Get(result == Failure.None ? "ReviewedDraftCreated" : "ReviewedDraftHeld") +
                    " " + Strings.Describe(result);
            }, status);
        }
        FormControls.AddActionButton(panel, "PostponeDraftReview", () =>
        {
            window.Close();
            return Task.CompletedTask;
        }, status);
        panel.Children.Add(status);
        window.Content = new ScrollViewer

        {
            Content = panel
        };
        ShowWindow(window);
    }

}
