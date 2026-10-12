using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;

namespace Postfold.UI;

internal static class UiForm
{
    private static readonly ConditionalWeakTable<Window, List<Task>> Work = new ConditionalWeakTable<Window, List<Task>>();

    internal static async Task DrainAsync(Window window)
    {
        if (Work.TryGetValue(window, out List<Task>? tasks))
        {
            try
            {
                await Task.WhenAll(tasks.ToArray());
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.IO.IOException or
                UnauthorizedAccessException)
            {
                // These failures were already reported by Action; compose persistence has its own shutdown barrier.
            }
        }
    }

    internal static TextBox Field(StackPanel panel, string key, string value = "", bool password = false)
    {
        string label = Strings.Get(key);
        TextBox field = new TextBox { Text = value, PasswordChar = password ? '●' : '\0' };
        AutomationProperties.SetName(field, label);
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(field);
        return field;
    }

    internal static Button Action(StackPanel panel, string key, Func<Task> action, TextBlock status)
    {
        Button button = new Button { Content = Strings.Get(key) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                Task task = action();
                if (TopLevel.GetTopLevel(button) is Window window)
                {
                    while (window.Owner is Window owner)
                    {
                        window = owner;
                    }

                    List<Task> tasks = Work.GetOrCreateValue(window);
                    tasks.RemoveAll(item => item.IsCompleted);
                    tasks.Add(task);
                }

                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.IO.IOException)
            {
                status.Text = Strings.DraftFailed;
            }
            catch (UnauthorizedAccessException)
            {
                status.Text = Strings.Get("DestinationDenied");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        panel.Children.Add(button);
        return button;
    }
}
