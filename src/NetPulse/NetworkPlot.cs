using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetPulse;

public sealed record PlotPoint(DateTimeOffset Time, double? Value, bool Upload = false);

public sealed class NetworkPlot : FrameworkElement
{
    private IReadOnlyList<PlotPoint> _points = [];
    public string Unit { get; set; } = "ms";
    public string EmptyText { get; set; } = "开始监测后显示真实采样数据";
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(126, 148, 179));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(36, 53, 76)), 1);
    private static readonly Pen GreenPen = new(new SolidColorBrush(Color.FromRgb(86, 221, 183)), 1.8);
    private static readonly Pen BluePen = new(new SolidColorBrush(Color.FromRgb(121, 175, 255)), 1.8);
    private static readonly Pen LostPen = new(new SolidColorBrush(Color.FromRgb(248, 119, 133)), 2);

    public NetworkPlot()
    {
        ClipToBounds = true;
        MouseMove += (_, e) =>
        {
            if (_points.Count == 0) return;
            var x = Math.Clamp((e.GetPosition(this).X - 44) / Math.Max(1, ActualWidth - 52), 0, 1);
            var point = _points[(int)Math.Round(x * (_points.Count - 1))];
            ToolTip = $"{point.Time:HH:mm:ss}  " + (point.Value is { } value ? $"{value:F2} {Unit}" : "探测失败");
        };
    }

    public void SetPoints(IEnumerable<PlotPoint> points)
    { _points = points.TakeLast(180).ToArray(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 70 || h < 40) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const double left = 44, top = 9, bottom = 21;
        var pw = w - left - 8;
        var ph = h - top - bottom;
        var max = Math.Max(10, _points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).DefaultIfEmpty(0).Max() * 1.18);
        max = Math.Ceiling(max / (max > 100 ? 50 : 10)) * (max > 100 ? 50 : 10);
        for (var i = 0; i <= 2; i++)
        {
            var y = top + ph * i / 2;
            dc.DrawLine(GridPen, new(left, y), new(w - 8, y));
            Label(dc, (max * (2 - i) / 2).ToString(max > 100 ? "0" : "0.#", CultureInfo.InvariantCulture), 0, y - 7, 10);
        }
        if (_points.Count == 0)
        {
            Label(dc, EmptyText, left + 12, top + ph / 2 - 8, 11);
            return;
        }
        Point? previous = null;
        bool? previousUpload = null;
        for (var i = 0; i < _points.Count; i++)
        {
            var sample = _points[i];
            var x = left + (_points.Count == 1 ? 0 : i * pw / (_points.Count - 1));
            if (sample.Value is not { } value)
            {
                dc.DrawLine(LostPen, new(x, top + ph - 12), new(x, top + ph));
                previous = null;
                continue;
            }
            var point = new Point(x, top + ph * (1 - value / max));
            var pen = sample.Upload ? BluePen : GreenPen;
            if (previous is { } last && previousUpload == sample.Upload) dc.DrawLine(pen, last, point);
            if (_points.Count < 3 || i == _points.Count - 1) dc.DrawEllipse(pen.Brush, null, point, 2.6, 2.6);
            previous = point;
            previousUpload = sample.Upload;
        }
        Label(dc, _points[0].Time.ToString("HH:mm:ss"), left, h - 15, 9);
        if (pw > 160) Label(dc, _points[^1].Time.ToString("HH:mm:ss"), w - 55, h - 15, 9);
        Label(dc, Unit, 0, h - 15, 9);
    }

    private void Label(DrawingContext dc, string text, double x, double y, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new(x, y));
    }
}
