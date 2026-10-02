using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace KsfCompanion.Ui
{
    /// <summary>
    /// One of the app's icons (Lucide's line icons, drawn on a 24 x 24 grid - the geometries are in Theme.axaml),
    /// in the text colour around it, like a glyph of an icon font. Filled draws it solid (the saved star).
    /// </summary>
    sealed class Icon : Control
    {
        public static readonly StyledProperty<Geometry> DataProperty = AvaloniaProperty.Register<Icon, Geometry>(nameof(Data));
        public static readonly StyledProperty<IBrush> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();
        public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));
        public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2.0);

        static Icon() => AffectsRender<Icon>(DataProperty, ForegroundProperty, FilledProperty, StrokeWidthProperty);

        public Geometry Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
        public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
        public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

        public override void Render(DrawingContext context)
        {
            var data = Data;
            var brush = Foreground;
            var size = Math.Min(Bounds.Width, Bounds.Height);
            if (data == null || brush == null || size <= 0) return;
            var scale = size / 24;
            var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
            using (context.PushTransform(transform))
                context.DrawGeometry(Filled ? brush : null, new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
