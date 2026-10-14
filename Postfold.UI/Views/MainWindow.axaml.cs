using Avalonia;
using Avalonia.Controls;
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
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
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
    private IAccountSetup? _setup;
    private IAccountManagement? _management;
    private IClientIdentity? _identity;
    private IDesktopIntegration? _desktop;
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
        messageList.Tapped += (_, args) =>
        {
            if (args.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MessageSummary row &&
                DataContext is MainWindowViewModel model)
            {
                Track(() => model.OpenMessageAsync(row.Message));
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

    public void ConfigureIdentity(IClientIdentity identity)
    {
        _identity = identity;
    }

    public void ConfigureHtml(IHtmlReadingService reader)
    {
        this.FindControl<MenuItem>("HtmlButton")!.IsEnabled = reader.IsQualified;
        this.FindControl<MenuItem>("ImagesButton")!.IsEnabled = reader.IsQualified;
        Wire("HtmlButton", async () =>
        {
            if (DataContext is MainWindowViewModel model && model.Selected is MessageSnapshot message)
            {
                Failure result = await reader.ReadAsync(message, true, _lifetime.Token);
                if (model.Selected?.Account == message.Account && model.Selected.Id == message.Id &&
                    model.Selected.Provider == message.Provider)
                {
                    model.ReportReadingResult(result);
                }
            }
        });
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
        IAccountManagement? management = null
    )
    {
        _management = management;
        _application = application;
        _setup = setup;
        _desktop = desktop;
        Wire("ComposeButton", () => { OpenCompose(); return Task.CompletedTask; });
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
        Wire("PlainButton", () => { SetHtmlSurface(null); return Task.CompletedTask; });
        Wire("CheckEmptyAttachmentsButton", async () =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                await model.CheckAttachmentsAsync();
            }
        });
        Wire("CheckAttachmentsButton", async () =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                await model.CheckAttachmentsAsync();
            }
        });
        Wire("MoveButton", () => { Move(); return Task.CompletedTask; });
        Wire("SaveAttachmentButton", SaveAttachmentAsync);
        this.FindControl<Button>("SaveAttachmentButton")!.IsEnabled = false;
        this.FindControl<ListBox>("AttachmentList")!.SelectionChanged += (_, _) =>
        {
            this.FindControl<Button>("SaveAttachmentButton")!.IsEnabled =
                this.FindControl<ListBox>("AttachmentList")!.SelectedItem is AttachmentSnapshot;
        };
        this.FindControl<ListBox>("AttachmentList")!.DoubleTapped += (_, _) => Track(SaveAttachmentAsync);
        this.FindControl<ListBox>("AttachmentList")!.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Track(SaveAttachmentAsync);
                args.Handled = true;
            }
        };
        this.FindControl<TreeView>("ScopeList")!.SelectionChanged += (_, _) => Track(RefreshFromControlsAsync);
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
            model.ReportSendStatus(Strings.Get("ProfileStartupHint"));
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
        AccountId? targetAccount = null
    )
    {
        MainWindowViewModel? model = DataContext as MainWindowViewModel;
        if (kind is not null && (model?.Selected is null || model.Content is null))
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
            account.Identity?.Sender ?? account.Outgoing.Username,
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
            Sender = account.Identity?.Sender ?? account.Outgoing.Username
        };

        ComposeWindow window = new ComposeWindow(
            _application,
            account.Id,
            content,
            id,
            conflict,
            predecessor,
            kind == "Forward" ? model?.Selected : null,
            kind == "Forward" && model is not null ? model.Attachments.ToImmutableArray() : []
        );
        window.FontFamily = FontFamily;
        window.FontSize = FontSize;
        _compose.Add(window);
        window.SendStatusChanged += status => model?.ReportSendStatus(account.Name + " — " + status);
        window.Closed += (_, _) => { _compose.Remove(window); this.FindControl<Button>("ComposeButton")!.Focus(); };
        ShowWindow(window);
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
            if (!await window.PrepareShutdownAsync())
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

    public async Task CommandsAsync(AccountId account)
    {
        if (_application is null || DataContext is not MainWindowViewModel model ||
            !model.Accounts.Any(item => item.Id == account))
        {
            return;
        }

        ListBox list = new ListBox
        {
            ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token),
            Name = "PendingCommands",
            MaxHeight = 280,
            ItemTemplate = new FuncDataTemplate<CommandSnapshot>((command, _) => command is null ? null : new TextBlock
            {
                Text = command.Subject + "\n" + Strings.Describe(command.Entry.Intent.Kind) + " — " +
                                            Strings.Describe(command.Entry.State) + "\n" + Strings.Describe(command.Entry.Evidence),
                TextWrapping = TextWrapping.Wrap
            })
        };
        Avalonia.Automation.AutomationProperties.SetName(list, Strings.Commands);
        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8
        };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("CommandReviewHint"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(list);
        CheckBox consent = new CheckBox
        {
            Content = Strings.Get("CommandRetryConsent"),
            IsChecked = false
        };
        list.SelectionChanged += (_, _) => consent.IsChecked = false;
        panel.Children.Add(consent);
        StackPanel footer = new StackPanel { Spacing = 8 };
        WrapPanel actions = new WrapPanel();
        footer.Children.Add(actions);
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(actions, "RetryCommand", async () =>
        {
            if (list.SelectedItem is CommandSnapshot command && consent.IsChecked == true)
            {
                status.Text = Strings.Describe(await _application.ResolveCommandAsync(command.Entry, true, _lifetime.Token));
                consent.IsChecked = false;
                list.ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token);
            }
            else
            {
                status.Text = Strings.Get("CommandDecisionRequired");
            }
        }, status);
        FormControls.AddActionButton(actions, "DismissCommand", async () =>
        {
            if (list.SelectedItem is CommandSnapshot command)
            {
                status.Text = Strings.Describe(await _application.ResolveCommandAsync(command.Entry, false, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token);
            }
        }, status);
        FormControls.AddActionButton(actions, "Refresh", async () =>
        {
            list.ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token);
        }, status);
        footer.Children.Add(status);
        Control sends = await SendIssuesAsync(account);
        ListBox sendList = ((StackPanel)sends).Children.OfType<ListBox>().Single();
        TabControl tabs = new TabControl
        {
            Margin = new Thickness(24, 0, 24, 16),
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = Strings.Get("MailNeedingAttention"),
                    Content = FormControls.Page("", sends)
                },
                new TabItem
                {
                    Header = Strings.Get("FolderChanges"),
                    Content = FormControls.Page("", panel, footer)
                }
            },
            SelectedIndex = sendList.ItemCount > 0 ? 0 : 1
        };
        DockPanel page = new DockPanel();
        TextBlock heading = new TextBlock
        {
            Text = Strings.Commands,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(24, 24, 24, 16)
        };
        heading.Bind(TextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SubjectFontSize"));
        DockPanel.SetDock(heading, Dock.Top);
        page.Children.Add(heading);
        page.Children.Add(tabs);
        ShowWindow(new Window
        {
            Title = Strings.Commands,
            Width = 760,
            Height = 520,
            MinWidth = 480,
            MinHeight = 480,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = page
        });
    }

    public async Task DraftsAsync(AccountId account)
    {
        if (_application is null || DataContext is not MainWindowViewModel model || !model.Accounts.Any(item => item.Id == account))
        {
            return;
        }

        ImmutableArray<DraftSnapshot> drafts = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
        ListBox list = new ListBox
        {
            ItemsSource = drafts,
            ItemTemplate = new FuncDataTemplate<DraftSnapshot>((draft, _) => draft is null ? null : new TextBlock
            {
                Text = Strings.Get("DraftRevision") + " " + draft.Revision + " — " +
                                            Strings.Get(draft.Conflict || draft.Held ? "Conflict" : "Saved"),
                TextWrapping = TextWrapping.Wrap
            })
        };
        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8
        };
        panel.Children.Add(list);
        StackPanel footer = new StackPanel { Spacing = 8 };
        WrapPanel actions = new WrapPanel();
        footer.Children.Add(actions);
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(actions, "OpenDraft", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
                ImmutableArray<DraftSnapshot> current = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
                list.ItemsSource = current;
                if (!current.Any(item => item.Id == draft.Id))
                {
                    status.Text = Strings.Get("FrozenDraft");
                    return;
                }
                Outcome<ComposeContent> content = await _application.ReadDraftAsync(draft, _lifetime.Token);
                if (content.IsSuccess)
                {
                    OpenCompose(
                        activation: content.Value,
                        id: draft.Id,
                        conflict: draft.Conflict || draft.Held,
                        targetAccount: draft.Account
                    );
                }
            }
        }, status);
        FormControls.AddActionButton(actions, "PushDraft", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
                status.Text = Strings.Describe(await _application.PushDraftAsync(draft.Account, draft.Id, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
            }
        }, status);
        FormControls.AddActionButton(actions, "ReviewVersions", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
                await ReviewVersionsAsync(draft);
            }
        }, status);
        footer.Children.Add(status);
        Window window = new Window
        {
            Title = Strings.Drafts,
            Width = 760,
            Height = 480,
            Content = FormControls.Page(Strings.Drafts, panel, footer)
        };
        ShowWindow(window);
    }

    private void MessagePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _dragPress = null;
        _dragMessage = null;
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
            model.Selected.Id == message.Id && model.CanMove)
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

    private async Task<Control> SendIssuesAsync(AccountId account)
    {
        IMailApplication application = _application!;
        StackPanel panel = new StackPanel
        {
            Spacing = 12
        };
        ListBox list = new ListBox
        {
            Name = "SendIssues",
            MaxHeight = 240,
            ItemTemplate = new FuncDataTemplate<OutboxEntry>((entry, _) => entry is null ? null : new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(entry.Submission.Subject) ? Strings.Get("NoSubject") : entry.Submission.Subject) +
                    "\n" + string.Join("; ", entry.Submission.Recipients) + "\n" + Strings.Get("SendStatus." + entry.State) +
                    (entry.Evidence is { Failure: not (Failure.None or Failure.NeedsReview) } evidence
                        ? "\n" + Strings.Describe(evidence.Failure) : ""),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            })
        };
        Avalonia.Automation.AutomationProperties.SetName(list, Strings.Get("MailNeedingAttention"));
        list.Classes.Add("mail");
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        StackPanel details = new StackPanel
        {
            Spacing = 8
        };
        TextBlock empty = new TextBlock
        {
            Text = Strings.Get("NoSendIssues"),
            TextWrapping = TextWrapping.Wrap
        };
        async Task RefreshAsync()
        {
            ImmutableArray<OutboxEntry> entries = await application.Catalog.OutboxAsync(account, _lifetime.Token);
            list.ItemsSource = entries.IsDefault ? ImmutableArray<OutboxEntry>.Empty
                : entries.Where(entry => entry.State != SubmissionState.Completed).ToImmutableArray();
            empty.IsVisible = list.ItemCount == 0;
            details.Children.Clear();
        }
        list.SelectionChanged += (_, _) =>
        {
            details.Children.Clear();
            if (list.SelectedItem is not OutboxEntry entry)
            {
                return;
            }

            WrapPanel actions = new WrapPanel();
            details.Children.Add(actions);
            if (entry.State == SubmissionState.Queued)
            {
                FormControls.AddActionButton(actions, "Send", async () =>
                {
                    status.Text = Strings.Get("Sending");
                    status.Text = Strings.Describe(await application.SubmitAsync(account, entry.Submission.Id, _lifetime.Token));
                    await RefreshAsync();
                }, status);
                FormControls.AddActionButton(actions, "CancelSend", async () =>
                {
                    status.Text = Strings.Describe(await application.CancelQueuedAsync(entry, _lifetime.Token));
                    await RefreshAsync();
                }, status);
            }
            else if (entry.State is SubmissionState.Accepted or SubmissionState.SentCopyPending)
            {
                CheckBox consent = new CheckBox
                {
                    Content = Strings.Get("SentCopyConsent")
                };
                consent.IsVisible = entry.State == SubmissionState.SentCopyPending;
                details.Children.Add(consent);
                FormControls.AddActionButton(actions, "SaveSentCopy", async () =>
                {
                    if (entry.State == SubmissionState.SentCopyPending && consent.IsChecked != true)
                    {
                        status.Text = Strings.Describe(Failure.NeedsReview);
                        return;
                    }

                    status.Text = Strings.Describe(entry.State == SubmissionState.Accepted
                        ? await application.StoreSentCopyAsync(entry, _lifetime.Token)
                        : await application.RetrySentCopyAsync(entry, _lifetime.Token));
                    await RefreshAsync();
                }, status);
            }
            else if (entry.State is SubmissionState.Uncertain or SubmissionState.PartiallyAccepted or
                SubmissionState.Rejected or SubmissionState.Cancelled)
            {
                bool uncertain = entry.State is SubmissionState.Uncertain or SubmissionState.PartiallyAccepted;
                if (entry.Evidence is not null)
                {
                    details.Children.Add(new TextBlock
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
                details.Children.Add(consent);
                FormControls.AddActionButton(actions, "ReviewMessage", async () =>
                {
                    if (uncertain && consent.IsChecked != true)
                    {
                        status.Text = Strings.Describe(Failure.NeedsReview);
                        return;
                    }

                    Outcome<ComposeContent> content = await application.PrepareResendAsync(entry, _lifetime.Token);
                    if (content.IsSuccess)
                    {
                        OpenCompose(
                            activation: content.Value,
                            predecessor: entry.Submission.Id,
                            targetAccount: account
                        );
                        consent.IsChecked = false;
                    }
                    else
                    {
                        status.Text = Strings.Describe(content.Failure);
                    }
                }, status);
            }
        };
        panel.Children.Add(list);
        panel.Children.Add(empty);
        panel.Children.Add(details);
        FormControls.AddActionButton(panel, "RefreshSends", RefreshAsync, status);
        panel.Children.Add(status);
        await RefreshAsync();
        return panel;
    }

    private async Task CredentialsAsync(IAccountSetup setup, AccountId selectedAccount)
    {
        if (_application is null)
        {
            return;
        }

        ImmutableArray<AccountConfiguration> accounts = await _application.Catalog.AccountsAsync(_lifetime.Token);
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
                credentials.Shared.IsChecked = selected.Incoming.Username == selected.Outgoing.Username;
            }
        }
        account.SelectionChanged += (_, _) => SelectAccount();
        SelectAccount();
        CheckBox session = new CheckBox
        {
            Content = Strings.SessionOnly,
            IsChecked = false
        };
        form.Children.Add(session);
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
                    session.IsChecked == true,
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
                _setup is not null
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
            Text = Strings.Get("ProfileStartupHint"),
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

    private void AllFilterClicked(object? sender, RoutedEventArgs args)
    {
        Track(() => SelectFilterAsync(false));
    }

    private void UnreadFilterClicked(object? sender, RoutedEventArgs args)
    {
        Track(() => SelectFilterAsync(!(DataContext as MainWindowViewModel)?.UnreadOnly ?? true));
    }

    private Task SelectFilterAsync(bool unread)
    {
        if (DataContext is not MainWindowViewModel model || model.UpdatingNavigation)
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

        string[] names = ["ReplyButton", "ReplyAllButton", "ForwardButton", "ReadButton", "TrashButton"];
        Button[] commands = names.Select(name => this.FindControl<Button>(name)!).ToArray();
        foreach (Button command in commands)
        {
            command.IsVisible = true;
            command.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        Button more = this.FindControl<Button>("ReaderMoreButton")!;
        more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double available = Math.Max(0, pane.Bounds.Width - 32 - more.DesiredSize.Width);
        double required = commands.Sum(command => command.DesiredSize.Width) + 17;
        foreach (int index in new[] { 4, 3, 2, 1 })
        {
            if (required <= available)
            {
                break;
            }
            required -= commands[index].DesiredSize.Width;
            if (commands[index].IsKeyboardFocusWithin)
            {
                more.Focus();
            }
            commands[index].IsVisible = false;
        }
        this.FindControl<Border>("ReaderActionDivider")!.IsVisible = commands[3].IsVisible || commands[4].IsVisible;

        foreach ((int index, string menu) in new[]
        {
            (1, "OverflowReplyAllButton"), (2, "OverflowForwardButton"),
            (3, "OverflowReadButton"), (4, "OverflowTrashButton")
        })
        {
            this.FindControl<MenuItem>(menu)!.IsVisible = !commands[index].IsVisible;
        }
        this.FindControl<ScrollViewer>("HtmlHeaderScroll")!.MaxHeight = Math.Max(64, Math.Min(240, pane.Bounds.Height * 0.3));
        this.FindControl<ScrollViewer>("HtmlAttachmentScroll")!.MaxHeight = Math.Max(48, Math.Min(180, pane.Bounds.Height * 0.24));
    }

    private void PagingStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.HasMore))
        {
            UpdatePagination();
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
        this.FindControl<StackPanel>("ListFilters")!.IsVisible = pane.Bounds.Width >= 420 * FontSize / 16;
    }

    private void AccountHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
    {
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

    private void RemoveAccount(IAccountManagement management, AccountId? selected = null)
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
                Text = account.Name + " — " + account.Incoming.Host
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
        if (DataContext is not MainWindowViewModel model || model.UpdatingNavigation)
        {
            return Task.CompletedTask;
        }

        model.Scope = (this.FindControl<TreeView>("ScopeList")!.SelectedItem as NavigationItemViewModel)?.Scope ?? model.Scope;
        model.Terms = this.FindControl<TextBox>("SearchBox")!.Text ?? "";
        return model.RefreshAsync(false);
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
            catch (IOException)
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

    private async Task ReviewVersionsAsync(DraftSnapshot draft)
    {
        if (_application is null)
        {
            return;
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
        });
        FormControls.AddActionButton(panel, "UseReviewedVersion", () =>
        {
            if (selected is not null)
            {
                OpenCompose(activation: selected, targetAccount: draft.Account);
                status.Text = Strings.Get("VersionsPreserved");
            }

            return Task.CompletedTask;
        }, status);
        panel.Children.Add(status);
        ShowWindow(new Window
        {
            Title = Strings.Get("ReviewVersions"),
            Width = 760,
            Height = 560,
            Content = new ScrollViewer
            {
                Content = panel
            }
        });
    }

}
