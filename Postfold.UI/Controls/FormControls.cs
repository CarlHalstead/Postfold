using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Postfold.UI.Resources;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Postfold.UI.Controls;

internal static class FormControls
{
    private static readonly ConditionalWeakTable<Window, List<Task>> Work = new ConditionalWeakTable<Window, List<Task>>();

    internal static DockPanel Page(string title, Control content, Panel? footer = null, Button? back = null)
    {
        DockPanel page = new DockPanel();
        if (title.Length > 0)
        {
            TextBlock heading = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            heading.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("SubjectFontSize"));
            Grid header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Avalonia.Thickness(24, 24, 24, 16)
            };
            if (back is not null)
            {
                back.Margin = new Avalonia.Thickness(0, 0, 12, 0);
                header.Children.Add(back);
            }
            header.Children.Add(heading);
            Grid.SetColumn(heading, 1);
            DockPanel.SetDock(header, Dock.Top);
            page.Children.Add(header);
        }
        if (footer is not null)
        {
            footer.Margin = new Avalonia.Thickness(24, 12);
            DockPanel.SetDock(footer, Dock.Bottom);
            page.Children.Add(footer);
        }
        page.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content
        });
        return page;
    }

    internal static Avalonia.Controls.Shapes.Path Icon(Geometry geometry)
    {
        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Width = 16,
            Height = 16
        };
    }

    internal static void ApplyTypography(Window window, double fontSize, FontFamily fontFamily)
    {
        window.FontSize = fontSize;
        window.FontFamily = fontFamily;
        window.Resources["SubjectFontSize"] = fontSize * 26 / 16;
        window.Resources["BodyFontSize"] = fontSize * 18 / 16;
        window.Resources["BodyLineHeight"] = fontSize * 27 / 16;
        window.Resources["MetadataFontSize"] = fontSize * 14 / 16;
    }

    internal static Grid Pair(Control first, Control second)
    {
        Grid pair = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,16,*")
        };
        pair.Children.Add(first);
        pair.Children.Add(second);
        Grid.SetColumn(second, 2);
        bool stacked = false;
        pair.SizeChanged += (_, _) =>
        {
            bool next = pair.Bounds.Width < 460 * pair.GetValue(TemplatedControl.FontSizeProperty) / 16;
            if (next == stacked)
            {
                return;
            }
            stacked = next;
            pair.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "150,16,*");
            pair.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "*");
            Grid.SetColumn(second, stacked ? 0 : 2);
            Grid.SetRow(second, stacked ? 1 : 0);
            second.Margin = stacked ? new Avalonia.Thickness(0, 12, 0, 0) : default;
        };
        return pair;
    }

    internal static void Track(Window window, Task task)
    {
        while (window.Owner is Window owner)
        {
            window = owner;
        }

        List<Task> tasks = Work.GetOrCreateValue(window);
        tasks.RemoveAll(item => item.IsCompleted);
        tasks.Add(task);
    }

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
                // These failures were already reported by AddActionButton; compose persistence has its own shutdown barrier.
            }
        }
    }

    internal static TextBox AddField(StackPanel panel, string key, string value = "", bool password = false)
    {
        string label = Strings.Get(key);
        TextBox field = new TextBox
        {
            Name = key,
            Text = value,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            PasswordChar = password ? '●' : '\0'
        };
        AutomationProperties.SetName(field, label);
        StackPanel group = new StackPanel { Spacing = 4 };
        TextBlock caption = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap
        };
        caption.Classes.Add("caption");
        group.Children.Add(caption);
        group.Children.Add(field);
        panel.Children.Add(group);
        return field;
    }

    internal static Button AddActionButton(Panel panel, string key, Func<Task> action, TextBlock status)
    {
        Button button = new Button
        {
            Name = key,
            Content = new TextBlock { Text = Strings.Get(key), TextWrapping = TextWrapping.Wrap },
            Margin = new Avalonia.Thickness(0, 0, 8, 8)
        };
        AutomationProperties.SetName(button, Strings.Get(key));
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        if (key is "Send" or "SaveSettings" or "SaveCredentials")
        {
            button.Classes.Add("primary");
        }
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                Task task = action();
                if (TopLevel.GetTopLevel(button) is Window window)
                {
                    Track(window, task);
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
