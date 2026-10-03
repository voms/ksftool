using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace KsfCompanion.Ui
{
    /// <summary>
    /// A row of short bars, one per stage or bonus, like the zones on ksf.surf's player pages: green for the ones
    /// you've done, dark red for the rest. Pattern is one character a bar: '1' done, '0' not yet.
    /// </summary>
    sealed class ZoneBar : Control
    {
        static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.Parse("#2FB879")), ToDoBrush = new ImmutableSolidColorBrush(Color.Parse("#6E2A2A"));

        public static readonly StyledProperty<string> PatternProperty = AvaloniaProperty.Register<ZoneBar, string>(nameof(Pattern));

        static ZoneBar() => AffectsRender<ZoneBar>(PatternProperty);

        public string Pattern { get => GetValue(PatternProperty); set => SetValue(PatternProperty, value); }

        public override void Render(DrawingContext context)
        {
            var pattern = Pattern;
            if (string.IsNullOrEmpty(pattern) || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            // Thinner gaps when there are many bars, so even 30 stages fit.
            var gap = pattern.Length > 12 ? 1.0 : 2.0;
            var width = (Bounds.Width - gap * (pattern.Length - 1)) / pattern.Length;
            if (width <= 0) return;
            for (var i = 0; i < pattern.Length; i++)
            {
                var x = Math.Round(i * (width + gap), 1);
                context.FillRectangle(pattern[i] == '1' ? DoneBrush : ToDoBrush, new Rect(x, 0, Math.Max(1, width), Bounds.Height), 1);
            }
        }
    }
}
