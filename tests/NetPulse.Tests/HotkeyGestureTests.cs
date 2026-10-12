using NetPulse.Core.Models;

namespace NetPulse.Tests;

public class HotkeyGestureTests
{
    [Fact]
    public void EveryPreset_Parses()
    {
        foreach (var preset in HotkeyGesture.Presets)
            Assert.True(HotkeyGesture.TryParse(preset, out _), preset);
    }

    [Fact]
    public void Off_IsValidButHasNoGesture()
    {
        Assert.True(HotkeyGesture.TryParse("off", out var g));
        Assert.Null(g);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F9", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x78, "Ctrl+Shift+F9")]
    [InlineData("alt + ctrl + l", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'L', "Ctrl+Alt+L")]
    [InlineData("Ctrl+Shift+Pause", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x13, "Ctrl+Shift+Pause")]
    [InlineData("Win+F12", HotkeyModifiers.Win, 0x7B, "Win+F12")]
    public void Parses_ModifiersKeyAndCanonicalDisplay(string text, HotkeyModifiers mods, int vk, string display)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g!.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
        Assert.Equal(display, g.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F9")]                 // no modifier: would steal the key from every program
    [InlineData("Shift+A")]            // would block typing capital A
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Hyper+A")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Enter")]
    public void Rejects_UnsafeOrUnknownCombinations(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Settings_RestoreTheDefaultShortcutWhenTheStoredOneIsInvalid()
    {
        var s = AppSettings.CreateDefault();
        s.MarkLagHotkey = "Shift+Q";
        s.Normalize();
        Assert.Equal("Ctrl+Shift+F9", s.MarkLagHotkey);
    }

    [Fact]
    public void SettingsDraft_CarriesTheNewSettingsBothWays_AndValidatesTheShortcut()
    {
        var s = AppSettings.CreateDefault();
        var d = SettingsDraft.FromSettings(s, false);
        d.MarkLagHotkey = "Ctrl+Alt+M";
        d.MarkLagSound = false;
        d.RecordingRetentionDays = 90;
        Assert.Empty(d.Validate());
        d.ApplyTo(s);
        Assert.Equal("Ctrl+Alt+M", s.MarkLagHotkey);
        Assert.False(s.MarkLagSound);
        Assert.Equal(90, s.RecordingRetentionDays);

        d.MarkLagHotkey = "nonsense";
        Assert.Contains(d.Validate(), e => e.Contains("shortcut"));
    }
}
