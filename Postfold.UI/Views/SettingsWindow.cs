using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

/// <summary>One settings surface; account actions always carry the explicitly selected account.</summary>
public sealed class SettingsWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly ComboBox _accounts;
    private readonly TabControl _tabs;
    private bool _refreshingAccounts;
    private event Action? AccountSelectionChanged;

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
        bool canRestoreCredentials
    )
    {
        Title = Strings.Settings;
        Width = 760;
        Height = 680;
        MinWidth = 480;
        MinHeight = 480;
        DockPanel root = new DockPanel();
        TextBlock heading = new TextBlock
        {
            Text = Strings.Settings,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(24, 24, 24, 16)
        };
        heading.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("SubjectFontSize"));
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        _accounts = new ComboBox
        {
            Name = "SettingsAccountSelection",
            ItemsSource = model.Accounts.ToArray(),
            PlaceholderText = Strings.Get("ChooseAccount"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(24, 0, 24, 16),
            ItemTemplate = new FuncDataTemplate<AccountConfiguration>((account, _) => new TextBlock
            {
                Text = account?.Name ?? Strings.Get("ChooseAccount"),
                TextTrimming = TextTrimming.CharacterEllipsis
            })
        };
        AutomationProperties.SetName(_accounts, Strings.Get("ChooseAccount"));
        DockPanel.SetDock(_accounts, Dock.Top);
        root.Children.Add(_accounts);
        _tabs = new TabControl
        {
            Name = "SettingsSections",
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel())
        };
        _tabs.ItemsSource = new[]
        {
            Section("Appearance", Appearance()),
            Section("Accounts", Accounts()),
            Section("SenderAndSignature", Identity()),
            Section("Desktop", Desktop())
        };
        _tabs.SelectionChanged += (_, _) => _accounts.IsVisible = _tabs.SelectedIndex is 1 or 2;
        _accounts.IsVisible = false;
        root.Children.Add(_tabs);
        Content = root;
        _accounts.SelectionChanged += (_, _) =>
        {
            if (!_refreshingAccounts)
            {
                AccountSelectionChanged?.Invoke();
            }
        };
        void RefreshAccounts(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(MainWindowViewModel.HasAccounts))
            {
                return;
            }
            AccountId? selected = (_accounts.SelectedItem as AccountConfiguration)?.Id;
            _refreshingAccounts = true;
            try
            {
                _accounts.ItemsSource = model.Accounts.ToArray();
                _accounts.SelectedItem = model.Accounts.FirstOrDefault(item => item.Id == selected);
            }
            finally
            {
                _refreshingAccounts = false;
            }
            AccountSelectionChanged?.Invoke();
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
            TextBlock sizeLabel = new TextBlock { Text = Strings.Get("FontSize"), TextWrapping = TextWrapping.Wrap };
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
            size.ValueChanged += (_, _) => sizeLabel.Text = Strings.Get("FontSize") + ": " + size.Value.ToString("0",
                System.Globalization.CultureInfo.CurrentCulture);
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
                status.Text = appearance is null ? Strings.Saved :
                    Strings.Describe(await appearance.SaveAsync(chosen, _lifetime.Token));
            }, status).Classes.Add("primary");
            footer.Children.Add(status);
            return FormControls.Page("", form, footer);
        }

        Control Accounts()
        {
            StackPanel form = Form();
            TextBlock hint = new TextBlock { Text = Strings.Get("ChooseAccount"), TextWrapping = TextWrapping.Wrap };
            form.Children.Add(hint);
            TextBlock status = Status();
            Button restore = FormControls.AddActionButton(form, "Credentials", async () =>
            {
                if (_accounts.SelectedItem is AccountConfiguration account)
                {
                    await credentials(account.Id);
                }
            }, status);
            Button renameButton = FormControls.AddActionButton(form, "RenameAccount", () =>
            {
                if (_accounts.SelectedItem is AccountConfiguration account)
                {
                    rename(account.Id);
                }
                return Task.CompletedTask;
            }, status);
            Button removeButton = FormControls.AddActionButton(form, "RemoveAccount", () =>
            {
                if (_accounts.SelectedItem is AccountConfiguration account)
                {
                    remove(account.Id);
                }
                return Task.CompletedTask;
            }, status);
            void Select()
            {
                bool selected = _accounts.SelectedItem is AccountConfiguration;
                restore.IsEnabled = selected && canRestoreCredentials;
                renameButton.IsEnabled = selected && canManage;
                removeButton.IsEnabled = selected && canManage;
                hint.Text = selected ? ((AccountConfiguration)_accounts.SelectedItem!).Name : Strings.Get("ChooseAccount");
            }
            AccountSelectionChanged += Select;
            Select();
            form.Children.Add(status);
            return FormControls.Page("", form);
        }

        Control Identity()
        {
            StackPanel form = Form();
            StackPanel footer = Footer();
            TextBox sender = FormControls.AddField(form, "Sender");
            sender.MaxLength = 320;
            TextBox signature = FormControls.AddField(form, "Signature");
            signature.MaxLength = 8192;
            signature.AcceptsReturn = true;
            signature.TextWrapping = TextWrapping.Wrap;
            signature.MinHeight = 140;
            form.Children.Add(new TextBlock { Text = Strings.Get("IdentityHint"), TextWrapping = TextWrapping.Wrap });
            TextBlock status = Status();
            AccountId? editing = null;
            Button save = FormControls.AddActionButton(footer, "SaveSettings", async () =>
            {
                if (identity is null || _accounts.SelectedItem is not AccountConfiguration account)
                {
                    return;
                }
                Failure result = await identity.SaveAsync(account.Id, new ComposeIdentity(sender.Text ?? "", signature.Text ?? ""), _lifetime.Token);
                if (result == Failure.None)
                {
                    editing = null;
                    await model.InitialiseAsync();
                }
                if ((_accounts.SelectedItem as AccountConfiguration)?.Id == account.Id)
                {
                    status.Text = Strings.Describe(result);
                }
            }, status);
            void Select()
            {
                AccountConfiguration? account = _accounts.SelectedItem as AccountConfiguration;
                if (editing != account?.Id)
                {
                    sender.Text = account?.Identity?.Sender ?? "";
                    signature.Text = account?.Identity?.Signature ?? "";
                    editing = account?.Id;
                    status.Text = "";
                }
                form.IsEnabled = account is not null && identity is not null;
                save.IsEnabled = form.IsEnabled;
            }
            AccountSelectionChanged += Select;
            Select();
            footer.Children.Add(status);
            return FormControls.Page("", form, footer);
        }

        Control Desktop()
        {
            StackPanel form = Form();
            StackPanel footer = Footer();
            CheckBox tray = new CheckBox { Content = Strings.Get("TrayOptIn") };
            CheckBox notifications = new CheckBox { Content = Strings.Get("PrivateNotifications") };
            form.Children.Add(tray);
            form.Children.Add(notifications);
            form.Children.Add(new TextBlock { Text = Strings.Get("DesktopIntegrationHint"), TextWrapping = TextWrapping.Wrap });
            TextBlock status = Status();
            FormControls.AddActionButton(footer, "SaveSettings", async () =>
            {
                if (desktop is not null)
                {
                    status.Text = Strings.Describe(await desktop.ConfigureAsync(new DesktopPreferences(
                        tray.IsChecked == true,
                        notifications.IsChecked == true
                    ), _lifetime.Token));
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

    public void SelectSection(string section, AccountId? account = null)
    {
        _accounts.SelectedItem = _accounts.Items.OfType<AccountConfiguration>().FirstOrDefault(item => item.Id == account);
        _tabs.SelectedIndex = section switch
        {
            "Accounts" => 1,
            "SenderAndSignature" => 2,
            "Desktop" => 3,
            _ => 0
        };
        _accounts.IsVisible = _tabs.SelectedIndex is 1 or 2;
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
