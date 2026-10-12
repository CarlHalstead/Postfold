using System;
using Avalonia;
using Avalonia.Animation;

namespace Postfold.UI;

/// <summary>Development client uses immediate control state changes; theme transitions are disabled throughout.</summary>
public sealed class ReducedMotionPolicy : IObserver<AvaloniaPropertyChangedEventArgs>, IDisposable
{
    private readonly IDisposable _subscription;

    public ReducedMotionPolicy()
    {
        _subscription = Animatable.TransitionsProperty.Changed.Subscribe(this);
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
        _subscription.Dispose();
    }
}
