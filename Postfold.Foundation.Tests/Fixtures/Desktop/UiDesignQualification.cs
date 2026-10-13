using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

/// <summary>Captures actual native Avalonia layouts from an isolated synthetic profile; owner observations stay separate.</summary>
internal static class UiDesignQualification
{
    internal static async Task RunAsync(
        MainWindow main,
        MainWindowViewModel model,
        IAccountSetup setup,
        string output,
        CancellationToken cancellationToken
    )
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        List<object> captures = [];
        AppearanceSettings current = AppearanceSettings.Default;
        model.Selected = model.Messages.First();
        await model.ReadSelectedAsync(false);
        foreach ((string name, string theme, double width, double height, double font, string preview) in new[]
        {
            ("light", "Light", 1400d, 900d, 16d, "Right"),
            ("prototype-size", "Light", 1472d, 909d, 16d, "Right"),
            ("dark", "Dark", 1400d, 900d, 16d, "Right"),
            ("narrow", "Light", 800d, 760d, 16d, "Bottom"),
            ("minimum", "Light", 760d, 480d, 16d, "Bottom"),
            ("minimum-large-text", "Light", 760d, 480d, 24d, "Bottom"),
            ("large-text", "Light", 1400d, 1000d, 24d, "Right"),
            ("bottom", "Light", 1400d, 1000d, 16d, "Bottom"),
            ("hidden", "Light", 1100d, 760d, 16d, "Hidden")
        })
        {
            main.Width = width;
            main.Height = height;
            Apply(AppearanceSettings.Default with
            {
                Theme = theme,
                FontSize = font,
                Preview = preview
            });
            await CaptureAsync(main, name);
        }
        main.Width = 1400;
        main.Height = 900;
        Apply(AppearanceSettings.Default with
        {
            Theme = "Light",
            Density = "Compact"
        });
        await CaptureAsync(main, "compact");
        Apply(AppearanceSettings.Default with
        {
            Theme = "Dark"
        });
        TreeView tree = main.FindControl<TreeView>("ScopeList")!;
        TreeViewItem accountNode = (TreeViewItem)tree.ContainerFromIndex(0)!;
        tree.SelectedItem = accountNode.DataContext;
        await main.WaitForActionsAsync();
        await CaptureAsync(main, "account-selected");
        accountNode.IsExpanded = false;
        await Task.Delay(80, cancellationToken);
        await CaptureAsync(main, "account-collapsing");
        await Task.Delay(160, cancellationToken);
        await CaptureAsync(main, "account-collapsed");
        accountNode.IsExpanded = true;
        await Task.Delay(80, cancellationToken);
        await CaptureAsync(main, "account-expanding");
        await Task.Delay(160, cancellationToken);
        await CaptureAsync(main, "account-expanded");
        foreach (Button header in main.GetVisualDescendants().OfType<Button>()
            .Where(control => control.Classes.Contains("navigation")))
        {
            ((Avalonia.Controls.IPseudoClasses)header.Classes).Set(":pointerover", true);
        }
        await CaptureAsync(main, "sidebar-programmatic-hover");
        foreach (Button header in main.GetVisualDescendants().OfType<Button>()
            .Where(control => control.Classes.Contains("navigation")))
        {
            ((Avalonia.Controls.IPseudoClasses)header.Classes).Set(":pointerover", false);
        }
        model.Selected = model.Messages.Single(message => message.Subject == "Lunch next week?");
        await model.ReadSelectedAsync(false);
        await CaptureAsync(main, "reader-no-attachments");
        model.Selected = model.Messages.First();
        await model.ReadSelectedAsync(false);
        Application.Current!.RequestedThemeVariant = MailTheme.HighContrast;
        await CaptureAsync(main, "high-contrast-palette");
        Apply(AppearanceSettings.Default with
        {
            Theme = "Light"
        });
        main.OpenAccountSetup();
        AccountSetupWindow account = main.OwnedWindows.OfType<AccountSetupWindow>().Single();
        await CaptureAsync(account, "setup-login");
        account.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "Email").Text = "owner@synthetic.invalid";
        account.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ContinueServers")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await CaptureAsync(account, "setup-servers");
        account.Close();
        main.OpenCompose();
        ComposeWindow compose = main.OwnedWindows.OfType<ComposeWindow>().Single();
        await CaptureAsync(compose, "compose");
        compose.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "Recipients").Text = "recipient@synthetic.invalid";
        compose.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "Subject").Text = "Synthetic immediate send";
        await compose.QueueAsync();
        await CaptureAsync(compose, "compose-send-offline");
        Apply(AppearanceSettings.Default with
        {
            Theme = "Dark"
        });
        await CaptureAsync(compose, "compose-send-offline-dark");
        Apply(AppearanceSettings.Default with
        {
            Theme = "Light",
            FontSize = 24
        });
        compose.Width = 480;
        compose.Height = 480;
        await CaptureAsync(compose, "compose-send-offline-minimum-large-text");
        Apply(AppearanceSettings.Default with
        {
            Theme = "Light"
        });
        compose.ConfirmShutdown();
        compose.Close();
        foreach ((string section, string name) in new[]
        {
            ("Appearance", "settings"), ("Accounts", "settings-accounts"),
            ("SenderAndSignature", "settings-identity"), ("Desktop", "settings-desktop")
        })
        {
            Console.WriteLine("Native settings checkpoint: " + section);
            main.OpenSettings(section, model.Accounts[0].Id);
            await main.WaitForActionsAsync();
            Console.WriteLine("Native settings actions completed: " + section);
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            await CaptureAsync(settings, name);
            Apply(AppearanceSettings.Default with
            {
                Theme = "Dark"
            });
            await CaptureAsync(settings, name + "-dark");
            Apply(AppearanceSettings.Default with
            {
                Theme = "Light",
                FontSize = 24
            });
            settings.Width = 480;
            settings.Height = 480;
            await CaptureAsync(settings, name + "-minimum-large-text");
            Apply(AppearanceSettings.Default with
            {
                Theme = "Light"
            });
            settings.Close();
        }
        main.OpenSettings("Accounts", model.Accounts[0].Id);
        SettingsWindow accountSettings = main.OwnedWindows.OfType<SettingsWindow>().Single();
        foreach ((string button, string name) in new[]
        {
            ("Credentials", "credentials"), ("RemoveAccount", "remove-account"), ("RenameAccount", "rename-account")
        })
        {
            accountSettings.GetVisualDescendants().OfType<Button>().Single(control => control.Name == button)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            await FrameAsync(main, cancellationToken);
            Window child = main.OwnedWindows.Last(window => window is not SettingsWindow);
            await CaptureAsync(child, name);
            if (name == "remove-account")
            {
                child.Width = 360;
                child.Height = 300;
                child.FontSize = 24;
                await CaptureAsync(child, "remove-account-minimum-large-text");
                Application.Current!.RequestedThemeVariant = MailTheme.HighContrast;
                await CaptureAsync(child, "remove-account-contrast");
                Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            }
            child.Close();
        }
        accountSettings.Close();
        foreach (string name in new[] { "drafts", "recovery" })
        {
            if (name == "drafts")
            {
                await main.DraftsAsync(model.Accounts[0].Id);
            }
            else
            {
                await main.CommandsAsync(model.Accounts[0].Id);
            }
            Window child = main.OwnedWindows.Last();
            await CaptureAsync(child, name);
            if (name == "recovery")
            {
                child.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "SendIssues").SelectedIndex = 0;
                await CaptureAsync(child, "send-recovery-selected");
                Apply(AppearanceSettings.Default with
                {
                    Theme = "Light",
                    FontSize = 24
                });
                child.Width = 480;
                child.Height = 480;
                await CaptureAsync(child, "send-recovery-minimum-large-text");
                Apply(AppearanceSettings.Default with
                {
                    Theme = "Light"
                });
            }
            child.Close();
        }
        Apply(AppearanceSettings.Default with
        {
            Theme = "Light"
        });
        model.Scope = model.Scopes.Single(scope => scope.Account is null && scope.InboxOnly);
        await model.RefreshAsync(false);
        await CaptureAsync(main, "unified");
        model.Scope = model.Scopes.First(scope => scope.Account == model.Accounts[0].Id && scope.Folder is not null &&
            model.Mailboxes.Any(mailbox => mailbox.Id == scope.Folder && mailbox.Role == MailboxRole.Sent));
        await model.RefreshAsync(false);
        await CaptureAsync(main, "empty-folder");
        model.Scope = model.Scopes.First(scope => scope.Account == model.Accounts[0].Id && scope.Folder is not null &&
            model.Mailboxes.Any(mailbox => mailbox.Id == scope.Folder && mailbox.Role == MailboxRole.Inbox));
        await model.RefreshAsync(false);
        model.Selected = model.Messages.First();
        await model.ReadSelectedAsync(false);
        await model.FlagCommand.ExecuteAsync(null);
        await CaptureAsync(main, "flagged-pending");
        main.FindControl<Button>("DetailsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await main.WaitForActionsAsync();
        Window details = main.OwnedWindows.Last();
        await CaptureAsync(details, "reader-details");
        details.Close();
        model.Selected = model.Messages.First(message => message.Subject == "Development update");
        await model.ReadSelectedAsync(false);
        await CaptureAsync(main, "long-reader-many-attachments");
        model.Selected = model.Messages.First(message => message.Subject.StartsWith("Synthetic résumé", StringComparison.Ordinal));
        await model.ReadSelectedAsync(false);
        await CaptureAsync(main, "reader-missing-content");
        main.FindControl<TextBox>("SearchBox")!.Focus(Avalonia.Input.NavigationMethod.Tab);
        await CaptureAsync(main, "search-programmatic-focus");
        model.Terms = "no-results-authored-fixture";
        await model.RefreshAsync(false);
        await CaptureAsync(main, "search-no-results");
        model.Terms = "";
        await model.RefreshAsync(false);
        Apply(AppearanceSettings.Default with
        {
            Theme = "System"
        });
        await CaptureAsync(main, "system");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            NativePlatform = true,
            ScreenshotMethod = "RenderTargetBitmap of actual native Avalonia window content; excludes OS title bar and native HTML child",
            RealAccounts = false,
            Captures = captures,
            OwnerVisualAcceptance = false,
            NarratorVerified = false,
            OsHighContrastVerified = false,
            AcceptanceComplete = false
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        }) + "\n", cancellationToken);

        void Apply(AppearanceSettings settings)
        {
            current = settings;
            main.ApplyAppearance(settings);
        }

        async Task CaptureAsync(Window window, string name)
        {
            Console.WriteLine("Native content capture: " + name);
            if (window == main)
            {
                main.FindControl<ScrollViewer>("PlainPreviewScroll")!.Offset = default;
            }
            await FrameAsync(window, cancellationToken);
            await FrameAsync(window, cancellationToken);
            string image = Path.Combine(directory, "pf049-" + name + ".png");
            using RenderTargetBitmap bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)),
                new Vector(96, 96)
            );
            bitmap.Render(window);
            bitmap.Save(image, PngBitmapEncoderOptions.Default);
            ListBox? list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(control => control.Name == "MessageList");
            bool? forwardFocus = null;
            bool? reverseFocus = null;
            if (window == main)
            {
                main.FindControl<TreeView>("ScopeList")!.Focus();
                main.CyclePaneFocus(false);
                forwardFocus = main.FindControl<ListBox>("MessageList")!.IsKeyboardFocusWithin;
                main.CyclePaneFocus(true);
                reverseFocus = main.FindControl<TreeView>("ScopeList")!.IsKeyboardFocusWithin;
            }
            captures.Add(new
            {
                Name = name,
                Image = Path.GetFileName(image),
                Width = window.Bounds.Width,
                Height = window.Bounds.Height,
                Scaling = window.RenderScaling,
                FontSize = window.FontSize,
                Font = window.FontFamily.Name,
                Density = current.Density,
                RequestedPreview = current.Preview,
                ActualPreview = main.FindControl<Grid>("Panes")!.RowDefinitions.Count == 3 ? "Bottom" :
                    main.FindControl<DockPanel>("PreviewPane")!.IsVisible ? "Right" : "Hidden",
                Theme = window.ActualThemeVariant.ToString(),
                Virtualised = list?.ItemsPanelRoot is VirtualizingStackPanel,
                RealisedMessages = list?.GetRealizedContainers().Count(),
                ReaderBodyHeight = window == main ? main.FindControl<ScrollViewer>("PlainPreviewScroll")!.Bounds.Height : (double?)null,
                TotalMessages = model.CachedMessageCount,
                AccountDisclosureProgress = window == main ? main.GetVisualDescendants()
                    .OfType<Postfold.UI.Controls.AnimatedDisclosure>().FirstOrDefault()?.Progress : (double?)null,
                AccountDisclosureHeight = window == main ? main.GetVisualDescendants()
                    .OfType<Postfold.UI.Controls.AnimatedDisclosure>().FirstOrDefault()?.Bounds.Height : (double?)null,
                AttachmentsVisible = window == main ? main.FindControl<StackPanel>("ReaderAttachmentPanel")!.IsVisible : (bool?)null,
                ListToolbarHeight = window == main ? main.FindControl<Grid>("ListToolbar")!.Bounds.Height : (double?)null,
                ReaderToolbarHeight = window == main ? main.FindControl<Grid>("ReaderToolbar")!.Bounds.Height : (double?)null,
                NativeProgrammaticForwardPaneFocus = forwardFocus,
                NativeProgrammaticReversePaneFocus = reverseFocus
            });
        }
    }

    private static async Task FrameAsync(Window window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
    }
}
