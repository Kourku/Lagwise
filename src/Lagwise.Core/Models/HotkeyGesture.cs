using System;
using System.Collections.Generic;
using System.Linq;

namespace Lagwise.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// A system-wide keyboard shortcut such as "Ctrl+Shift+F9", parsed into the modifier flags and virtual-key code that
/// Windows' RegisterHotKey expects. Pure parsing, no Win32 calls, so it is testable anywhere.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey, string Display)
{
    public const string Off = "Off";

    /// <summary>The shortcuts offered in Settings. All use at least two modifiers or a function key, to avoid clashing with game controls.</summary>
    public static IReadOnlyList<string> Presets { get; } = new[] { "Ctrl+Shift+F9", "Ctrl+Shift+F10", "Ctrl+Alt+L", "Ctrl+Alt+M", "Ctrl+Shift+Pause", Off };

    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (string.Equals(text.Trim(), Off, StringComparison.OrdinalIgnoreCase)) return true; // valid, but no gesture

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        var mods = HotkeyModifiers.None;
        foreach (var p in parts[..^1])
        {
            var m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => (HotkeyModifiers?)null
            };
            if (m == null || mods.HasFlag(m.Value)) return false;
            mods |= m.Value;
        }

        int? vk = KeyCode(parts[^1]);
        if (vk == null) return false;

        // a bare Shift+letter would type a capital letter in every other app
        if (mods == HotkeyModifiers.Shift && vk is >= 0x30 and <= 0x5A) return false;

        string display = string.Join("+", new[] { (HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win") }
            .Where(x => mods.HasFlag(x.Item1)).Select(x => x.Item2).Append(Normalize(parts[^1])));
        gesture = new HotkeyGesture(mods, vk.Value, display);
        return true;
    }

    private static string Normalize(string key) => key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();

    private static int? KeyCode(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return null;
        }

        if ((key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24) return 0x70 + f - 1;

        return key.ToLowerInvariant() switch
        {
            "pause" => 0x13,
            "scrolllock" or "scroll" => 0x91,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            _ => null
        };
    }
}
