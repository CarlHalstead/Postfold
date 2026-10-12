using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Services;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services;
using Postfold.UI.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Postfold.Desktop.Diagnostics;

/// <summary>Interactive qualification controls for a separate synthetic profile. All observations are explicit.</summary>
internal static class ClientQualification
{
    internal static void Open(
        MainWindow main,
        DesktopMailRuntime host,
        QualificationMailApplication application,
        QualificationAccountSetup setup,
        DesktopIntegration integration,
        string output
    )
    {
        if (host.NetworkAllowed)
        {
            throw new InvalidOperationException("Qualification cannot access a real or network-enabled profile.");
        }

        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10
        };
        Window controls = new Window
        {
            Title = "Postfold synthetic qualification controls",
            Width = 640,
            Height = 780,
            Content = new ScrollViewer
            {
                Content = panel
            }
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Separate synthetic profile; networking and mail submission are disabled. " +
                "Setup responses below are authored fixtures, not real server tests. " +
                "The first messages contain authored attachments and HTML. Image consent uses in-process fixtures. " +
                "HTML in ordinary profiles remains disabled.",
            TextWrapping = TextWrapping.Wrap
        });
        Failure[] diagnostics = [Failure.None, Failure.Validation, Failure.TlsRejected, Failure.NeedsAuthentication,
            Failure.CredentialStorageUnavailable, Failure.PolicyBlocked, Failure.Unsupported];
        ComboBox incoming = Choice("Incoming fixture result");
        ComboBox outgoing = Choice("Outgoing fixture result");
        ComboBox credentials = Choice("Credential fixture result");
        TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        AddButton("Open setup fixture", () =>
        {
            setup.Incoming = (Failure)incoming.SelectedItem!;
            setup.Outgoing = (Failure)outgoing.SelectedItem!;
            setup.Credentials = (Failure)credentials.SelectedItem!;
            AccountSetupWindow window = new AccountSetupWindow(setup);
            window.Show(main);
            foreach (TextBox field in window.GetVisualDescendants().OfType<TextBox>())
            {
                field.Text = field.Name switch
                {
                    "Name" => "Authored synthetic account",
                    "IncomingHost" => "incoming.synthetic.invalid",
                    "OutgoingHost" => "outgoing.synthetic.invalid",
                    "IncomingUsername" => "incoming-synthetic",
                    "OutgoingUsername" => "outgoing-synthetic",
                    "IncomingPassword" or "OutgoingPassword" => "authored-synthetic-secret",
                    _ => field.Text
                };
            }
        });
        AddButton("Request generic Windows notification", () => status.Text =
            "Native request result: " + integration.NotifyNewMail() + ". Confirm visible content separately below.");
        AddButton("Activate authored valid mailto", () =>
        {
            Outcome<ComposeContent> parsed = MailtoActivation.Parse(
                "mailto:recipient@synthetic.invalid?subject=Authored%20activation&body=Synthetic%20body"
            );
            if (parsed.IsSuccess)
            {
                main.OpenCompose(activation: parsed.Value, applyIdentitySignature: true);
            }
            status.Text = "Activation result: " + parsed.Failure;
        });
        AddButton("Reject authored malformed mailto", () => status.Text = "Malformed activation result: " +
            MailtoActivation.Parse("mailto:recipient@synthetic.invalid?body=%00").Failure);
        CheckBox failing = new CheckBox
        {
            Content = "Inject draft-save failure (synthetic profile only)"
        };
        failing.IsCheckedChanged += (_, _) => application.FailDraftSaves = failing.IsChecked == true;
        panel.Children.Add(failing);
        panel.Children.Add(new TextBlock
        {
            Text = "With failure enabled, edit two compose windows, then close the main window: both must stay open. " +
                "Disable the failure, save, close and reopen this mode to inspect persisted drafts. " +
                "No delivery is possible in this mode.",
            TextWrapping = TextWrapping.Wrap
        });
        List<CheckBox> observations = [];
        foreach (string check in new[]
        {
            "Setup fixture diagnostics remain distinct; passwords clear; unavailable roles are explicit",
            "Received attachment picker cancellation writes nothing; selected save has a safe name and authored content",
            "Narrator names, roles, states and focus work in setup, reading, drafts, outbox and settings",
            "Existing display scaling, contrast and reduced motion work without clipping or lost focus",
            "Native notification appeared with generic text and its transient icon cleared",
            "Valid mailto opens an editable draft; malformed activation is rejected without sending",
            "Injected failed saves preserve all compose windows; recovery and restart preserve their text"
            , "Authored HTML supports Narrator and F6/Shift+F6 focus; selected CID renders; remote image needs explicit consent"
        })
        {
            CheckBox observation = new CheckBox
            {
                Content = check
            };
            observations.Add(observation);
            panel.Children.Add(observation);
        }
        Button save = new Button
        {
            Content = "Save explicit observations"
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                object report = new
                {
                    CapturedUtc = DateTimeOffset.UtcNow,
                    Runtime = RuntimeInformation.FrameworkDescription,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    DesktopAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
                    UiAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                    Scaling = main.RenderScaling,
                    Theme = main.ActualThemeVariant.ToString(),
                    FontSize = main.FontSize,
                    NetworkingEnabled = false,
                    RealProfilesUsed = false,
                    Observations = observations.Select(item => new { Check = item.Content, OwnerReportedPass = item.IsChecked == true }).ToArray(),
                    AcceptanceComplete = false
                };
                await File.WriteAllTextAsync(
                    output,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }) + "\n"
                );
                status.Text = "Observations saved. Unchecked items remain unverified.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                status.Text = "Evidence could not be saved: " + exception.GetType().Name;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        panel.Children.Add(save);
        panel.Children.Add(status);
        controls.Show(main);

        ComboBox Choice(string label)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label
            });
            ComboBox choice = new ComboBox
            {
                ItemsSource = diagnostics,
                SelectedIndex = 0
            };
            Avalonia.Automation.AutomationProperties.SetName(choice, label);
            panel.Children.Add(choice);
            return choice;
        }

        void AddButton(string label, Action action)
        {
            Button button = new Button
            {
                Content = label
            };
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }
    }
}
