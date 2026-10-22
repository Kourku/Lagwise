using System;
using System.Runtime.InteropServices;

namespace Lagwise.Core.Services;

/// <summary>
/// How busy the processor is, from Windows' own idle/kernel/user time counters (<c>GetSystemTimes</c>), as Task Manager
/// shows it: percent of all cores. No administrator rights, nothing installed. A PC working flat out stutters in a way
/// players take for lag, so recordings keep it beside the network measurements.
/// </summary>
public sealed class ProcessorLoad
{
    private long _idle, _kernel, _user;
    private bool _primed;

    /// <summary>Load since the previous call, or null on the first call (and where Windows does not answer).</summary>
    public double? Sample()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out long idle, out long kernel, out long user)) return null;
        bool primed = _primed;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        (_idle, _kernel, _user, _primed) = (idle, kernel, user, true);
        return primed ? Percent(di, dk, du) : null;
    }

    /// <summary>Busy share of the time that passed: kernel time includes idle time, so busy = kernel + user - idle.</summary>
    public static double? Percent(long idleDelta, long kernelDelta, long userDelta)
    {
        long total = kernelDelta + userDelta;
        if (total <= 0 || idleDelta < 0) return null;
        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
