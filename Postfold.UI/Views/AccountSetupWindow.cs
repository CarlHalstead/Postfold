using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;

namespace Postfold.UI.Views;

public sealed class AccountSetupWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    public AccountSetupWindow(IAccountSetup setup)
    {
        Title = Strings.Setup;
        Width = 620;
        Height = 780;
        MinWidth = 360;
        MinHeight = 480;
        StackPanel form = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        form.Children.Add(new TextBlock
        {
            Text = Strings.Setup,
            FontWeight = FontWeight.SemiBold,
            FontSize = 22
        });
        StackPanel login = new StackPanel
        {
            Name = "LoginStep",
            Spacing = 12
        };
        form.Children.Add(login);
        TextBox email = FormControls.AddField(login, "Email");
        email.MaxLength = 320;
        login.Children.Add(new TextBlock
        {
            Text = Strings.LoginHint,
            TextWrapping = TextWrapping.Wrap
        });
        StackPanel advanced = new StackPanel { Spacing = 8 };
        TextBox username = FormControls.AddField(advanced, "IncomingUsername");
        login.Children.Add(new Expander
        {
            Name = "ReceivingLoginOptions",
            Header = Strings.Get("SharedLoginOptions"),
            Content = advanced
        });
        TextBox password = FormControls.AddField(login, "AccountPassword", password: true);
        password.Name = "IncomingPassword";
        StackPanel servers = new StackPanel
        {
            Name = "ServerStep",
            Spacing = 12,
            IsVisible = false
        };
        form.Children.Add(servers);
        servers.Children.Add(new TextBlock
        {
            Text = Strings.ServerHint,
            TextWrapping = TextWrapping.Wrap
        });
        TextBox incomingHost = FormControls.AddField(servers, "IncomingHost");
        TextBox incomingPort = FormControls.AddField(servers, "IncomingPort", "993");
        ComboBox incomingTls = TlsChoice("IncomingTls");
        servers.Children.Add(incomingTls);
        TextBox outgoingHost = FormControls.AddField(servers, "OutgoingHost");
        TextBox outgoingPort = FormControls.AddField(servers, "OutgoingPort", "465");
        ComboBox outgoingTls = TlsChoice("OutgoingTls");
        servers.Children.Add(outgoingTls);
        TextBox draft = FormControls.AddField(servers, "DraftMailbox");
        form.Children.Add(new TextBlock
        {
            Text = Strings.Privacy,
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock status = new TextBlock
        {
            Name = "SetupStatus",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        FormControls.AddActionButton(login, "ContinueServers", () =>
        {
            if (!MailAddress.TryCreate(email.Text, out MailAddress? address) || address.Address != email.Text)
            {
                status.Text = Strings.Email + ": " + Strings.Describe(Failure.Validation);
                email.Focus();
                return System.Threading.Tasks.Task.CompletedTask;
            }
            login.IsVisible = false;
            servers.IsVisible = true;
            incomingHost.Focus();
            status.Text = "";
            return System.Threading.Tasks.Task.CompletedTask;
        }, status).Classes.Add("primary");
        FormControls.AddActionButton(servers, "BackLogin", () =>
        {
            servers.IsVisible = false;
            login.IsVisible = true;
            email.Focus();
            return System.Threading.Tasks.Task.CompletedTask;
        }, status);
        StackPanel roles = new StackPanel
        {
            Name = "FolderRoles",
            Spacing = 8
        };
        FormControls.AddActionButton(servers, "TestEndpoints", async () =>
        {
            roles.Children.Clear();
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
                password.Focus();
                return;
            }
            string receiving = string.IsNullOrWhiteSpace(username.Text) ?
                email.Text ?? "" : username.Text;
            status.Text = Strings.Get("SetupConnecting");
            try
            {
                result = await setup.AddAsync(new AccountSetupRequest(
                    email.Text ?? "",
                    new MailEndpoint(incomingHost.Text ?? "", first, (TlsMode)incomingTls.SelectedItem!, receiving, ""),
                    new MailEndpoint(outgoingHost.Text ?? "", second, (TlsMode)outgoingTls.SelectedItem!, receiving, ""),
                    password.Text ?? "",
                    password.Text ?? "",
                    false,
                    string.IsNullOrWhiteSpace(draft.Text) ? null : draft.Text,
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
                $"{Strings.Get("InitialSync")}: {Strings.Describe(result.Synchronisation)}\n" +
                string.Join("\n", result.Mailboxes.Select(item => item.Name));
            if (result.Incoming == Failure.None && !result.Mailboxes.IsDefaultOrEmpty)
            {
                AccountAdded?.Invoke(result.Mailboxes[0].Account);
            }
            if (result.Incoming == Failure.None && !result.Mailboxes.IsDefaultOrEmpty)
            {
                ImmutableArray<MailboxSnapshot> available = result.Mailboxes.Where(item => item.Selectable && item.Writable &&
                    item.Role != MailboxRole.Inbox)
                    .ToImmutableArray();
                string[] labels = new[] { Strings.Get("RoleUnavailable") }.Concat(available.Select(item => item.Name)).ToArray();
                ComboBox sent = Choice("SentRole", MailboxRole.Sent);
                ComboBox drafts = Choice("DraftsRole", MailboxRole.Drafts);
                ComboBox archive = Choice("Archive", MailboxRole.Archive);
                ComboBox trash = Choice("Trash", MailboxRole.Trash);
                FormControls.AddActionButton(roles, "SaveRoles", async () =>
                {
                    status.Text = Strings.Describe(await setup.SetRolesAsync(
                        result.Mailboxes[0].Account,
                        new FolderRoles(Path(sent), Path(drafts), Path(archive), Path(trash)),
                        _lifetime.Token
                    ));
                }, status);

                ComboBox Choice(string key, MailboxRole role)
                {
                    roles.Children.Add(new TextBlock
                    {
                        Text = Strings.Get(key)
                    });
                    ComboBox choice = new ComboBox
                    {
                        Name = key,
                        ItemsSource = labels,
                        SelectedIndex = 0
                    };
                    Avalonia.Automation.AutomationProperties.SetName(choice, Strings.Get(key));
                    MailboxSnapshot[] defaults = available.Where(item => item.Role == role).ToArray();
                    if (defaults.Length == 1)
                    {
                        choice.SelectedIndex = available.IndexOf(defaults[0]) + 1;
                    }

                    roles.Children.Add(choice);
                    return choice;
                }

                string? Path(ComboBox choice)
                {
                    return choice.SelectedIndex > 0 ? available[choice.SelectedIndex - 1].RemoteKey : null;
                }
            }
        }, status).Classes.Add("primary");
        form.Children.Add(roles);
        form.Children.Add(status);
        FormControls.AddActionButton(form, "SkipSetup", () =>
        {
            Close();
            return System.Threading.Tasks.Task.CompletedTask;
        }, status);
        Content = new ScrollViewer
        {
            Content = form
        };
        Opened += (_, _) => email.Focus();
        Closed += (_, _) => { password.Text = ""; CancelSetup(); };

        ComboBox TlsChoice(string key)
        {
            servers.Children.Add(new TextBlock
            {
                Text = Strings.Get(key)
            });
            ComboBox choice = new ComboBox
            {
                Name = key,
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
