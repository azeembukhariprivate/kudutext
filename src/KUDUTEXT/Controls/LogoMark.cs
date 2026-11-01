using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudutext.Controls
{
    /// <summary>The KUDUTEXT mark: a speech bubble with lines of text in it. Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            var bubble = new Path
            {
                StrokeThickness = 3.5,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 16,12 H 48 A 6,6 0 0 1 54,18 V 38 A 6,6 0 0 1 48,44 H 30 L 20,53 V 44 H 16 A 6,6 0 0 1 10,38 V 18 A 6,6 0 0 1 16,12 Z")
            };
            Bind(bubble, Shape.StrokeProperty);
            canvas.Children.Add(bubble);
            foreach (var (x, y, w) in new[] { (18.0, 21.0, 28.0), (18.0, 28.0, 22.0), (18.0, 35.0, 14.0) })
            {
                var line = new Rectangle { Width = w, Height = 3.4, RadiusX = 1.7, RadiusY = 1.7 };
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, y);
                Bind(line, Shape.FillProperty);
                canvas.Children.Add(line);
            }
            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }

    /// <summary>The app tile: the mark in white on an orange rounded square.</summary>
    public static class LogoTile
    {
        public static Border Create(double size)
        {
            var mark = new LogoMark(size);
            mark.SetValue(TextElement.ForegroundProperty, Brushes.White);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x59, 0x0C)),
                Child = mark
            };
        }
    }
}
