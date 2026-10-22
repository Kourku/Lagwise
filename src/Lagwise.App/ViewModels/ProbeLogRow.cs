using Lagwise.Core.Models;

namespace Lagwise.App.ViewModels;

/// <summary>One raw measurement as shown in the Advanced probe log.</summary>
public sealed class ProbeLogRow
{
    public ProbeLogRow(Measurement m)
    {
        Time = m.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        Target = m.TargetName;
        Address = string.IsNullOrEmpty(m.ResolvedIp) ? m.TargetHost : m.ResolvedIp;
        Method = m.Protocol.ToString().ToUpperInvariant();
        Ok = m.Success;
        Result = m.Success ? $"{m.RttMs:F1} ms" : "no reply";
        Detail = !m.Success ? m.ErrorMessage ?? ""
            : m.HttpsBreakdown is { } b ? b.ShortText
            : "";
    }

    public string Time { get; }
    public string Target { get; }
    public string Address { get; }
    public string Method { get; }
    public bool Ok { get; }
    public string Result { get; }
    public string Detail { get; }
}
