using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Lagwise.Core;

/// <summary>
/// Keeps Lagwise to one running copy per data folder. A second start hands its request (show the window, mark lag,
/// a taskbar jump-list task, or "exit" from the installer) to the running copy and closes, so two copies never
/// measure twice or write the same files. Requests travel over a named pipe that only this Windows user can open, only
/// from this PC, and only the fixed words in <see cref="Commands"/> are accepted.
/// </summary>
public static class AppInstance
{
    public const string Show = "show";
    public const string MarkLag = "mark-lag";
    public const string Record = "record";
    public const string Overlay = "overlay";
    public const string SpeedTest = "speedtest";
    public const string Bufferbloat = "bufferbloat";
    public const string History = "history";
    public const string Exit = "exit";
    /// <summary>Started by Windows at sign-in: when Lagwise already runs, there is nothing to do.</summary>
    public const string None = "none";

    /// <summary>Every request a second start (or the installer) can hand over. Anything else is ignored.</summary>
    public static readonly IReadOnlyCollection<string> Commands = new HashSet<string>(StringComparer.Ordinal)
        { Show, MarkLag, Record, Overlay, SpeedTest, Bufferbloat, History, Exit, None };

    private const int MaxRequestBytes = 64;

    /// <summary>The name shared by the running copy's mutex and pipe: one per data folder, so a test copy with its own folder runs alongside.</summary>
    public static string KeyFor(string dataDirectory)
    {
        string full = Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return "Lagwise-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// The request in a command line: <c>--command name</c> (the taskbar jump list), nothing at all when Windows starts
    /// Lagwise at sign-in (<c>--minimized</c>), otherwise "show".
    /// </summary>
    public static string CommandFromArgs(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--command", StringComparison.OrdinalIgnoreCase)) continue;
            string? value = i + 1 < args.Count ? args[i + 1].Trim().ToLowerInvariant() : null;
            return value != null && Commands.Contains(value) ? value : Show;
        }
        foreach (var a in args)
            if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) return None;
        return Show;
    }

    /// <summary>Hands <paramref name="command"/> to the running copy. False when no copy answered within <paramref name="timeout"/>.</summary>
    public static bool TrySend(string key, string command, TimeSpan timeout)
    {
        try
        {
            // CurrentUserOnly: connects only to a pipe this same Windows user created
            using var client = new NamedPipeClientStream(".", key, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Receives requests from later starts until <paramref name="ct"/> is cancelled. <paramref name="received"/> runs on
    /// a background thread with a word from <see cref="Commands"/>; anything else is dropped.
    /// </summary>
    public static async Task ListenAsync(string key, Action<string> received, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer(key);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                string? command = await ReadRequestAsync(server, ct).ConfigureAwait(false);
                if (command != null) received(command);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a client that went away mid-request, or the name briefly in use: listen again shortly
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    // ---- the pipe itself: this user only, and never from another computer

    private const uint PipeAccessInbound = 0x1, FileFlagOverlapped = 0x40000000, FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeRejectRemoteClients = 0x8;   // byte mode, blocking: the other flags are 0

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSddl(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// A pipe owned by, and open only to, the signed-in Windows user, which Windows refuses to clients on other computers
    /// (PIPE_REJECT_REMOTE_CLIENTS). .NET's own pipe would accept them through file sharing, if only for this same user.
    /// </summary>
    private static NamedPipeServerStream CreateServer(string key)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                     ?? throw new UnauthorizedAccessException("The signed-in user could not be identified.");
        // owner: this user; access: this user only, nothing inherited
        if (!ConvertSddl($"O:{sid}D:P(A;;GA;;;{sid})", 1, out IntPtr descriptor, out _))
            throw new IOException("The pipe's access list could not be built.", Marshal.GetHRForLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + key, PipeAccessInbound | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients, 1, 0, MaxRequestBytes, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"The pipe could not be created (error {error}).", error);
            }
            return new NamedPipeServerStream(PipeDirection.In, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static async Task<string?> ReadRequestAsync(Stream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));   // a client that connects and says nothing must not block the next one
        var buffer = new byte[MaxRequestBytes];
        int length = 0;
        try
        {
            while (length < buffer.Length)
            {
                int n = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                length += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        return Parse(Encoding.UTF8.GetString(buffer, 0, length));
    }

    /// <summary>The request word in what a later start sent, or null when it is not one of <see cref="Commands"/>.</summary>
    public static string? Parse(string text)
    {
        int end = text.IndexOf('\n');
        string word = (end >= 0 ? text[..end] : text).Trim().ToLowerInvariant();
        return Commands.Contains(word) ? word : null;
    }
}
