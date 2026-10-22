using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

/// <summary>Explicit connection actions that retain mailbox binding and protected work.</summary>
public sealed class ProviderAccountWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    public ProviderAccountWindow(
        IProviderAccounts providers,
        AccountConfiguration account,
        Func<Task> refresh
    )
    {
        Title = Strings.Get("ProviderConnection");
        Width = 560;
        Height = 520;
        MinWidth = 360;
        MinHeight = 360;
        StackPanel form = new StackPanel

        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        form.Children.Add(new TextBlock

        {
            Text = account.Address,
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock

        {
            Text = Strings.Get("ProviderConnectionHint"),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock

        {
            Text = Strings.Get(account.ProviderKind == "Google" ? "GoogleRevokeHint" : "MicrosoftRevokeHint"),
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock status = new TextBlock

        {
            Name = "ProviderAccountStatus",
            Text = Strings.Get("ProviderNotChecked"),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        StackPanel actions = new StackPanel

        {
            Spacing = 8
        };
        bool busy = false;
        Button reconnect = FormControls.AddActionButton(actions, "ReconnectProvider", () => RunAsync(async () =>
        {
            ProviderConnectionResult result = await providers.ReconnectProviderAsync(account.Id, _lifetime.Token);
            return result.Cancelled ? Strings.Get("ProviderCancelled") : result.Failure != Failure.None ? Strings.Describe(result.Failure) :
                Strings.Get("InitialSync") + ": " + Strings.Describe(result.Synchronisation);
        }), status);
        reconnect.Classes.Add("primary");
        FormControls.AddActionButton(actions, "DeleteProviderTokens", () => RunAsync(async () =>
        {
            Failure result = await providers.DeleteProviderTokensAsync(account.Id, _lifetime.Token);
            return result == Failure.None ? Strings.Get("ProviderTokensDeleted") : Strings.Describe(result);
        }), status);
        if (account.ProviderKind == "Google")
        {
            FormControls.AddActionButton(actions, "RevokeProvider", () => RunAsync(async () =>
            {
                Failure result = await providers.RevokeProviderAsync(account.Id, _lifetime.Token);
                return result == Failure.None ? Strings.Get("ProviderGrantRevoked") : Strings.Describe(result);
            }), status);
        }
        form.Children.Add(actions);
        form.Children.Add(status);
        Content = FormControls.Page(Title, form);
        Opened += (_, _) => reconnect.Focus();
        Closed += (_, _) => CancelConnection();

        async Task RunAsync(
            Func<Task<string>> action
        )
        {
            if (busy)
            {
                return;
            }
            busy = true;
            actions.IsEnabled = false;
            status.Text = Strings.Get("ProviderWorking");
            try
            {
                status.Text = await action();
                await refresh();
            }
            catch (OperationCanceledException)
            {
                status.Text = Strings.Get("ProviderCancelled");
            }
            finally
            {
                busy = false;
                actions.IsEnabled = true;
                actions.Children[0].Focus();
            }
        }
    }

    /// <summary>Cancels browser and connection work before the owner's shutdown drain.</summary>
    public void CancelConnection()
    {
        _lifetime.Cancel();
    }
}
