using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;

namespace KsfCompanion.Ui
{
    /// <summary>
    /// ui:BarFill.Value="{Binding ...}" on a bar (0..1): instead of jumping, the bar fills smoothly to the new
    /// value - from empty when it first appears, from the old length when a new time comes in.
    /// ui:BarFill.Fraction does the same without the animation (for bars that move every second).
    /// </summary>
    sealed class BarFill : AvaloniaObject
    {
        /// <summary>Off for --preview renders, which take their picture before any animation has run.</summary>
        public static bool Animate { get; set; } = true;

        // The default isn't a real value, so the first one bound - even 0 - sets the bar up (an untouched bar would show full).
        public static readonly AttachedProperty<double> ValueProperty =
            AvaloniaProperty.RegisterAttached<BarFill, Visual, double>("Value", -1.0);

        public static readonly AttachedProperty<double> FractionProperty =
            AvaloniaProperty.RegisterAttached<BarFill, Visual, double>("Fraction", -1.0);

        public static double GetValue(Visual element) => element.GetValue(ValueProperty);
        public static void SetValue(Visual element, double value) => element.SetValue(ValueProperty, value);
        public static double GetFraction(Visual element) => element.GetValue(FractionProperty);
        public static void SetFraction(Visual element, double value) => element.SetValue(FractionProperty, value);

        static BarFill()
        {
            ValueProperty.Changed.AddClassHandler<Visual>((visual, e) => Show(visual, (double)e.NewValue, animate: Animate));
            FractionProperty.Changed.AddClassHandler<Visual>((visual, e) => Show(visual, (double)e.NewValue, animate: false));
        }

        static void Show(Visual visual, double value, bool animate)
        {
            visual.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
            // The bar gets its own transform, starting empty.
            if (!(visual.RenderTransform is ScaleTransform scale))
                visual.RenderTransform = scale = new ScaleTransform(0, 1);
            var to = Math.Max(0, Math.Min(1, value));
            if (!animate || Math.Abs(to - scale.ScaleX) < 0.0001)
            {
                scale.Transitions = null;
                scale.ScaleX = to;
                return;
            }
            // Longer for a bigger change, so a new best visibly grows into place.
            var distance = Math.Abs(to - scale.ScaleX);
            scale.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleXProperty,
                    Duration = TimeSpan.FromMilliseconds(600 + 1400 * Math.Min(1, distance * 2)),
                    Easing = new CubicEaseOut(),
                },
            };
            scale.ScaleX = to;
        }
    }
}
