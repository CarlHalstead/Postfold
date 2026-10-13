using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using System;

namespace Postfold.UI.Controls;

/// <summary>Clips a native tree's child presenter while smoothly changing its measured height.</summary>
public sealed class AnimatedDisclosure : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<AnimatedDisclosure, bool>(nameof(IsExpanded));
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<AnimatedDisclosure, double>(nameof(Progress));

    static AnimatedDisclosure()
    {
        AffectsMeasure<AnimatedDisclosure>(ProgressProperty);
    }

    public AnimatedDisclosure()
    {
        ClipToBounds = true;
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = ProgressProperty,
                Duration = TimeSpan.FromMilliseconds(160)
            }
        };
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            if (IsExpanded && Child is not null)
            {
                Child.IsVisible = true;
            }
            Progress = IsExpanded ? 1 : 0;
            IsHitTestVisible = IsExpanded;
        }
        if (change.Property == ProgressProperty && Progress == 0 && !IsExpanded && Child is not null)
        {
            Child.IsVisible = false;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(availableSize);
        Size desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width, desired.Height * Math.Clamp(Progress, 0, 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }
}
