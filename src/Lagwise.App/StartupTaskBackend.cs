using Lagwise.Core.Services;
using WinStartupTask = Windows.ApplicationModel.StartupTask;
using StartupTaskState = Lagwise.Core.Services.StartupTaskState;

namespace Lagwise.App;

/// <summary>
/// The Microsoft Store edition's "start with Windows": the startup task declared in its package manifest, switched on and
/// off through the Windows Runtime. (The portable and installed editions use the Run registry key instead.)
/// </summary>
internal sealed class StartupTaskBackend : IStartupTaskBackend
{
    /// <summary>Must match the TaskId of the windows.startupTask extension in the package manifest.</summary>
    public const string TaskId = "LagwiseStartup";

    public StartupTaskState GetState() => Run(async () => Map((await WinStartupTask.GetAsync(TaskId)).State));

    public StartupTaskState Enable() => Run(async () => Map(await (await WinStartupTask.GetAsync(TaskId)).RequestEnableAsync()));

    public void Disable() => Run(async () => { (await WinStartupTask.GetAsync(TaskId)).Disable(); return 0; });

    /// <summary>
    /// True when Windows started this Lagwise because of the startup task, which cannot carry the --minimized argument.
    /// </summary>
    public static bool StartedAtSignIn()
    {
        if (!Lagwise.Core.PackageContext.IsPackaged) return false;
        try
        {
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // on a pool thread so the window's thread is never blocked waiting for Windows
    private static T Run<T>(Func<Task<T>> work) where T : struct
    {
        try { return Task.Run(work).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    private static StartupTaskState Map(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => StartupTaskState.Enabled,
        Windows.ApplicationModel.StartupTaskState.Disabled => StartupTaskState.Disabled,
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => StartupTaskState.DisabledByUser,
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => StartupTaskState.DisabledByPolicy,
        _ => StartupTaskState.Unavailable
    };
}
