namespace Lagwise.Core.Services;

/// <summary>What Windows says about the packaged app's "start at sign-in" task.</summary>
public enum StartupTaskState
{
    /// <summary>Not available here (not a packaged app, or Windows could not be asked).</summary>
    Unavailable,
    Disabled,
    Enabled,
    /// <summary>The user switched it off in Settings > Apps > Startup or Task Manager; only the user can switch it on again.</summary>
    DisabledByUser,
    /// <summary>An organisation's policy keeps it off.</summary>
    DisabledByPolicy
}

/// <summary>
/// The Microsoft Store edition cannot use the Run registry key (a packaged app's registry writes stay inside the package),
/// so it declares a startup task in its manifest and asks Windows to switch it on or off. The Windows-specific part lives
/// in the app; this is the seam, so it can be tested without a package.
/// </summary>
public interface IStartupTaskBackend
{
    StartupTaskState GetState();
    /// <summary>Asks Windows to switch the task on; returns the state afterwards.</summary>
    StartupTaskState Enable();
    void Disable();
}
