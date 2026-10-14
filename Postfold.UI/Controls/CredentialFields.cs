using Avalonia.Automation;
using Avalonia.Controls;
using Postfold.UI.Resources;

namespace Postfold.UI.Controls;

/// <summary>Shared secret entry with explicit sending overrides and no retained hidden secret.</summary>
internal sealed class CredentialFields
{
    private readonly StackPanel _sending;

    internal CredentialFields(StackPanel form)
    {
        ReceivingPassword = FormControls.AddField(form, "IncomingPassword", password: true);
        Shared = new CheckBox
        {
            Name = "SharedCredentials",
            Content = Strings.SharedPassword,
            IsChecked = true
        };
        AutomationProperties.SetName(Shared, Shared.Content!.ToString());
        form.Children.Add(Shared);
        _sending = new StackPanel
        {
            Name = "SendingCredentials",
            Spacing = 8,
            IsVisible = false
        };
        _sending.Children.Add(new TextBlock
        {
            Text = Strings.SendingOverrideHint,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        SendingPassword = FormControls.AddField(_sending, "OutgoingPassword", password: true);
        form.Children.Add(_sending);
        Shared.IsCheckedChanged += (_, _) =>
        {
            bool shared = Shared.IsChecked == true;
            if (shared)
            {
                if (_sending.IsKeyboardFocusWithin)
                {
                    Shared.Focus();
                }
                SendingPassword.Text = "";
            }
            _sending.IsVisible = !shared;
        };
    }

    internal TextBox ReceivingPassword { get; }
    internal TextBox SendingPassword { get; }
    internal CheckBox Shared { get; }
    internal string OutgoingPassword => Shared.IsChecked == true ? ReceivingPassword.Text ?? "" : SendingPassword.Text ?? "";

    internal void Clear()
    {
        ReceivingPassword.Text = "";
        SendingPassword.Text = "";
    }

    internal bool Validate(TextBlock status)
    {
        TextBox? invalid = string.IsNullOrEmpty(ReceivingPassword.Text) ? ReceivingPassword :
            Shared.IsChecked != true && string.IsNullOrEmpty(SendingPassword.Text) ? SendingPassword : null;
        if (invalid is null)
        {
            return true;
        }
        status.Text = AutomationProperties.GetName(invalid) + ": " + Strings.Describe(Postfold.Core.Enums.Failure.Validation);
        invalid.Focus();
        return false;
    }
}
