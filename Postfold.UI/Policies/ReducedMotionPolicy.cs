using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Microsoft.Win32;
using System;

namespace Postfold.UI.Policies;

/// <summary>Suppresses transitions when Windows disables animation; other platforms conservatively reduce motion.</summary>
public sealed class ReducedMotionPolicy : IObserver<AvaloniaPropertyChangedEventArgs>, IDisposable
{
    internal const string ResourceKey = "PostfoldReducedMotion";
    private readonly IResourceDictionary? _resources;
    private readonly object? _previous;
    private readonly bool _hadPrevious;
    private readonly IDisposable? _subscription;

    public ReducedMotionPolicy(bool? reduceMotion = null)
    {
        bool reduced = reduceMotion ?? ShouldReduceMotion();
        _resources = Application.Current?.Resources;
        if (_resources is not null)
        {
            _hadPrevious = _resources.TryGetValue(ResourceKey, out _previous);
            _resources[ResourceKey] = reduced;
        }
        if (reduced)
        {
            _subscription = Animatable.TransitionsProperty.Changed.Subscribe(this);
        }
    }

    public void OnNext(AvaloniaPropertyChangedEventArgs value)
    {
        if (value.Sender is Animatable element && element.Transitions is not null)
        {
            element.Transitions = null;
        }
    }

    public void OnError(Exception error)
    {
        throw new InvalidOperationException("The reduced-motion property subscription failed.", error);
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        if (_resources is not null)
        {
            if (_hadPrevious)
            {
                _resources[ResourceKey] = _previous;
            }
            else
            {
                _resources.Remove(ResourceKey);
            }
        }
    }

    private static bool ShouldReduceMotion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1")?.ToString() == "0" ||
                Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask", null) is byte[] mask &&
                mask.Length > 4 && (mask[4] & 2) == 0;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
