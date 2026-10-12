using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using Postfold.Core;

namespace Postfold.UI;

public sealed class AccountSetupWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    public AccountSetupWindow(IAccountSetup setup)
    {
        Title = Strings.Setup;
        Width = 620;
        Height = 820;
        StackPanel form = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        TextBox name = UiForm.Field(form, "Name");
        TextBox incomingHost = UiForm.Field(form, "IncomingHost");
        TextBox incomingPort = UiForm.Field(form, "IncomingPort", "993");
        ComboBox incomingTls = TlsChoice("IncomingTls");
        form.Children.Add(incomingTls);
        TextBox incomingUser = UiForm.Field(form, "IncomingUsername");
        TextBox incomingPassword = UiForm.Field(form, "IncomingPassword", password: true);
        TextBox outgoingHost = UiForm.Field(form, "OutgoingHost");
        TextBox outgoingPort = UiForm.Field(form, "OutgoingPort", "465");
        ComboBox outgoingTls = TlsChoice("OutgoingTls");
        form.Children.Add(outgoingTls);
        TextBox outgoingUser = UiForm.Field(form, "OutgoingUsername");
        TextBox outgoingPassword = UiForm.Field(form, "OutgoingPassword", password: true);
        TextBox draft = UiForm.Field(form, "DraftMailbox");
        CheckBox session = new CheckBox { Name = "SessionOnly", Content = Strings.SessionOnly, IsChecked = false };
        form.Children.Add(session);
        form.Children.Add(new TextBlock { Text = Strings.Privacy, TextWrapping = TextWrapping.Wrap });
        TextBlock status = new TextBlock { Name = "SetupStatus", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        StackPanel roles = new StackPanel { Name = "FolderRoles", Spacing = 8 };
        UiForm.Action(form, "TestEndpoints", async () =>
        {
            roles.Children.Clear();
            if (!int.TryParse(incomingPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int first) ||
                !int.TryParse(outgoingPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int second))
            {
                status.Text = Strings.Describe(Failure.Validation);
                return;
            }

            AccountSetupResult result;
            try
            {
                result = await setup.AddAsync(new AccountSetupRequest(name.Text ?? "",
                new MailEndpoint(incomingHost.Text ?? "", first, (TlsMode)incomingTls.SelectedItem!, incomingUser.Text ?? "", ""),
                new MailEndpoint(outgoingHost.Text ?? "", second, (TlsMode)outgoingTls.SelectedItem!, outgoingUser.Text ?? "", ""),
                incomingPassword.Text ?? "", outgoingPassword.Text ?? "", session.IsChecked == true,
                    string.IsNullOrWhiteSpace(draft.Text) ? null : draft.Text), _lifetime.Token);
            }
            finally
            {
                incomingPassword.Text = "";
                outgoingPassword.Text = "";
            }

            status.Text = $"{Strings.Get("CredentialResult")}: {Strings.Describe(result.Credentials)}; " +
                $"IMAP: {Strings.Describe(result.Incoming)}; SMTP: {Strings.Describe(result.Outgoing)}\n" +
                string.Join("\n", result.Mailboxes.Select(item => item.Name));
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
                UiForm.Action(roles, "SaveRoles", async () =>
                {
                    status.Text = Strings.Describe(await setup.SetRolesAsync(result.Mailboxes[0].Account,
                        new FolderRoles(Path(sent), Path(drafts), Path(archive), Path(trash)), _lifetime.Token));
                }, status);

                ComboBox Choice(string key, MailboxRole role)
                {
                    roles.Children.Add(new TextBlock { Text = Strings.Get(key) });
                    ComboBox choice = new ComboBox { Name = key, ItemsSource = labels, SelectedIndex = 0 };
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
        }, status);
        form.Children.Add(roles);
        form.Children.Add(status);
        Content = new ScrollViewer { Content = form };
        Closed += (_, _) => CancelSetup();

        ComboBox TlsChoice(string key)
        {
            form.Children.Add(new TextBlock { Text = Strings.Get(key) });
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

    public void CancelSetup()
    {
        _lifetime.Cancel();
        foreach (TextBox password in this.GetVisualDescendants().OfType<TextBox>().Where(field => field.PasswordChar != '\0'))
        {
            password.Text = "";
        }
    }
}
