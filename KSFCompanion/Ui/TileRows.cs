using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace KsfCompanion.Ui
{
    /// <summary>
    /// The number tiles: <see cref="Columns"/> to a row, each row as tall as its tallest tile. A last row with fewer
    /// tiles shares out the whole width, so there's never a hole at its end.
    /// </summary>
    sealed class TileRows : Panel
    {
        public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<TileRows, int>(nameof(Columns), 4);

        static TileRows() => AffectsMeasure<TileRows>(ColumnsProperty);

        public int Columns
        {
            get => GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        protected override Size MeasureOverride(Size available)
        {
            var tiles = Children.Where(c => c.IsVisible).ToList();
            var columns = Math.Max(1, Columns);
            double height = 0, width = 0;
            for (var start = 0; start < tiles.Count; start += columns)
            {
                var row = tiles.Skip(start).Take(columns).ToList();
                var cell = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / row.Count;
                double rowHeight = 0, rowWidth = 0;
                foreach (var tile in row)
                {
                    tile.Measure(new Size(cell, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, tile.DesiredSize.Height);
                    rowWidth += tile.DesiredSize.Width;
                }
                height += rowHeight;
                width = Math.Max(width, rowWidth);
            }
            return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var tiles = Children.Where(c => c.IsVisible).ToList();
            var columns = Math.Max(1, Columns);
            double y = 0;
            for (var start = 0; start < tiles.Count; start += columns)
            {
                var row = tiles.Skip(start).Take(columns).ToList();
                var cell = final.Width / row.Count;
                var rowHeight = row.Max(t => t.DesiredSize.Height);
                for (var i = 0; i < row.Count; i++)
                    row[i].Arrange(new Rect(i * cell, y, cell, rowHeight));
                y += rowHeight;
            }
            return final;
        }
    }
}
