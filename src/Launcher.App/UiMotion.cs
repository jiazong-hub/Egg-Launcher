using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.App;

/// <summary>
/// Centralised, low-cost motion helpers. Animations are limited to opacity and
/// transforms so they do not invalidate WPF layout on every frame.
/// </summary>
public static class UiMotion
{
    public const int FastMilliseconds = 90;
    public const int StandardMilliseconds = 160;
    public const int EntranceMilliseconds = 200;

    private static readonly DependencyProperty WindowMotionStateProperty =
        DependencyProperty.RegisterAttached(
            "WindowMotionState",
            typeof(WindowMotionState),
            typeof(UiMotion));
    private static readonly ConditionalWeakTable<DependencyObject, MotionRegistry> MotionRegistries = new();

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public static void AttachWindowEntrance(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.GetValue(WindowMotionStateProperty) is WindowMotionState)
        {
            return;
        }

        var state = new WindowMotionState();
        window.SetValue(WindowMotionStateProperty, state);
        if (!AnimationsEnabled)
        {
            return;
        }

        window.Opacity = 0;
        window.Loaded += Window_Loaded;
    }

    public static async Task<bool> AnimateEntranceAsync(
        FrameworkElement element,
        double offsetX = 0,
        double offsetY = 8,
        int durationMilliseconds = EntranceMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = false;
        var translate = EnsureTranslateTransform(element);

        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            translate.X = 0;
            translate.Y = 0;
            element.IsHitTestVisible = true;
            return true;
        }

        Stop(element, UIElement.OpacityProperty);
        Stop(translate, TranslateTransform.XProperty);
        Stop(translate, TranslateTransform.YProperty);
        element.Opacity = 0;
        translate.X = offsetX;
        translate.Y = offsetY;

        var completed = await Task.WhenAll(
            AnimateAsync(element, UIElement.OpacityProperty, 1, durationMilliseconds),
            AnimateAsync(translate, TranslateTransform.XProperty, 0, durationMilliseconds),
            AnimateAsync(translate, TranslateTransform.YProperty, 0, durationMilliseconds));
        if (completed.All(value => value))
        {
            element.IsHitTestVisible = true;
            return true;
        }

        return false;
    }

    public static async Task<bool> AnimateExitAsync(
        FrameworkElement element,
        double offsetX = 0,
        double offsetY = -4,
        int durationMilliseconds = FastMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!AnimationsEnabled || element.Visibility != Visibility.Visible)
        {
            return true;
        }

        element.IsHitTestVisible = false;
        var translate = EnsureTranslateTransform(element);
        var completed = await Task.WhenAll(
            AnimateAsync(element, UIElement.OpacityProperty, 0, durationMilliseconds, easeIn: true),
            AnimateAsync(translate, TranslateTransform.XProperty, offsetX, durationMilliseconds, easeIn: true),
            AnimateAsync(translate, TranslateTransform.YProperty, offsetY, durationMilliseconds, easeIn: true));
        return completed.All(value => value);
    }

    public static async Task SwapAsync(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        double incomingOffsetY = 8)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        if (ReferenceEquals(outgoing, incoming))
        {
            return;
        }

        if (!AnimationsEnabled)
        {
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;
            Reset(outgoing);
            Reset(incoming);
            return;
        }

        if (!await AnimateExitAsync(outgoing))
        {
            return;
        }

        outgoing.Visibility = Visibility.Collapsed;
        Reset(outgoing);
        _ = await AnimateEntranceAsync(incoming, offsetY: incomingOffsetY, durationMilliseconds: StandardMilliseconds);
    }

    public static void AnimateRefresh(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!AnimationsEnabled)
        {
            return;
        }

        var translate = EnsureTranslateTransform(element);
        Stop(element, UIElement.OpacityProperty);
        Stop(translate, TranslateTransform.YProperty);
        element.Opacity = 0.82;
        translate.Y = 3;
        _ = Task.WhenAll(
            AnimateAsync(element, UIElement.OpacityProperty, 1, StandardMilliseconds),
            AnimateAsync(translate, TranslateTransform.YProperty, 0, StandardMilliseconds));
    }

    public static Task<bool> FadeToAsync(UIElement element, double opacity, int durationMilliseconds = StandardMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(element);
        return AnimateAsync(element, UIElement.OpacityProperty, opacity, durationMilliseconds, opacity < element.Opacity);
    }

    public static void RotateOnce(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!AnimationsEnabled)
        {
            return;
        }

        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        var rotate = element.RenderTransform as RotateTransform;
        if (rotate is null)
        {
            rotate = new RotateTransform();
            element.RenderTransform = rotate;
        }

        Stop(rotate, RotateTransform.AngleProperty);
        rotate.Angle = 0;
        _ = AnimateAsync(rotate, RotateTransform.AngleProperty, 360, 420);
    }

    public static void Reset(FrameworkElement element)
    {
        Stop(element, UIElement.OpacityProperty);
        element.Opacity = 1;
        if (FindTranslateTransform(element.RenderTransform) is { } translate)
        {
            Stop(translate, TranslateTransform.XProperty);
            Stop(translate, TranslateTransform.YProperty);
            translate.X = 0;
            translate.Y = 0;
        }

        element.IsHitTestVisible = true;
    }

    private static async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window
            || window.GetValue(WindowMotionStateProperty) is not WindowMotionState state
            || state.EntrancePlayed)
        {
            return;
        }

        state.EntrancePlayed = true;
        window.Loaded -= Window_Loaded;
        if (window.Content is FrameworkElement content)
        {
            content.IsHitTestVisible = false;
            var translate = EnsureTranslateTransform(content);
            translate.Y = 6;
            var completed = await Task.WhenAll(
                AnimateAsync(window, UIElement.OpacityProperty, 1, EntranceMilliseconds),
                AnimateAsync(translate, TranslateTransform.YProperty, 0, EntranceMilliseconds));
            if (completed.All(value => value))
            {
                content.IsHitTestVisible = true;
            }
        }
        else
        {
            await AnimateAsync(window, UIElement.OpacityProperty, 1, EntranceMilliseconds);
        }
    }

    private static TranslateTransform EnsureTranslateTransform(FrameworkElement element)
    {
        if (FindTranslateTransform(element.RenderTransform) is { } existing)
        {
            return existing;
        }

        var translate = new TranslateTransform();
        if (element.RenderTransform is null || element.RenderTransform == Transform.Identity)
        {
            element.RenderTransform = translate;
            return translate;
        }

        var group = new TransformGroup();
        group.Children.Add(element.RenderTransform);
        group.Children.Add(translate);
        element.RenderTransform = group;
        return translate;
    }

    private static TranslateTransform? FindTranslateTransform(Transform? transform) => transform switch
    {
        TranslateTransform translate => translate,
        TransformGroup group => group.Children.OfType<TranslateTransform>().LastOrDefault(),
        _ => null,
    };

    private static Task<bool> AnimateAsync(
        DependencyObject target,
        DependencyProperty property,
        double to,
        int durationMilliseconds,
        bool easeIn = false)
    {
        if (!AnimationsEnabled || durationMilliseconds <= 0)
        {
            target.SetValue(property, to);
            return Task.FromResult(true);
        }

        var registry = MotionRegistries.GetOrCreateValue(target);
        var operation = new MotionOperation();
        lock (registry.Gate)
        {
            if (registry.Operations.Remove(property, out var previous))
            {
                previous.Completion.TrySetResult(false);
            }

            registry.Operations[property] = operation;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = easeIn ? EasingMode.EaseIn : EasingMode.EaseOut,
            },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            var isCurrent = false;
            lock (registry.Gate)
            {
                if (registry.Operations.TryGetValue(property, out var current)
                    && ReferenceEquals(current, operation))
                {
                    registry.Operations.Remove(property);
                    isCurrent = true;
                }
            }

            if (isCurrent)
            {
                target.SetValue(property, to);
                BeginAnimation(target, property, null);
            }

            operation.Completion.TrySetResult(isCurrent);
        };
        BeginAnimation(target, property, animation, HandoffBehavior.SnapshotAndReplace);
        return operation.Completion.Task;
    }

    private static void Stop(DependencyObject target, DependencyProperty property)
    {
        if (MotionRegistries.TryGetValue(target, out var registry))
        {
            lock (registry.Gate)
            {
                if (registry.Operations.Remove(property, out var operation))
                {
                    operation.Completion.TrySetResult(false);
                }
            }
        }

        BeginAnimation(target, property, null);
    }

    private static void BeginAnimation(
        DependencyObject target,
        DependencyProperty property,
        AnimationTimeline? animation,
        HandoffBehavior handoffBehavior = HandoffBehavior.SnapshotAndReplace)
    {
        switch (target)
        {
            case UIElement element:
                element.BeginAnimation(property, animation, handoffBehavior);
                break;
            case Animatable animatable:
                animatable.BeginAnimation(property, animation, handoffBehavior);
                break;
            default:
                throw new ArgumentException("The motion target does not support WPF animation.", nameof(target));
        }
    }

    private sealed class WindowMotionState
    {
        public bool EntrancePlayed { get; set; }
    }

    private sealed class MotionRegistry
    {
        public object Gate { get; } = new();

        public Dictionary<DependencyProperty, MotionOperation> Operations { get; } = [];
    }

    private sealed class MotionOperation
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
