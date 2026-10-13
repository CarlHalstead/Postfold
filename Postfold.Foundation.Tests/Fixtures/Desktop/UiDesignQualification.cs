using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        model.Selected = model.Messages.First();
        await model.ReadSelectedAsync(false);
        foreach ((string name, string theme, double width, double height, double font, string preview) in new[]
        {
            ("light", "Light", 1400d, 900d, 14d, "Right"),
            ("dark", "Dark", 1400d, 900d, 14d, "Right"),
            ("narrow", "Light", 800d, 760d, 14d, "Bottom"),
            ("minimum", "Light", 760d, 480d, 14d, "Bottom"),
            ("minimum-large-text", "Light", 760d, 480d, 24d, "Bottom"),
            ("large-text", "Light", 1400d, 1000d, 24d, "Right"),
            ("bottom", "Light", 1400d, 1000d, 14d, "Bottom"),
            ("hidden", "Light", 1100d, 760d, 14d, "Hidden")
        })
        {
            main.Width = width;
            main.Height = height;
            main.ApplyAppearance(AppearanceSettings.Default with
            {
                Theme = theme,
                FontSize = font,
                Preview = preview
            });
            await CaptureAsync(main, name);
        }
        main.Width = 1400;
        main.Height = 900;
        main.ApplyAppearance(AppearanceSettings.Default with
        {
            Theme = "Dark"
        });
        TreeView tree = main.FindControl<TreeView>("ScopeList")!;
        TreeViewItem accountNode = (TreeViewItem)tree.ContainerFromIndex(0)!;
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
        foreach (Avalonia.Controls.Primitives.ToggleButton header in main.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(control => control.Name == "ExpanderHeader" &&
                control.GetVisualAncestors().OfType<Expander>().Any(parent => parent.Classes.Contains("navigation"))))
        {
            ((Avalonia.Controls.IPseudoClasses)header.Classes).Set(":pointerover", true);
        }
        await CaptureAsync(main, "sidebar-programmatic-hover");
        foreach (Avalonia.Controls.Primitives.ToggleButton header in main.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(control => control.Name == "ExpanderHeader"))
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
        main.ApplyAppearance(AppearanceSettings.Default with
        {
            Theme = "Light"
        });
        AccountSetupWindow account = new AccountSetupWindow(setup);
        account.Show(main);
        await CaptureAsync(account, "setup-login");
        account.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "Email").Text = "owner@synthetic.invalid";
        account.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ContinueServers")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await CaptureAsync(account, "setup-servers");
        account.Close();
        main.OpenCompose();
        ComposeWindow compose = main.OwnedWindows.OfType<ComposeWindow>().Single();
        await CaptureAsync(compose, "compose");
        compose.ConfirmShutdown();
        compose.Close();
        foreach ((string button, string name) in new[]
        {
            ("AppearanceButton", "settings"),
            ("CredentialsButton", "credentials"),
            ("RemoveAccountButton", "remove-account"),
            ("RenameAccountButton", "rename-account"),
            ("DraftsButton", "drafts"),
            ("OutboxButton", "outbox"),
            ("CommandsButton", "recovery")
        })
        {
            main.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            await FrameAsync(main, cancellationToken);
            Window child = main.OwnedWindows.LastOrDefault() ?? throw new InvalidOperationException("Surface did not open: " + name);
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

        async Task CaptureAsync(Window window, string name)
        {
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
                Theme = window.ActualThemeVariant.ToString(),
                Virtualised = list?.ItemsPanelRoot is VirtualizingStackPanel,
                RealisedMessages = list?.GetRealizedContainers().Count(),
                ReaderBodyHeight = window == main ? main.FindControl<ScrollViewer>("PlainPreviewScroll")!.Bounds.Height : (double?)null,
                TotalMessages = model.CachedMessageCount,
                AccountDisclosureProgress = window == main ? main.GetVisualDescendants()
                    .OfType<Postfold.UI.Controls.AnimatedDisclosure>().FirstOrDefault()?.Progress : (double?)null,
                AccountDisclosureHeight = window == main ? main.GetVisualDescendants()
                    .OfType<Postfold.UI.Controls.AnimatedDisclosure>().FirstOrDefault()?.Bounds.Height : (double?)null,
                AttachmentsVisible = window == main ? main.FindControl<ScrollViewer>("ReaderAttachmentScroll")!.IsVisible : (bool?)null,
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
