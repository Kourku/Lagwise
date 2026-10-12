using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NetPulse.Core.Models;

namespace NetPulse.App;

/// <summary>
/// One system-wide keyboard shortcut (Windows RegisterHotKey) bound to a window, so the player can mark lag
/// without leaving the game. Windows refuses a combination another program already owns; that is reported, not hidden.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int Id = 0x4E50; // "NP"

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;

    public GlobalHotkey(HwndSource source, Action pressed)
    {
        _source = source;
        _pressed = pressed;
        _source.AddHook(Hook);
    }

    /// <summary>The gesture currently registered, or null.</summary>
    public HotkeyGesture? Current { get; private set; }

    /// <summary>Registers <paramref name="text"/> (replacing any previous shortcut). Returns null on success, otherwise why it failed.</summary>
    public string? Set(string? text)
    {
        Clear();
        if (!HotkeyGesture.TryParse(text, out var gesture)) return $"\"{text}\" is not a valid shortcut.";
        if (gesture == null) return null; // "Off"

        uint mods = ModNoRepeat;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= 0x1;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= 0x2;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= 0x4;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= 0x8;

        if (!RegisterHotKey(_source.Handle, Id, mods, (uint)gesture.VirtualKey))
            return $"{gesture.Display} is already used by another program, so it could not be set. Choose another shortcut in Settings.";

        _registered = true;
        Current = gesture;
        return null;
    }

    private void Clear()
    {
        if (_registered) UnregisterHotKey(_source.Handle, Id);
        _registered = false;
        Current = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(Hook);
    }
}
