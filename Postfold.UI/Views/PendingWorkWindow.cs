using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Operations;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

internal sealed class PendingWorkWindow : Window
{
    private PendingWorkWindow(
        IMailApplication application,
        AccountId account,
        ImmutableArray<CommandSnapshot> commands,
        SendIssuesView sends,
        CancellationToken cancellationToken
    )
    {
        ListBox list = new ListBox
        {
            ItemsSource = commands,
            Name = "PendingCommands",
            MaxHeight = 280,
            ItemTemplate = new FuncDataTemplate<CommandSnapshot>((command, _) => command is null ? null : new TextBlock
            {
                Text = command.Subject + "\n" + Strings.Describe(command.Entry.Intent.Kind) + " — " +
                    Strings.Describe(command.Entry.State) + "\n" + Strings.Describe(command.Entry.Evidence),
                TextWrapping = TextWrapping.Wrap
            })
        };
        AutomationProperties.SetName(list, Strings.Commands);
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
        StackPanel footer = new StackPanel
        {
            Spacing = 8
        };
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
                status.Text = Strings.Describe(await application.ResolveCommandAsync(command.Entry, true, cancellationToken));
                consent.IsChecked = false;
                list.ItemsSource = await application.Catalog.CommandsAsync(account, cancellationToken);
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
                status.Text = Strings.Describe(await application.ResolveCommandAsync(command.Entry, false, cancellationToken));
                list.ItemsSource = await application.Catalog.CommandsAsync(account, cancellationToken);
            }
        }, status);
        FormControls.AddActionButton(actions, "Refresh", async () =>
        {
            list.ItemsSource = await application.Catalog.CommandsAsync(account, cancellationToken);
        }, status);
        footer.Children.Add(status);
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
            SelectedIndex = sends.HasIssues ? 0 : 1
        };
        DockPanel page = new DockPanel();
        TextBlock heading = new TextBlock
        {
            Text = Strings.Commands,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(24, 24, 24, 16)
        };
        heading.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("SubjectFontSize"));
        DockPanel.SetDock(heading, Dock.Top);
        page.Children.Add(heading);
        page.Children.Add(tabs);
        Title = Strings.Commands;
        Width = 760;
        Height = 520;
        MinWidth = 480;
        MinHeight = 480;
        Content = page;
    }

    internal static async Task<PendingWorkWindow> CreateAsync(
        IMailApplication application,
        AccountId account,
        Action<ComposeContent, Guid, AccountId> reviewMessage,
        CancellationToken cancellationToken
    )
    {
        ImmutableArray<CommandSnapshot> commands = await application.Catalog.CommandsAsync(account, cancellationToken);
        SendIssuesView sends = new SendIssuesView(application, account, reviewMessage, cancellationToken);
        await sends.RefreshAsync();
        return new PendingWorkWindow(application, account, commands, sends, cancellationToken);
    }
}
