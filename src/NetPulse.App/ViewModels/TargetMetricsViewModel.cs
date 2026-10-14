using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using NetPulse.Core.Models;

namespace NetPulse.App.ViewModels;

public class TargetMetricsViewModel : ViewModelBase
{
    private TargetMetrics _model;
    private List<double> _chartValues = new();

    public TargetMetricsViewModel(TargetMetrics model)
    {
        _model = model;
        UpdateFromModel(model);
    }

    public string TargetId => _model.TargetId;
    public string TargetName => _model.TargetName;
    public string TargetHost => _model.TargetHost;
    public NetworkProtocol Protocol => _model.Protocol;
    public bool IsGateway => _model.IsGateway;
    public bool IsDnsResolver => _model.IsDnsResolver;

    public double CurrentLatency => _model.CurrentLatency;
    public double RollingAvg => _model.RollingAvg;
    public double MinLatency => _model.MinLatency == double.MaxValue ? 0 : _model.MinLatency;
    public double MaxLatency => _model.MaxLatency;
    public double MedianLatency => _model.MedianLatency;
    public double P95Latency => _model.P95Latency;
    public double Jitter => _model.Jitter;
    public double PacketLossPercent => _model.PacketLossPercent;
    public int TotalProbes => _model.TotalProbes;
    public bool IsReachable => _model.IsReachable;
    public string? LastError => _model.LastError;
    public HttpsTimingBreakdown? HttpsBreakdown => _model.LastHttpsBreakdown;

    public string LatencyDisplay => _model.IsReachable && _model.TotalProbes > 0
        ? $"{_model.CurrentLatency:F1} ms"
        : "Failed";

    public string AvgDisplay => $"{RollingAvg:F1} ms";
    public string JitterDisplay => $"{Jitter:F1} ms";
    public string LossDisplay => $"{PacketLossPercent:F1}%";
    public string P95Display => _model.P95Latency > 0 ? $"{_model.P95Latency:F1} ms" : "-";
    /// <summary>"usual 24 ms" - this target's own normal, which is what "higher than usual" is judged against.</summary>
    public string BaselineDisplay => _model.BaselineLatencyMs > 0 ? $"{_model.BaselineLatencyMs:F1} ms" : "learning...";
    public string StateText => !_model.IsReachable ? "Not answering" : _model.IsLatencyElevated ? "Higher than usual" : _model.TotalProbes == 0 ? "Waiting" : "Normal";
    /// <summary>Where an HTTPS probe's time went (Advanced): DNS, TCP connect, TLS handshake, first byte.</summary>
    public string HttpsTimingDisplay => _model.LastHttpsBreakdown is { } b
        ? $"DNS {b.DnsLookupMs:F0} · TCP {b.TcpConnectMs:F0} · TLS {b.TlsHandshakeMs:F0} · first byte {b.TtfbMs:F0} ms"
        : "";

    private static readonly Brush RedBrush = Frozen(239, 68, 68), AmberBrush = Frozen(245, 158, 11), GreenBrush = Frozen(16, 185, 129);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    // judged against the target's own normal, never against a fixed ping (a far-away server is not "bad" for being far)
    public Brush StatusBrush => !_model.IsReachable || PacketLossPercent > 10.0 ? RedBrush
        : PacketLossPercent > 2.0 || _model.IsLatencyElevated ? AmberBrush
        : GreenBrush;

    public List<double> ChartValues
    {
        get => _chartValues;
        private set => SetProperty(ref _chartValues, value);
    }

    public void UpdateFromModel(TargetMetrics model)
    {
        _model = model;
        lock (model)
        {
            ChartValues = model.RecentHistory
                .Where(m => m.Success)
                .Select(m => m.RttMs)
                .TakeLast(40)
                .ToList();
        }

        OnPropertyChanged(nameof(CurrentLatency));
        OnPropertyChanged(nameof(RollingAvg));
        OnPropertyChanged(nameof(MinLatency));
        OnPropertyChanged(nameof(MaxLatency));
        OnPropertyChanged(nameof(MedianLatency));
        OnPropertyChanged(nameof(P95Latency));
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(PacketLossPercent));
        OnPropertyChanged(nameof(TotalProbes));
        OnPropertyChanged(nameof(IsReachable));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HttpsBreakdown));
        OnPropertyChanged(nameof(LatencyDisplay));
        OnPropertyChanged(nameof(AvgDisplay));
        OnPropertyChanged(nameof(JitterDisplay));
        OnPropertyChanged(nameof(LossDisplay));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(P95Display));
        OnPropertyChanged(nameof(BaselineDisplay));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HttpsTimingDisplay));
    }
}
