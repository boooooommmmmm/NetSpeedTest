using System.Globalization;
using System.Text;

namespace NetPulse.Core;

public enum ProbeMode { Icmp, Tcp }
public enum TrafficPhase { Idle, Download, Upload }

public sealed record ProbeSample(DateTimeOffset Time, string Target, ProbeMode Mode,
    TrafficPhase Phase, double? LatencyMs, string Detail)
{
    public bool Success => LatencyMs.HasValue;
    public string PhaseLabel => Phase switch { TrafficPhase.Download => "下载负载", TrafficPhase.Upload => "上传负载", _ => "空闲" };
}

public sealed class LatencyStatistics
{
    private readonly List<double> _latencies = [];
    private double _sum, _differenceSum;
    private double? _previous;
    private int _pairs;
    public int Total { get; private set; }
    public int Lost { get; private set; }
    public int Received => Total - Lost;
    public double LossPercent => Total == 0 ? 0 : Lost * 100d / Total;
    public double? Average => Received == 0 ? null : _sum / Received;
    public double? Minimum { get; private set; }
    public double? Maximum { get; private set; }
    public double? Jitter => _pairs == 0 ? null : _differenceSum / _pairs;
    public double? P95
    {
        get
        {
            if (_latencies.Count == 0) return null;
            var sorted = _latencies.Order().ToArray();
            return sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
        }
    }

    public void Add(ProbeSample sample)
    {
        Total++;
        if (sample.LatencyMs is not { } value)
        {
            Lost++;
            _previous = null;
            return;
        }
        _latencies.Add(value);
        _sum += value;
        Minimum = Math.Min(Minimum ?? value, value);
        Maximum = Math.Max(Maximum ?? value, value);
        if (_previous is { } previous) { _differenceSum += Math.Abs(value - previous); _pairs++; }
        _previous = value;
    }
}

public static class CsvExporter
{
    public static string Export(IEnumerable<ProbeSample> samples)
    {
        var sb = new StringBuilder("时间,目标,探测方式,网络负载,延迟_ms,成功,详情\r\n");
        foreach (var s in samples)
            sb.AppendJoin(',', Escape(s.Time.ToString("O")), Escape(s.Target), s.Mode.ToString(), s.PhaseLabel,
                s.LatencyMs?.ToString("F3", CultureInfo.InvariantCulture) ?? "", s.Success ? "1" : "0", Escape(s.Detail)).Append("\r\n");
        return sb.ToString();
    }

    private static string Escape(string text)
    {
        // Prevent a user-supplied host or server error from becoming a spreadsheet formula.
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') ||
            text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
