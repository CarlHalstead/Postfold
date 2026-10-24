using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

/// <summary>One settings surface with account-scoped actions and sender identities.</summary>
public sealed class SettingsWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly TabControl _tabs;

    internal SettingsWindow(
        MainWindowViewModel model,
        AppearanceSettings settings,
        IClientAppearance? appearance,
        IClientIdentity? identity,
        IDesktopIntegration? desktop,
        Action<AppearanceSettings> apply,
        Func<AccountId, Task> credentials,
        Action<AccountId> rename,
        Action<AccountId> remove,
        bool canManage,
        bool canRestoreCredentials,
        IClientUpdates? updates = null
    )
    {
        Title = Strings.Settings;
        Width = 760;
        Height = 680;
        MinWidth = 480;
        MinHeight = 480;
        DockPanel root = new DockPanel();
        _tabs = new TabControl
        {
            Name = "SettingsSections",
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel
            {
                [KeyboardNavigation.TabNavigationProperty] = KeyboardNavigationMode.Continue
            })
        };
        _tabs.ItemsSource = new[]
        {
            Section("Appearance", Appearance()),
            Section("Accounts", Accounts()),
            Section("SenderIdentity", Identity()),
            Section("Desktop", Desktop()),
            Section("Updates", Updates())
        };

        root.Children.Add(_tabs);
        Content = root;
        void RefreshAccounts(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs args
        )
        {
            if (args.PropertyName != nameof(MainWindowViewModel.HasAccounts))
            {
                return;
            }
            AccountCatalogChanged?.Invoke();
        }
        model.PropertyChanged += RefreshAccounts;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= RefreshAccounts;
            _lifetime.Cancel();
        };

        Control Appearance()
        {
            StackPanel form = Form();
            StackPanel footer = Footer();
            string[] themes = ["System", "Light", "Dark"];
            string[] previews = ["Right", "Bottom", "Hidden"];
            string[] densities = ["Comfortable", "Compact"];
            ComboBox theme = Choice(form, "Theme", themes.Select(Strings.Get).ToArray(), Array.IndexOf(themes, settings.Theme));
            ComboBox preview = Choice(form, "PreviewLayout", previews.Select(Strings.Get).ToArray(), Array.IndexOf(previews, settings.Preview));
            ComboBox density = Choice(form, "Density", densities.Select(Strings.Get).ToArray(), Array.IndexOf(densities, settings.Density));
            TextBlock sizeLabel = new TextBlock
            {
                Text = Strings.Get("FontSize"),
                TextWrapping = TextWrapping.Wrap
            };
            form.Children.Add(sizeLabel);
            Slider size = new Slider
            {
                Name = "TextSize",
                Minimum = 12,
                Maximum = 28,
                Value = settings.FontSize,
                TickFrequency = 1,
                IsSnapToTickEnabled = true
            };
            AutomationProperties.SetName(size, Strings.Get("FontSize"));
            size.ValueChanged += (_, _) => sizeLabel.Text = Strings.Get("FontSize") + ": " + size.Value.ToString(
                "0",
                System.Globalization.CultureInfo.CurrentCulture
            );
            sizeLabel.Text += ": " + settings.FontSize.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
            form.Children.Add(size);
            string[] fonts = new[] { Strings.Get("FontDefault") }.Concat(FontManager.Current.SystemFonts
                .Select(font => font.Name).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
            ComboBox font = Choice(form, "Font", fonts, Math.Max(0, Array.IndexOf(fonts, settings.Font)));
            TextBlock status = Status();
            FormControls.AddActionButton(footer, "SaveAppearance", async () =>
            {
                AppearanceSettings chosen = new AppearanceSettings(
                    themes[theme.SelectedIndex],
                    previews[preview.SelectedIndex],
                    densities[density.SelectedIndex],
                    font.SelectedIndex == 0 ? "" : (string)font.SelectedItem!,
                    size.Value
                );
                if (!chosen.IsValid)
                {
                    status.Text = Strings.Describe(Failure.Validation);
                    return;
                }
                apply(chosen);
                status.Text = appearance is null ? Strings.Get("AppearanceSessionOnly") :
                    Strings.Describe(await appearance.SaveAsync(chosen, _lifetime.Token));
            }, status).Classes.Add("primary");
            footer.Children.Add(status);
            return FormControls.Page("", form, footer);
        }

        Control Accounts()
        {
            StackPanel form = Form();
            void Refresh()
            {
                form.Children.Clear();
                foreach (AccountConfiguration account in model.Accounts)
                {
                    Grid row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                        ColumnSpacing = 8
                    };
                    TextBlock address = new TextBlock
                    {
                        Text = account.Address,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    ToolTip.SetTip(address, account.Name + "\n" + account.Address);
                    row.Children.Add(address);
                    TextBlock status = Status();
                    string connection = account.ProviderKind == "Imap" ? "Credentials" : "ProviderConnection";
                    Button restore = FormControls.AddActionButton(row, connection, () => credentials(account.Id), status);
                    Button renameButton = FormControls.AddActionButton(row, "RenameAccount", () =>
                    {
                        rename(account.Id);
                        return Task.CompletedTask;
                    }, status);
                    Button removeButton = FormControls.AddActionButton(row, "RemoveAccount", () =>
                    {
                        remove(account.Id);
                        return Task.CompletedTask;
                    }, status);
                    IconAction(restore, MailIcons.Refresh, connection, account.Address, 1, canRestoreCredentials);
                    IconAction(renameButton, MailIcons.Rename, "RenameAccount", account.Address, 2, canManage);
                    IconAction(removeButton, MailIcons.Close, "RemoveAccount", account.Address, 3, canManage);
                    Border surface = new Border
                    {
                        Child = row
                    };
                    surface.Classes.Add("card");
                    surface.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MailNavigationBrush"));
                    form.Children.Add(surface);
                    form.Children.Add(status);
                }
            }
            AccountCatalogChanged += Refresh;
            Refresh();
            return FormControls.Page("", form);
        }

        Control Identity()
        {
            StackPanel form = Form();
            Dictionary<AccountId, (StackPanel Form, TextBlock Label)> rows = new Dictionary<AccountId, (StackPanel, TextBlock)>();
            void Refresh()
            {
                foreach (AccountId removed in rows.Keys.Where(id => model.Accounts.All(account => account.Id != id)).ToArray())
                {
                    rows.Remove(removed);
                }
                form.Children.Clear();
                foreach (AccountConfiguration account in model.Accounts)
                {
                    if (!rows.TryGetValue(account.Id, out (StackPanel Form, TextBlock Label) row))
                    {
                        StackPanel fields = new StackPanel { Spacing = 12 };
                        TextBlock label = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            FontWeight = FontWeight.SemiBold
                        };
                        fields.Children.Add(label);
                        TextBox sender = FormControls.AddField(fields, "Sender", account.Identity?.Sender ?? "");
                        sender.MaxLength = 320;
                        AutomationProperties.SetName(sender, Strings.Get("Sender") + ": " + account.Address);
                        fields.Children.Add(new TextBlock
                        {
                            Text = Strings.Get("IdentityHint"),
                            TextWrapping = TextWrapping.Wrap
                        });
                        TextBlock status = Status();
                        FormControls.AddActionButton(fields, "SaveSettings", async () =>
                        {
                            if (identity is null || model.Accounts.All(current => current.Id != account.Id))
                            {
                                return;
                            }
                            Failure result = await identity.SaveAsync(account.Id, new ComposeIdentity(sender.Text ?? ""), _lifetime.Token);
                            if (result == Failure.None)
                            {
                                await model.InitialiseAsync();
                            }
                            status.Text = Strings.Describe(result);
                        }, status);
                        fields.Children.Add(status);
                        fields.IsEnabled = identity is not null;
                        row = (fields, label);
                        rows.Add(account.Id, row);
                    }
                    row.Label.Text = account.Address;
                    form.Children.Add(row.Form);
                }
            }
            AccountCatalogChanged += Refresh;
            Refresh();
            return FormControls.Page("", form);
        }

        Control Updates()
        {
            UpdateSettingsView view = new UpdateSettingsView(desktop, updates, _lifetime.Token);
            Opened += async (_, _) =>
            {
                Task load = view.LoadAsync();
                FormControls.Track(this, load);
                await load;
            };
            Closed += (_, _) => view.Stop();
            return view;
        }

        Control Desktop()
        {
            StackPanel form = Form();
            StackPanel footer = Footer();
            CheckBox tray = new CheckBox
            {
                Content = Strings.Get("TrayOptIn")
            };
            CheckBox notifications = new CheckBox
            {
                Content = Strings.Get("PrivateNotifications")
            };
            form.Children.Add(tray);
            form.Children.Add(notifications);
            form.Children.Add(new TextBlock
            {
                Text = Strings.Get("MailPollInterval"),
                TextWrapping = TextWrapping.Wrap
            });
            NumericUpDown pollInterval = new NumericUpDown
            {
                Name = "MailPollInterval",
                Minimum = 3,
                Maximum = 60,
                Increment = 1,
                FormatString = "0",
                Value = 30
            };
            AutomationProperties.SetName(pollInterval, Strings.Get("MailPollInterval"));
            form.Children.Add(pollInterval);
            form.Children.Add(new TextBlock
            {
                Text = Strings.Get("MailPollIntervalHint"),
                TextWrapping = TextWrapping.Wrap
            });
            form.Children.Add(new TextBlock
            {
                Text = Strings.Get("DesktopIntegrationHint"),
                TextWrapping = TextWrapping.Wrap
            });
            TextBlock status = Status();
            FormControls.AddActionButton(footer, "SaveSettings", async () =>
            {
                if (desktop is not null)
                {
                    if (pollInterval.Value is not decimal seconds || seconds != decimal.Truncate(seconds))
                    {
                        status.Text = Strings.Describe(Failure.Validation);
                        return;
                    }
                    status.Text = Strings.Describe(await desktop.ConfigureAsync(new DesktopPreferences(
                        tray.IsChecked == true,
                        notifications.IsChecked == true
                    )
                    {
                        MailPollIntervalSeconds = (int)seconds
                    }, _lifetime.Token));
                }
            }, status);
            FormControls.AddActionButton(form, "TestGenericNotification", () =>
            {
                if (desktop is not null)
                {
                    status.Text = Strings.Describe(desktop.NotifyNewMail());
                }
                return Task.CompletedTask;
            }, status);
            footer.Children.Add(status);
            form.IsEnabled = false;
            footer.IsEnabled = false;
            if (desktop is not null)
            {
                Opened += async (_, _) =>
                {
                    Task load = LoadAsync();
                    FormControls.Track(this, load);
                    await load;
                };
            }
            return FormControls.Page("", form, footer);

            async Task LoadAsync()
            {
                try
                {
                    DesktopPreferences current = await desktop!.ReadPreferencesAsync(_lifetime.Token);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    tray.IsChecked = current.TrayEnabled;
                    notifications.IsChecked = current.NotificationsEnabled;
                    pollInterval.Value = current.MailPollIntervalSeconds;
                    form.IsEnabled = true;
                    footer.IsEnabled = true;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                    status.Text = Strings.Describe(Failure.Unavailable);
                }
            }
        }
    }
    private event Action? AccountCatalogChanged;

    public void SelectSection(string section, AccountId? account = null)
    {
        _tabs.SelectedIndex = section switch
        {
            "Accounts" => 1,
            "SenderIdentity" => 2,
            "Desktop" => 3,
            "Updates" => 4,
            _ => 0
        };
    }

    internal static void IconAction(
        Button button,
        Geometry icon,
        string key,
        string address,
        int column,
        bool enabled
    )
    {
        button.Content = FormControls.Icon(icon);
        button.Classes.Add("quiet");
        button.Margin = default;
        button.MinWidth = 40;
        button.MinHeight = 40;
        button.IsEnabled = enabled;
        string label = Strings.Get(key) + ": " + address;
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        Grid.SetColumn(button, column);
    }

    private static TabItem Section(string key, Control content)
    {
        return new TabItem { Header = Strings.Get(key), Content = content };
    }

    private static ComboBox Choice(StackPanel form, string key, string[] values, int selected)
    {
        form.Children.Add(new TextBlock { Text = Strings.Get(key), TextWrapping = TextWrapping.Wrap });
        ComboBox choice = new ComboBox
        {
            Name = key,
            ItemsSource = values,
            SelectedIndex = selected,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(choice, Strings.Get(key));
        form.Children.Add(choice);
        return choice;
    }

    private static StackPanel Form()
    {
        return new StackPanel { Margin = new Thickness(24, 16), Spacing = 12 };
    }

    private static StackPanel Footer()
    {
        return new StackPanel { Spacing = 8 };
    }

    private static TextBlock Status()
    {
        TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }
}
