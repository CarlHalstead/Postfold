using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

public sealed class AccountSetupWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    public AccountSetupWindow(
        IAccountSetup setup
    )
    {
        Title = Strings.Setup;
        Width = 620;
        Height = 600;
        MinWidth = 360;
        MinHeight = 360;
        StackPanel form = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        IProviderAccounts? providers = setup as IProviderAccounts;
        string providerKind = "Imap";
        form.Children.Add(new TextBlock
        {
            Text = Strings.Get("ProviderLoginHint"),
            TextWrapping = TextWrapping.Wrap
        });
        Grid providerActions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,12,*")
        };
        form.Children.Add(providerActions);
        StackPanel login = new StackPanel
        {
            Name = "LoginStep",
            Spacing = 12
        };
        form.Children.Add(login);
        TextBox email = FormControls.AddField(login, "Email");
        email.MaxLength = 320;
        TextBox password = FormControls.AddField(login, "AccountPassword", password: true);
        password.Name = "IncomingPassword";
        TextBlock loginHint = new TextBlock
        {
            Text = Strings.LoginHint,
            TextWrapping = TextWrapping.Wrap
        };
        login.Children.Add(loginHint);
        StackPanel servers = new StackPanel
        {
            Name = "ServerStep",
            Spacing = 12,
            IsVisible = false
        };
        form.Children.Add(servers);
        servers.Children.Add(new TextBlock
        {
            Text = Strings.ReceivingSettings,
            FontWeight = FontWeight.SemiBold
        });
        servers.Children.Add(new TextBlock
        {
            Text = Strings.ServerHint,
            TextWrapping = TextWrapping.Wrap
        });
        StackPanel incomingHostGroup = new StackPanel();
        TextBox incomingHost = FormControls.AddField(incomingHostGroup, "IncomingHost");
        StackPanel incomingPortGroup = new StackPanel
        {
            Spacing = 4
        };
        StackPanel incomingTlsGroup = new StackPanel
        {
            Spacing = 4
        };
        TextBox incomingPort = FormControls.AddField(incomingPortGroup, "IncomingPort", "993");
        ComboBox incomingTls = TlsChoice("IncomingTls", incomingTlsGroup);
        incomingTlsGroup.Children.Add(incomingTls);
        servers.Children.Add(HostAndPort(incomingHostGroup, incomingPortGroup));
        servers.Children.Add(incomingTlsGroup);
        servers.Children.Add(new TextBlock
        {
            Text = Strings.SendingSettings,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        StackPanel outgoingHostGroup = new StackPanel();
        TextBox outgoingHost = FormControls.AddField(outgoingHostGroup, "OutgoingHost");
        StackPanel outgoingPortGroup = new StackPanel
        {
            Spacing = 4
        };
        StackPanel outgoingTlsGroup = new StackPanel
        {
            Spacing = 4
        };
        TextBox outgoingPort = FormControls.AddField(outgoingPortGroup, "OutgoingPort", "465");
        ComboBox outgoingTls = TlsChoice("OutgoingTls", outgoingTlsGroup);
        outgoingTlsGroup.Children.Add(outgoingTls);
        servers.Children.Add(HostAndPort(outgoingHostGroup, outgoingPortGroup));
        servers.Children.Add(outgoingTlsGroup);
        TextBlock status = new TextBlock
        {
            Name = "SetupStatus",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        WrapPanel loginActions = new WrapPanel();
        WrapPanel serverActions = new WrapPanel
        {
            IsVisible = false
        };
        Grid actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        StackPanel footer = new StackPanel
        {
            Spacing = 8
        };
        actions.Children.Add(loginActions);
        actions.Children.Add(serverActions);
        footer.Children.Add(actions);
        footer.Children.Add(status);
        Button back = new Button
        {
            Name = "BackLogin",
            Content = FormControls.Icon(MailIcons.Back),
            IsVisible = false
        };
        back.Classes.Add("quiet");
        AutomationProperties.SetName(back, Strings.Get("Back"));
        ToolTip.SetTip(back, Strings.Get("Back"));
        string? configuredEmail = null;
        Button next = FormControls.AddActionButton(loginActions, "ContinueServers", async () =>
        {
            if (!MailAddress.TryCreate(email.Text, out MailAddress? address) || address.Address != email.Text)
            {
                status.Text = Strings.Email + ": " + Strings.Describe(Failure.Validation);
                email.Focus();
                return;
            }
            string currentEmail = address.Address;
            if (configuredEmail != currentEmail && providerKind == "Imap")
            {
                status.Text = Strings.Get("SetupDiscovering");
                AccountServerSettings? discovered = await setup.DiscoverAsync(currentEmail, _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (email.Text != currentEmail || providerKind != "Imap")
                {
                    status.Text = "";
                    return;
                }
                if (configuredEmail is not null)
                {
                    incomingHost.Text = "";
                    outgoingHost.Text = "";
                    incomingPort.Text = "993";
                    outgoingPort.Text = "465";
                    incomingTls.SelectedItem = TlsMode.Implicit;
                    outgoingTls.SelectedItem = TlsMode.Implicit;
                }
                if (discovered?.Incoming is MailEndpoint incoming)
                {
                    incomingHost.Text = incoming.Host;
                    incomingPort.Text = incoming.Port.ToString(CultureInfo.InvariantCulture);
                    incomingTls.SelectedItem = incoming.Tls;
                }
                if (discovered?.Outgoing is MailEndpoint outgoing)
                {
                    outgoingHost.Text = outgoing.Host;
                    outgoingPort.Text = outgoing.Port.ToString(CultureInfo.InvariantCulture);
                    outgoingTls.SelectedItem = outgoing.Tls;
                }
                configuredEmail = currentEmail;
            }
            login.IsVisible = false;
            servers.IsVisible = true;
            loginActions.IsVisible = false;
            serverActions.IsVisible = true;
            back.IsVisible = true;
            incomingHost.Focus();
            status.Text = "";
        }, status);
        next.Classes.Add("primary");
        back.Click += (_, _) =>
        {
            servers.IsVisible = false;
            login.IsVisible = true;
            loginActions.IsVisible = true;
            serverActions.IsVisible = false;
            back.IsVisible = false;
            email.Focus();
        };
        FormControls.AddActionButton(serverActions, "TestEndpoints", async () =>
        {
            if (!int.TryParse(incomingPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int first) ||
                !int.TryParse(outgoingPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int second))
            {
                status.Text = Strings.Describe(Failure.Validation);
                return;
            }

            AccountSetupResult result;
            if (string.IsNullOrEmpty(password.Text))
            {
                status.Text = Strings.Describe(Failure.Validation);
                servers.IsVisible = false;
                login.IsVisible = true;
                loginActions.IsVisible = true;
                serverActions.IsVisible = false;
                back.IsVisible = false;
                password.Focus();
                return;
            }
            string loginEmail = email.Text ?? "";
            status.Text = Strings.Get("SetupConnecting");
            try
            {
                result = await setup.AddAsync(new AccountSetupRequest(
                    email.Text ?? "",
                    new MailEndpoint(incomingHost.Text ?? "", first, (TlsMode)incomingTls.SelectedItem!, loginEmail, ""),
                    new MailEndpoint(outgoingHost.Text ?? "", second, (TlsMode)outgoingTls.SelectedItem!, loginEmail, ""),
                    password.Text ?? "",
                    password.Text ?? "",
                    null,
                    email.Text
                ), _lifetime.Token);
            }
            finally
            {
                password.Text = "";
            }

            if (result.Duplicate)
            {
                status.Text = Strings.Get("DuplicateAccount");
                return;
            }
            status.Text = $"{Strings.Get("CredentialResult")}: {Strings.Describe(result.Credentials)}; " +
                $"IMAP: {Strings.Describe(result.Incoming)}; SMTP: {Strings.Describe(result.Outgoing)}; " +
                $"{Strings.Sender}: {Strings.Describe(result.Identity)}; " +
                $"{Strings.Get("InitialSync")}: {Strings.Describe(result.Synchronisation)}";
            status.BringIntoView();
            if (result.Incoming == Failure.None && !result.Mailboxes.IsDefaultOrEmpty)
            {
                AccountAdded?.Invoke(result.Mailboxes[0].Account);
            }
            if (result.Credentials == Failure.None && result.Incoming == Failure.None && result.Outgoing == Failure.None &&
                result.Identity == Failure.None && result.Synchronisation == Failure.None && !result.Mailboxes.IsDefaultOrEmpty)
            {
                Close();
            }
        }, status).Classes.Add("primary");
        foreach (string kind in new[] { "Google", "Microsoft" })
        {
            Button signIn = FormControls.AddActionButton(providerActions, "SignIn" + kind, async () =>
            {
                SelectProvider(kind);
                try
                {
                    await ConnectProviderAsync();
                }
                finally
                {
                    providerKind = "Imap";
                    login.IsVisible = true;
                    loginActions.IsVisible = true;
                    loginHint.Text = Strings.LoginHint;
                    email.Focus();
                }
            }, status);
            Grid caption = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("24,12,*")
            };
            caption.Children.Add(ProviderIcons.Create(kind));
            TextBlock label = new TextBlock
            {
                Text = Strings.Get("Provider" + kind),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(label, 2);
            caption.Children.Add(label);
            signIn.Content = caption;
            signIn.Margin = default;
            signIn.MinHeight = 48;
            signIn.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            signIn.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            Grid.SetColumn(signIn, kind == "Google" ? 0 : 2);
        }
        Button skip = FormControls.AddActionButton(actions, "SkipSetup", () =>
        {
            Close();
            return System.Threading.Tasks.Task.CompletedTask;
        }, status);
        skip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        Grid.SetColumn(skip, 1);
        Content = FormControls.Page(Strings.Setup, form, footer, back);
        Opened += (_, _) => email.Focus();
        Closed += (_, _) => { password.Text = ""; CancelSetup(); };

        void SelectProvider(string kind)
        {
            providerKind = kind;
            password.Text = "";
            email.Text = "";
            login.IsVisible = kind == "Imap";
            loginHint.Text = Strings.Get(kind == "Imap" ? "LoginHint" : "ProviderLoginHint");
            servers.IsVisible = false;
            loginActions.IsVisible = kind == "Imap";
            serverActions.IsVisible = false;
            back.IsVisible = false;
            status.Text = "";
            if (kind == "Imap")
            {
                email.Focus();
            }
        }

        async Task ConnectProviderAsync()
        {
            if (providers is null)
            {
                status.Text = Strings.Get("ProviderConfigurationMissing");
                return;
            }
            string kind = providerKind;
            Failure available = providers.Providers.FirstOrDefault(item => item.ProviderKind == kind)?.Failure ?? Failure.Unavailable;
            if (available != Failure.None)
            {
                status.Text = Strings.Get(available == Failure.PolicyBlocked ? "ProviderPolicyMissing" : "ProviderConfigurationMissing");
                return;
            }
            providerActions.IsEnabled = false;
            login.IsEnabled = false;
            servers.IsEnabled = false;
            next.IsEnabled = false;
            back.IsEnabled = false;
            status.Text = Strings.Get("ProviderConnecting");
            try
            {
                ProviderConnectionResult result = await providers.ConnectProviderAsync(
                    new ProviderSetupRequest(kind, Synchronise: false),
                    _lifetime.Token
                );
                status.Text = result.Cancelled ? Strings.Get("ProviderCancelled") : result.Duplicate ? Strings.Get("DuplicateAccount") :
                    result.Failure != Failure.None ? Strings.Describe(result.Failure) :
                    Strings.Get("InitialSync") + ": " + Strings.Describe(result.Synchronisation);
                if (result.Account is AccountId account)
                {
                    AccountAdded?.Invoke(account);
                    Close();
                }
            }
            finally
            {
                providerActions.IsEnabled = true;
                login.IsEnabled = true;
                servers.IsEnabled = true;
                next.IsEnabled = true;
                back.IsEnabled = true;
                password.Text = "";
                email.Focus();
            }
        }

        Grid HostAndPort(
            StackPanel host,
            StackPanel port
        )
        {
            Grid row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("9*,12,1*")
            };
            row.ColumnDefinitions[2].MinWidth = 80;
            host.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            port.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            row.Children.Add(host);
            row.Children.Add(port);
            Grid.SetColumn(port, 2);
            return row;
        }

        ComboBox TlsChoice(
            string key,
            StackPanel target
        )
        {
            TextBlock label = new TextBlock
            {
                Text = Strings.Get(key),
                TextWrapping = TextWrapping.Wrap
            };
            label.Classes.Add("caption");
            target.Children.Add(label);
            ComboBox choice = new ComboBox
            {
                Name = key,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                ItemsSource = Enum.GetValues<TlsMode>(),
                SelectedIndex = 0,
                ItemTemplate = new FuncDataTemplate<TlsMode>((value, _) => new TextBlock
                {
                    Text = Strings.Describe(value)
                })
            };
            Avalonia.Automation.AutomationProperties.SetName(choice, Strings.Get(key));
            return choice;
        }
    }

    /// <summary>Notifies the owner when an account has been saved, including a reported initial-sync failure.</summary>
    public event Action<AccountId>? AccountAdded;

    public void CancelSetup()
    {
        _lifetime.Cancel();
        foreach (TextBox password in this.GetVisualDescendants().OfType<TextBox>().Where(field => field.PasswordChar != '\0'))
        {
            password.Text = "";
        }
    }
}
