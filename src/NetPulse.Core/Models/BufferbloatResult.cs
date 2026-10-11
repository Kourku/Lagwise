using System;

namespace NetPulse.Core.Models;

public class BufferbloatResult
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double BaselineLatencyMs { get; set; }
    public double DownloadLoadedLatencyMs { get; set; }
    public double DownloadDeltaMs { get; set; }
    public double UploadLoadedLatencyMs { get; set; }
    public double UploadDeltaMs { get; set; }

    public string Grade { get; set; } = "A"; // A+, A, B, C, D, F
    public string Summary { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;

    /// <summary>False when the test could not produce a trustworthy grade (no baseline, or load never flowed).</summary>
    public bool IsValid { get; set; } = true;
    /// <summary>External service that generated the load, and the ICMP reference target pinged (not a game server).</summary>
    public string? Provider { get; set; }
    public string? ReferenceTarget { get; set; }
    public string? ErrorMessage { get; set; }
    public long BytesDownloadedDuringLoad { get; set; }
    public long BytesUploadedDuringLoad { get; set; }
    public int BaselineSampleCount { get; set; }
    public int DownloadSampleCount { get; set; }
    public int UploadSampleCount { get; set; }

    public static string CalculateGrade(double maxDelta)
    {
        return maxDelta switch
        {
            <= 5 => "A+",
            <= 15 => "A",
            <= 30 => "B",
            <= 60 => "C",
            <= 150 => "D",
            _ => "F"
        };
    }
}

public class SpeedTestResult
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double DownloadSpeedMbps { get; set; }
    public double UploadSpeedMbps { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesUploaded { get; set; }
    public double DurationSeconds { get; set; }
    public bool CompletedSuccessfully { get; set; }
    /// <summary>External service that generated the test traffic.</summary>
    public string? Provider { get; set; }
    public string? ErrorMessage { get; set; }
}
