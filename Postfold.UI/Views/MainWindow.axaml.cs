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
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

public sealed partial class MainWindow : Window
{
    private readonly List<ComposeWindow> _compose = [];
    private readonly List<Task> _work = [];
    private CancellationTokenSource _lifetime = new CancellationTokenSource();
    private bool _stopping;
    private IClientAppearance? _appearance;
    private AppearanceSettings _settings = AppearanceSettings.Default;
    private IMailApplication? _application;
    private IAccountSetup? _setup;
    private IAccountManagement? _management;
    private readonly DispatcherTimer _dates = new DispatcherTimer
    {
        Interval = TimeSpan.FromMinutes(1)
    };
    private string? _layoutPreview;
    private AccountId? _removingAccount;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<DockPanel>("PreviewPane")!.SizeChanged += (_, _) => AdjustReaderViewport();
        this.FindControl<DockPanel>("NavigationPane")!.SizeChanged += (_, _) => AdjustPaneToolbars();
        this.FindControl<DockPanel>("MessagePane")!.SizeChanged += (_, _) => AdjustPaneToolbars();
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
            string preview = Bounds.Width < 1050 && _settings.Preview == "Right" ? "Bottom" : _settings.Preview;
            if (_layoutPreview != preview)
            {
                ReflowPreview(preview);
            }
        };
    }

    public void SetHtmlSurface(Control? surface)
    {
        ContentControl html = this.FindControl<ContentControl>("HtmlSurface")!;
        if (surface is not null)
        {
            html.Content = surface;
        }
        html.IsVisible = surface is not null;
        this.FindControl<ScrollViewer>("PlainPreviewScroll")!.IsVisible = surface is null;
    }

    public void ReleaseHtmlSurface()
    {
        this.FindControl<ContentControl>("HtmlSurface")!.Content = null;
        SetHtmlSurface(null);
    }

    public void ConfigureIdentity(IClientIdentity identity)
    {
        this.FindControl<Button>("IdentityButton")!.IsEnabled = true;
        Wire("IdentityButton", () =>
        {
            if (DataContext is not MainWindowViewModel model || model.Accounts.Count == 0)
            {
                return Task.CompletedTask;
            }

            StackPanel panel = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8
            };
            ComboBox accounts = new ComboBox
            {
                ItemsSource = model.Accounts,
                ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => new TextBlock
                {
                    Text = account!.Name
                }),
                SelectedItem = model.Accounts.FirstOrDefault(account => account.Id == (model.Scope?.Account ?? model.Selected?.Account)) ??
                    model.Accounts[0]
            };
            Avalonia.Automation.AutomationProperties.SetName(accounts, Strings.Get("IdentityAccount"));
            panel.Children.Add(accounts);
            TextBox sender = FormControls.AddField(panel, "Sender");
            sender.MaxLength = 320;
            TextBox signature = FormControls.AddField(panel, "Signature");
            signature.MaxLength = 8192;
            signature.AcceptsReturn = true;
            signature.MinHeight = 120;
            void Load()
            {
                if (accounts.SelectedItem is AccountConfiguration account)
                {
                    sender.Text = account.Identity?.Sender ?? account.Outgoing.Username;
                    signature.Text = account.Identity?.Signature ?? "";
                }
            }

            accounts.SelectionChanged += (_, _) => Load();
            Load();
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Get("IdentityHint"),
                TextWrapping = TextWrapping.Wrap
            });
            TextBlock status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap
            };
            FormControls.AddActionButton(panel, "Save", async () =>
            {
                if (accounts.SelectedItem is AccountConfiguration account)
                {
                    Failure result = await identity.SaveAsync(account.Id, new ComposeIdentity(
                        sender.Text ?? "",
                        signature.Text ?? ""
                    ), _lifetime.Token);
                    if (result == Failure.None)
                    {
                        await model.InitialiseAsync();
                        accounts.SelectedItem = model.Accounts.FirstOrDefault(item => item.Id == account.Id);
                    }

                    status.Text = Strings.Describe(result);
                }
            }, status);
            panel.Children.Add(status);
            ShowWindow(new Window
            {
                Title = Strings.IdentitySettings,
                Width = 620,
                Height = 500,
                FontFamily = FontFamily,
                FontSize = FontSize,
                Content = new ScrollViewer
                {
                    Content = panel
                }
            });
            return Task.CompletedTask;
        });
    }

    public void ConfigureHtml(IHtmlReadingService reader)
    {
        this.FindControl<Button>("HtmlButton")!.IsEnabled = reader.IsQualified;
        this.FindControl<Button>("ImagesButton")!.IsEnabled = reader.IsQualified;
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
        this.FindControl<Button>("RenameAccountButton")!.IsEnabled = management is not null;
        Wire("RenameAccountButton", () =>
        {
            if (management is not null)
            {
                RenameAccount(management);
            }
            return Task.CompletedTask;
        });
        _management = management;
        _application = application;
        _setup = setup;
        this.FindControl<Button>("RemoveAccountButton")!.IsEnabled = management is not null;
        Wire("RemoveAccountButton", () =>
        {
            if (management is not null)
            {
                RemoveAccount(management);
            }
            return Task.CompletedTask;
        });
        this.FindControl<Button>("DesktopButton")!.IsEnabled = desktop is not null;
        Wire("DesktopButton", async () =>
        {
            if (desktop is not null)
            {
                await DesktopSettingsAsync(desktop);
            }
        });
        Wire("ComposeButton", () => { OpenCompose(); return Task.CompletedTask; });
        Wire("ReplyButton", () => { OpenCompose("Reply"); return Task.CompletedTask; });
        Wire("ReplyAllButton", () => { OpenCompose("ReplyAll"); return Task.CompletedTask; });
        Wire("ForwardButton", () => { OpenCompose("Forward"); return Task.CompletedTask; });
        Wire("BottomReplyButton", () => { OpenCompose("Reply"); return Task.CompletedTask; });
        Wire("BottomReplyAllButton", () => { OpenCompose("ReplyAll"); return Task.CompletedTask; });
        Wire("BottomForwardButton", () => { OpenCompose("Forward"); return Task.CompletedTask; });
        Wire("AllFilterButton", async () =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                model.UnreadOnly = false;
                model.KnownAttachmentsOnly = false;
                await model.RefreshAsync(false);
            }
        });
        foreach (string filter in new[] { "UnreadFilter", "AttachmentFilter" })
        {
            this.FindControl<CheckBox>(filter)!.IsCheckedChanged += (_, _) => Track(RefreshFromControlsAsync);
        }
        Wire("SetupButton", () =>
        {
            OpenAccountSetup();
            return Task.CompletedTask;
        });
        this.FindControl<Button>("SetupButton")!.IsEnabled = setup is not null;
        this.FindControl<Button>("CredentialsButton")!.IsEnabled = setup is not null;
        Wire("CredentialsButton", async () =>
        {
            if (setup is not null)
            {
                await CredentialsAsync(setup);
            }
        });
        Wire("DraftsButton", DraftsAsync);
        Wire("OutboxButton", OutboxAsync);
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
        Wire("CommandsButton", CommandsAsync);
        Wire("MoveButton", () => { Move(); return Task.CompletedTask; });
        Wire("SaveAttachmentButton", SaveAttachmentAsync);
        Wire("AppearanceButton", () => { Appearance(); return Task.CompletedTask; });
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
        Resources["SubjectFontSize"] = settings.FontSize * 1.5;
        FontFamily = string.IsNullOrWhiteSpace(settings.Font) ? FontFamily.Default : new FontFamily(settings.Font);
        Resources["MessagePadding"] = settings.Density == "Compact" ? new Thickness(8, 4) : new Thickness(12, 10);
        foreach (Window child in OwnedWindows)
        {
            child.FontSize = FontSize;
            child.FontFamily = FontFamily;
        }

        ReflowPreview(Bounds.Width > 0 && Bounds.Width < 1050 && settings.Preview == "Right" ? "Bottom" : settings.Preview);
    }

    public void OpenCompose(
        string? kind = null,
        ComposeContent? activation = null,
        Guid? id = null,
        bool conflict = false,
        Guid? predecessor = null,
        AccountId? targetAccount = null,
        bool applyIdentitySignature = false
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
        ComposeWindow? existing = source is Guid parent ?
            _compose.FirstOrDefault(window => window.Account == account.Id && window.SourceSubmission == parent) : null;
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
            Guid.NewGuid().ToString("N") + "@postfold.invalid",
            Signature: account.Identity?.Signature ?? ""
        );
        if (applyIdentitySignature)
        {
            content = content with
            {
                Signature = account.Identity?.Signature ?? ""
            };
        }
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

        if (string.IsNullOrEmpty(content.Sender))
        {
            content = content with
            {
                Sender = account.Identity?.Sender ?? account.Outgoing.Username
            };
        }

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

    private async Task CredentialsAsync(IAccountSetup setup)
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
            SelectedIndex = accounts.IsEmpty ? -1 : 0,
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((item, _) => new TextBlock
            {
                Text = item!.Name
            })
        };
        Avalonia.Automation.AutomationProperties.SetName(account, Strings.Get("Account"));
        form.Children.Add(new TextBlock
        {
            Text = Strings.Get("CredentialsHint"),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(account);
        TextBlock usernames = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        form.Children.Add(usernames);
        CredentialFields credentials = new CredentialFields(form, includeUsernames: false);
        void SelectAccount()
        {
            credentials.Clear();
            if (account.SelectedItem is AccountConfiguration selected)
            {
                usernames.Text = Strings.CredentialUsernames + "\n" +
                    Strings.Get("IncomingUsername") + ": " + selected.Incoming.Username + "\n" +
                    Strings.Get("OutgoingUsername") + ": " + selected.Outgoing.Username;
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

    private void ReflowPreview(string position)
    {
        _layoutPreview = position;
        Grid panes = this.FindControl<Grid>("Panes")!;
        Control navigation = this.FindControl<DockPanel>("NavigationPane")!;
        Control navigationSplitter = this.FindControl<GridSplitter>("NavigationSplitter")!;
        Control messages = this.FindControl<DockPanel>("MessagePane")!;
        GridSplitter splitter = this.FindControl<GridSplitter>("PreviewSplitter")!;
        Control preview = this.FindControl<DockPanel>("PreviewPane")!;
        bool bottom = position == "Bottom";
        bool hidden = position == "Hidden";
        this.FindControl<Expander>("ReaderAttachments")!.IsExpanded = !bottom;
        panes.ColumnDefinitions = new ColumnDefinitions(bottom || hidden ? "250,5,*" : "250,5,*,5,1.3*");
        panes.RowDefinitions = new RowDefinitions(bottom ? "*,5,*" : "*");
        Grid.SetRowSpan(navigation, bottom ? 3 : 1);
        Grid.SetRowSpan(navigationSplitter, bottom ? 3 : 1);
        Grid.SetRow(messages, 0);
        Grid.SetColumn(preview, bottom || hidden ? 2 : 4);
        Grid.SetRow(preview, bottom ? 2 : 0);
        Grid.SetColumn(splitter, bottom || hidden ? 2 : 3);
        Grid.SetRow(splitter, bottom ? 1 : 0);
        splitter.ResizeDirection = bottom ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        splitter.IsVisible = !hidden;
        preview.IsVisible = !hidden;
        preview.Margin = new Thickness(0);
        AdjustReaderViewport();
    }

    private void AdjustReaderViewport()
    {
        double height = this.FindControl<DockPanel>("PreviewPane")!.Bounds.Height;
        if (height <= 0)
        {
            return;
        }
        double width = this.FindControl<DockPanel>("PreviewPane")!.Bounds.Width;
        this.FindControl<UniformGrid>("ReaderActions")!.Columns = Math.Clamp((int)(width / (FontSize * 8)), 1, 5);
        this.FindControl<ScrollViewer>("ReaderHeaderScroll")!.MaxHeight = Math.Max(48, Math.Min(260, height * 0.32));
        this.FindControl<ScrollViewer>("ReaderAttachmentScroll")!.MaxHeight = Math.Max(48, Math.Min(180, height * 0.24));
        this.FindControl<WrapPanel>("ReaderReplyActions")!.IsVisible = height >= 320;
    }

    private void AdjustPaneToolbars()
    {
        double navigation = this.FindControl<DockPanel>("NavigationPane")!.Bounds.Height;
        double messages = this.FindControl<DockPanel>("MessagePane")!.Bounds.Height;
        this.FindControl<ScrollViewer>("NavigationActionsScroll")!.MaxHeight = Math.Max(80, Math.Min(360, navigation * 0.4));
        this.FindControl<ScrollViewer>("ListToolbarScroll")!.MaxHeight = Math.Max(48, Math.Min(240, messages * 0.4));
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
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => new TextBlock { Text = account!.Name }),
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

    private void RemoveAccount(IAccountManagement management)
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
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => new TextBlock
            {
                Text = account!.Name + " — " + account.Incoming.Host
            }),
            SelectedItem = model.Accounts.FirstOrDefault(account => account.Id == model.Scope?.Account) ?? model.Accounts[0]
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
        window.FontFamily = FontFamily;
        window.FontSize = FontSize;
        bool wrap = window.Content is StackPanel;
        StackPanel? panel = window.Content as StackPanel ?? (window.Content as ScrollViewer)?.Content as StackPanel;
        if (panel is not null)
        {
            if (wrap)
            {
                window.Content = null;
            }
            panel.Margin = new Thickness(24);
            panel.Spacing = 12;
            panel.Children.Insert(0, new TextBlock
            {
                Text = window.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = FontSize * 1.4,
                TextWrapping = TextWrapping.Wrap
            });
            if (wrap)
            {
                window.Content = new ScrollViewer
                {
                    Content = panel
                };
            }
        }
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
        // Avalonia raises control events before its two-way bindings update the source.
        model.Scope = (this.FindControl<TreeView>("ScopeList")!.SelectedItem as NavigationItemViewModel)?.Scope;
        model.UnreadOnly = this.FindControl<CheckBox>("UnreadFilter")!.IsChecked == true;
        model.KnownAttachmentsOnly = this.FindControl<CheckBox>("AttachmentFilter")!.IsChecked == true;
        return model.RefreshAsync(false);
    }

    private void Wire(string name, Func<Task> action)
    {
        this.FindControl<Button>(name)!.Click += (_, _) => Track(action);
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
            ItemTemplate = new FuncDataTemplate<MailboxSnapshot>((item, _) => new TextBlock
            {
                Text = item!.Name
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

    private async Task DesktopSettingsAsync(IDesktopIntegration desktop)
    {
        DesktopPreferences current = await desktop.ReadPreferencesAsync(_lifetime.Token);
        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8
        };
        CheckBox tray = new CheckBox
        {
            Content = Strings.Get("TrayOptIn"),
            IsChecked = current.TrayEnabled
        };
        CheckBox notifications = new CheckBox
        {
            Content = Strings.Get("PrivateNotifications"),
            IsChecked = current.NotificationsEnabled
        };
        panel.Children.Add(tray);
        panel.Children.Add(notifications);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("DesktopIntegrationHint"),
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(panel, "Save", async () =>
        {
            status.Text = Strings.Describe(await desktop.ConfigureAsync(new DesktopPreferences(
                tray.IsChecked == true,
                notifications.IsChecked == true
            ), _lifetime.Token));
        }, status);
        FormControls.AddActionButton(panel, "TestGenericNotification", () =>
        {
            status.Text = Strings.Describe(desktop.NotifyNewMail());
            return Task.CompletedTask;
        }, status);
        panel.Children.Add(status);
        ShowWindow(new Window
        {
            Title = Strings.DesktopSettings,
            Width = 620,
            Height = 360,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = new ScrollViewer
            {
                Content = panel
            }
        });
    }

    private async Task CommandsAsync()
    {
        if (_application is null || DataContext is not MainWindowViewModel model ||
            (model.Scope?.Account ?? model.Selected?.Account) is not AccountId account)
        {
            return;
        }

        ListBox list = new ListBox
        {
            ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token),
            MaxHeight = 280,
            ItemTemplate = new FuncDataTemplate<CommandSnapshot>((command, _) => new TextBlock
            {
                Text = command!.Subject + "\n" + Strings.Describe(command.Entry.Intent.Kind) + " — " +
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
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(panel, "RetryCommand", async () =>
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
        FormControls.AddActionButton(panel, "DismissCommand", async () =>
        {
            if (list.SelectedItem is CommandSnapshot command)
            {
                status.Text = Strings.Describe(await _application.ResolveCommandAsync(command.Entry, false, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token);
            }
        }, status);
        FormControls.AddActionButton(panel, "Refresh", async () =>
        {
            list.ItemsSource = await _application.Catalog.CommandsAsync(account, _lifetime.Token);
        }, status);
        panel.Children.Add(status);
        ShowWindow(new Window
        {
            Title = Strings.Commands,
            Width = 760,
            Height = 520,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = new ScrollViewer
            {
                Content = panel
            }
        });
    }

    private async Task DraftsAsync()
    {
        if (_application is null || DataContext is not MainWindowViewModel model || model.Scope?.Account is not AccountId account)
        {
            return;
        }

        ImmutableArray<DraftSnapshot> drafts = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
        ListBox list = new ListBox
        {
            ItemsSource = drafts,
            ItemTemplate = new FuncDataTemplate<DraftSnapshot>((draft, _) => new TextBlock
            {
                Text = Strings.Get("DraftRevision") + " " + draft!.Revision + " — " +
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
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(panel, "OpenDraft", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
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
        FormControls.AddActionButton(panel, "PushDraft", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
                status.Text = Strings.Describe(await _application.PushDraftAsync(draft.Account, draft.Id, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.DraftsAsync(account, _lifetime.Token);
            }
        }, status);
        FormControls.AddActionButton(panel, "ReviewVersions", async () =>
        {
            if (list.SelectedItem is DraftSnapshot draft)
            {
                await ReviewVersionsAsync(draft);
            }
        }, status);
        panel.Children.Add(status);
        Window window = new Window
        {
            Title = Strings.Drafts,
            Width = 760,
            Height = 480,
            Content = panel
        };
        ShowWindow(window);
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

    private async Task OutboxAsync()
    {
        if (_application is null || DataContext is not MainWindowViewModel model || model.Scope?.Account is not AccountId account)
        {
            return;
        }

        StackPanel panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12)
        };
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        ListBox list = new ListBox
        {
            ItemsSource = await _application.Catalog.OutboxAsync(account, _lifetime.Token),
            ItemTemplate = new FuncDataTemplate<OutboxEntry>((entry, _) => new TextBlock
            {
                Text = Strings.Get(entry!.State.ToString()) + "\n" + string.Join("; ", entry.Submission.Recipients) +
                                            "\n" + (entry.Evidence is null ? "" : string.Join("\n", entry.Evidence.Recipients.Select(item =>
                                                item.Address + " — " + Strings.Describe(item.State) + " " + item.ReplyCode))),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8)
            })
        };
        FormControls.AddActionButton(panel, "Transmit", async () =>
        {
            status.Text = Strings.Describe(await _application.ProcessOutboxAsync(account, _lifetime.Token));
            list.ItemsSource = await _application.Catalog.OutboxAsync(account, _lifetime.Token);
        }, status);
        CheckBox consent = new CheckBox
        {
            Content = Strings.Get("ResendConsent"),
            IsChecked = false
        };
        panel.Children.Add(consent);
        FormControls.AddActionButton(panel, "CancelQueued", async () =>
        {
            if (list.SelectedItem is OutboxEntry entry)
            {
                status.Text = Strings.Describe(await _application.CancelQueuedAsync(entry, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.OutboxAsync(account, _lifetime.Token);
            }
        }, status);
        FormControls.AddActionButton(panel, "Resend", async () =>
        {
            if (consent.IsChecked != true || list.SelectedItem is not OutboxEntry entry)
            {
                status.Text = Strings.Describe(Failure.NeedsReview);
                return;
            }

            Outcome<ComposeContent> content = await _application.PrepareResendAsync(entry, _lifetime.Token);
            if (content.IsSuccess)
            {
                OpenCompose(
                    activation: content.Value,
                    predecessor: entry.Submission.Id,
                    targetAccount: entry.Submission.Account
                );
                consent.IsChecked = false;
            }
            else
            {
                status.Text = Strings.Describe(content.Failure);
            }
        }, status);
        FormControls.AddActionButton(panel, "EditCancelled", async () =>
        {
            if (list.SelectedItem is not OutboxEntry entry || entry.State != SubmissionState.Cancelled)
            {
                status.Text = Strings.Describe(Failure.NeedsReview);
                return;
            }

            Outcome<ComposeContent> content = await _application.PrepareResendAsync(entry, _lifetime.Token);
            if (content.IsSuccess)
            {
                OpenCompose(activation: content.Value, targetAccount: entry.Submission.Account);
            }
            else
            {
                status.Text = Strings.Describe(content.Failure);
            }
        }, status);
        FormControls.AddActionButton(panel, "SentCopy", async () =>
        {
            if (list.SelectedItem is OutboxEntry entry)
            {
                status.Text = Strings.Describe(await _application.StoreSentCopyAsync(entry, _lifetime.Token));
                list.ItemsSource = await _application.Catalog.OutboxAsync(account, _lifetime.Token);
            }
        }, status);
        CheckBox copyConsent = new CheckBox
        {
            Content = Strings.Get("SentCopyConsent"),
            IsChecked = false
        };
        list.SelectionChanged += (_, _) =>
        {
            consent.IsChecked = false;
            copyConsent.IsChecked = false;
        };
        panel.Children.Add(copyConsent);
        FormControls.AddActionButton(panel, "ReviewedSentCopy", async () =>
        {
            if (copyConsent.IsChecked != true || list.SelectedItem is not OutboxEntry entry)
            {
                status.Text = Strings.Describe(Failure.NeedsReview);
                return;
            }

            status.Text = Strings.Describe(await _application.RetrySentCopyAsync(entry, _lifetime.Token));
            copyConsent.IsChecked = false;
            list.ItemsSource = await _application.Catalog.OutboxAsync(account, _lifetime.Token);
        }, status);
        panel.Children.Add(status);
        panel.Children.Add(list);
        ShowWindow(new Window
        {
            Title = Strings.Outbox,
            Width = 900,
            Height = 480,
            Content = new ScrollViewer
            {
                Content = panel
            }
        });
    }

    private void Appearance()
    {
        StackPanel panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16)
        };
        string[] themes = ["System", "Light", "Dark"];
        string[] previews = ["Right", "Bottom", "Hidden"];
        string[] densities = ["Comfortable", "Compact"];
        ComboBox theme = Choice("Theme", themes, _settings.Theme);
        ComboBox preview = Choice("PreviewLayout", previews, _settings.Preview);
        ComboBox density = Choice("Density", densities, _settings.Density);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("FontSize")
        });
        Slider size = new Slider
        {
            Minimum = 12,
            Maximum = 28,
            Value = _settings.FontSize
        };
        Avalonia.Automation.AutomationProperties.SetName(size, Strings.Get("FontSize"));
        panel.Children.Add(size);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("Font")
        });
        ComboBox fonts = new ComboBox
        {
            ItemsSource = FontManager.Current.SystemFonts,
            SelectedItem = FontFamily
        };
        Avalonia.Automation.AutomationProperties.SetName(fonts, Strings.Get("Font"));
        panel.Children.Add(fonts);
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        FormControls.AddActionButton(panel, "SaveAppearance", async () =>
        {
            AppearanceSettings settings = new AppearanceSettings(
                themes[theme.SelectedIndex],
                previews[preview.SelectedIndex],
                densities[density.SelectedIndex],
                fonts.SelectedItem is FontFamily family ? family.Name : "",
                size.Value
            );
            ApplyAppearance(settings);
            status.Text = _appearance is null ? Strings.Saved :
                Strings.Describe(await _appearance.SaveAsync(settings, _lifetime.Token));
        }, status);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("ReducedMotion"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(status);
        ShowWindow(new Window
        {
            Title = Strings.Settings,
            Width = 500,
            Height = 620,
            Content = new ScrollViewer
            {
                Content = panel
            }
        });

        ComboBox Choice(string label, string[] values, string selected)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Get(label)
            });
            ComboBox choice = new ComboBox
            {
                ItemsSource = values.Select(Strings.Get).ToArray(),
                SelectedIndex = Array.IndexOf(values, selected)
            };
            Avalonia.Automation.AutomationProperties.SetName(choice, Strings.Get(label));
            panel.Children.Add(choice);
            return choice;
        }
    }
}
