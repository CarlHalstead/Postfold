using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Updates;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UI.Views;

/// <summary>Local settings and explicit operations; construction/loading never performs update discovery.</summary>
internal sealed class UpdateSettingsView : UserControl
{
    private readonly IDesktopIntegration? _desktop;
    private readonly IClientUpdates? _updates;
    private readonly IClientUpdateActivity? _activity;
    private readonly CancellationToken _lifetime;
    private readonly StackPanel _preferences = new StackPanel
    {
        Spacing = 12
    };
    private readonly CheckBox _checks = Choice("UpdateChecksEnabled");
    private readonly CheckBox _downloads = Choice("UpdateDownloadsEnabled");
    private readonly CheckBox _unmetered = Choice("UpdateUnmeteredOnly");
    private readonly ComboBox _schedule = new ComboBox
    {
        Name = "UpdateSchedule",
        ItemsSource = new[] { Strings.Get("UpdateDaily"), Strings.Get("UpdateWeekly") },
        SelectedIndex = 0
    };
    private readonly TextBlock _availability = new TextBlock
    {
        Name = "UpdatesAvailability",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _version = new TextBlock
    {
        Name = "UpdateVersion",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _progressText = new TextBlock
    {
        Name = "UpdateProgressText",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _saved = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ProgressBar _progress = new ProgressBar
    {
        Name = "UpdateProgress",
        Minimum = 0,
        Maximum = 100
    };
    private readonly Button _check = ActionButton("CheckForUpdates");
    private readonly Button _download = ActionButton("DownloadUpdate");
    private readonly Button _restart = ActionButton("RestartToUpdate");
    private readonly Button _cancel = ActionButton("CancelUpdate");
    private readonly Button _save = ActionButton("SaveUpdatePreferences");
    private UpdateStatus _status;
    private CancellationTokenSource? _active;
    private bool _loaded;
    private bool _saving;
    private bool _closed;

    internal UpdateSettingsView(IDesktopIntegration? desktop, IClientUpdates? updates, CancellationToken lifetime)
    {
        _desktop = desktop;
        _updates = updates;
        _activity = updates as IClientUpdateActivity;
        if (_activity is not null)
        {
            _activity.Changed += OnActivityChanged;
        }
        _lifetime = lifetime;
        _status = updates?.Current ?? new UpdateStatus(UpdateStage.Inactive);
        StackPanel form = new StackPanel
        {
            Margin = new Avalonia.Thickness(24, 16),
            Spacing = 12
        };
        _preferences.Children.Add(_checks);
        _preferences.Children.Add(new TextBlock
        {
            Text = Strings.Get("UpdateSchedule"),
            TextWrapping = TextWrapping.Wrap
        });
        AutomationProperties.SetName(_schedule, Strings.Get("UpdateSchedule"));
        _preferences.Children.Add(_schedule);
        _preferences.Children.Add(_downloads);
        _preferences.Children.Add(_unmetered);
        _checks.IsCheckedChanged += (_, _) => Render();
        _downloads.IsCheckedChanged += (_, _) => Render();
        form.Children.Add(_preferences);
        form.Children.Add(_availability);
        form.Children.Add(_version);
        form.Children.Add(_progress);
        form.Children.Add(_progressText);
        WrapPanel actions = new WrapPanel();
        foreach (Button button in new[] { _check, _download, _restart, _cancel })
        {
            actions.Children.Add(button);
        }
        form.Children.Add(actions);
        form.Children.Add(new TextBlock
        {
            Text = Strings.Get("UpdateRestartConsent"),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(_save);
        form.Children.Add(_saved);
        AutomationProperties.SetLiveSetting(_availability, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_saved, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, Strings.Get("UpdateProgress"));
        Content = FormControls.Page("", form);
        _check.Click += (_, _) => Track(() => RunAsync(UpdateStage.Checking));
        _download.Click += (_, _) => Track(() => RunAsync(UpdateStage.Downloading));
        // The application update owner observes Restart. Tracking it among the actions it must drain
        // would make safe shutdown await itself. The event still observes completion and failures.
        _restart.Click += async (_, _) => await RunAsync(UpdateStage.RestartPreparing);
        _cancel.Click += (_, _) =>
        {
            _active?.Cancel();
            if (_activity is not null)
            {
                Track(_activity.CancelAsync);
            }
            Render();
        };
        _save.Click += (_, _) => Track(SaveAsync);
        Render();
    }

    internal async Task LoadAsync()
    {
        try
        {
            if (_desktop is not null)
            {
                UpdatePreferences preferences = (await _desktop.ReadPreferencesAsync(_lifetime)).Updates ?? UpdatePreferences.Default;
                _lifetime.ThrowIfCancellationRequested();
                _checks.IsChecked = preferences.ChecksEnabled;
                _downloads.IsChecked = preferences.DownloadsEnabled;
                _unmetered.IsChecked = preferences.UnmeteredOnly;
                _schedule.SelectedIndex = preferences.CheckIntervalHours == 168 ? 1 : 0;
                _loaded = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _saved.Text = Strings.Describe(Failure.Unavailable);
        }
        Render();
    }

    internal void Stop()
    {
        _closed = true;
        _active?.Cancel();
        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;
        }
    }

    private async Task SaveAsync()
    {
        if (!_loaded || _desktop is null || !_save.IsEnabled)
        {
            return;
        }
        _saving = true;
        Render();
        try
        {
            UpdatePreferences preferences = new UpdatePreferences(_checks.IsChecked == true, _downloads.IsChecked == true, _unmetered.IsChecked == true)
            {
                CheckIntervalHours = _schedule.SelectedIndex == 1 ? 168 : 24
            };
            Failure result = await _desktop.ConfigureUpdatesAsync(preferences, _lifetime);
            _lifetime.ThrowIfCancellationRequested();
            _saved.Text = result == Failure.None ? Strings.Get("UpdatePreferencesSaved") : Strings.Describe(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _saved.Text = Strings.Describe(Failure.Unavailable);
        }
        finally
        {
            _saving = false;
            Render();
        }
    }

    private async Task RunAsync(UpdateStage operation)
    {
        if (_closed || _active is not null || _updates is null || operation switch
        {
            UpdateStage.Checking => !_check.IsEnabled,
            UpdateStage.Downloading => !_download.IsEnabled,
            _ => !_restart.IsEnabled
        })
        {
            return;
        }
        using CancellationTokenSource active = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _active = active;
        _status = _status with
        {
            Stage = operation,
            Percentage = null,
            Failure = Failure.None
        };
        Render();
        Progress<UpdateStatus> progress = new Progress<UpdateStatus>(status => Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && _active == active && !active.IsCancellationRequested)
            {
                _status = _activity is null ? status : _updates.Current;
                Render();
            }
        }));
        try
        {
            UpdateStatus result = operation switch
            {
                UpdateStage.Checking => await _updates.CheckAsync(progress, active.Token),
                UpdateStage.Downloading => await _updates.DownloadAsync(progress, active.Token),
                _ => await _updates.RestartAsync(active.Token)
            };
            active.Token.ThrowIfCancellationRequested();
            _status = _activity is null ? result : _updates.Current;
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            _status = _activity is null ? new UpdateStatus(UpdateStage.Cancelled) : _updates.Current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status = new UpdateStatus(UpdateStage.Failed, Failure: Failure.Unavailable);
        }
        finally
        {
            _active = null;
            if (_activity is not null)
            {
                _status = _updates.Current;
            }
            Render();
        }
    }

    private void Render()
    {
        if (_closed)
        {
            return;
        }
        bool operating = _active is not null || _activity?.IsBusy == true;
        bool busy = operating || _saving;
        bool cancelling = _active?.IsCancellationRequested == true || _activity?.IsCancelling == true;
        _preferences.IsEnabled = _loaded && !_saving;
        _save.IsEnabled = _loaded && !_saving;
        _schedule.IsEnabled = _checks.IsChecked == true;
        _unmetered.IsEnabled = _downloads.IsChecked == true;
        _check.IsEnabled = _loaded && !busy && _checks.IsChecked == true && _status.Stage is UpdateStage.Idle or UpdateStage.Current or UpdateStage.Available or UpdateStage.Ready or UpdateStage.Cancelled or UpdateStage.Failed or UpdateStage.RestartBlocked;
        _download.IsEnabled = _loaded && !busy && _downloads.IsChecked == true && _status.Stage == UpdateStage.Available;
        _restart.IsEnabled = _loaded && !busy && _status.CanRestart && _status.Stage is UpdateStage.Ready or UpdateStage.RestartBlocked;
        _cancel.IsVisible = operating;
        _cancel.IsEnabled = operating && !cancelling && _status.Stage is not (UpdateStage.RestartPreparing or UpdateStage.RestartCommitted);
        _availability.Text = Strings.Get(cancelling ? "UpdateCancelling" : _status.Stage == UpdateStage.Inactive ? "UpdatesUnavailable" :
            _status.Stage == UpdateStage.Ready && _status.CanRestart ? "UpdateReadyToRestart" : "UpdateState" + _status.Stage);
        if (_status.Failure != Failure.None)
        {
            _availability.Text += " " + (_status.Stage == UpdateStage.RestartCommitted ? Strings.Describe(_status.Failure) : _status.Explanation ?? (_status.Failure switch
            {
                Failure.Validation or Failure.Corrupt or Failure.NeedsReview => Strings.Get("UpdateVerificationFailed"),
                Failure.Unavailable or Failure.Transient => Strings.Get("UpdateConnectionFailed"),
                Failure.PolicyBlocked => Strings.Get("UpdatePolicyBlocked"),
                _ => Strings.Describe(_status.Failure)
            }));
        }
        if (_status.RetryAt is DateTimeOffset retry)
        {
            _availability.Text += " " + Strings.Get("UpdateRetryAt") + " " + retry.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }
        _version.Text = _status.Version is null ? "" : Strings.Get("UpdateVersion") + ": " + _status.Version;
        _version.IsVisible = _status.Version is not null;
        bool transferring = busy && _status.Stage is UpdateStage.Downloading or UpdateStage.Verifying;
        _progress.IsVisible = transferring;
        bool known = _status.Percentage is double percentage && double.IsFinite(percentage) && percentage >= 0 && percentage <= 100;
        _progress.IsIndeterminate = !known;
        _progress.Value = known ? _status.Percentage!.Value : 0;
        _progressText.IsVisible = transferring && known;
        _progressText.Text = known ? _progress.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "";
    }

    private void Track(Func<Task> action)
    {
        Task task = action();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            FormControls.Track(window, task);
        }
    }

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
            {
                // Read the latest owner snapshot: queued callbacks from old operations carry no state.
                _status = _updates!.Current;
                Render();
            }
        });
    }

    private static CheckBox Choice(string key)
    {
        CheckBox choice = new CheckBox
        {
            Name = key,
            Content = Strings.Get(key),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(choice, Strings.Get(key));
        return choice;
    }

    private static Button ActionButton(string key)
    {
        Button button = new Button
        {
            Name = key,
            Content = new TextBlock
            {
                Text = Strings.Get(key),
                TextWrapping = TextWrapping.Wrap
            },
            Margin = new Avalonia.Thickness(0, 0, 8, 8)
        };
        AutomationProperties.SetName(button, Strings.Get(key));
        return button;
    }
}
