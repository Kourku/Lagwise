using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using NetPulse.App.Themes;
using NetPulse.Core.Services;

namespace NetPulse.App.ViewModels;

public sealed class ThemeOption : ViewModelBase
{
    private bool _isSelected;
    public ThemeOption(string id, string label, string description, string glyph) { Id = id; Label = label; Description = description; Glyph = glyph; }
    public string Id { get; }
    public string Label { get; }
    public string Description { get; }
    public string Glyph { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class AccentOption : ViewModelBase
{
    private bool _isSelected;
    private Brush _swatch;
    public AccentOption(AccentChoice choice) { Choice = choice; _swatch = Make(); }
    public AccentChoice Choice { get; }
    public string Name => Choice.Name;
    public Brush Swatch { get => _swatch; private set => SetProperty(ref _swatch, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public void RefreshSwatch() => Swatch = Make();
    private Brush Make() { var b = new SolidColorBrush(ThemeManager.AccentPreview(Choice)); b.Freeze(); return b; }
}

/// <summary>
/// Theme, accent colour and interface size. Unlike the rest of Settings these apply (and are saved) the moment they
/// are chosen, so the user sees the result straight away.
/// </summary>
public sealed class AppearanceViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;

    /// <summary>Raised after any appearance change has been applied (the window updates its scale and frame).</summary>
    public event EventHandler? Applied;

    public AppearanceViewModel(NetPulseHost host)
    {
        _host = host;
        Themes = new[]
        {
            new ThemeOption("Dark", "Dark", "Easy on the eyes at night and next to a game", ""),
            new ThemeOption("Light", "Light", "Bright and crisp in a lit room", ""),
            new ThemeOption("System", "Match Windows", "Follows your Windows app colour setting", ""),
        };
        Accents = ThemeManager.Accents.Select(a => new AccentOption(a)).ToList();
        SelectThemeCommand = new RelayCommand(p => { if (p is ThemeOption t) Theme = t.Id; });
        SelectAccentCommand = new RelayCommand(p => { if (p is AccentOption a) Accent = a.Name; });
        ToggleThemeCommand = new RelayCommand(() => Theme = ThemeManager.IsLight ? "Dark" : "Light");
        Sync();
    }

    public IReadOnlyList<ThemeOption> Themes { get; }
    public IReadOnlyList<AccentOption> Accents { get; }
    public IReadOnlyList<Choice> Scales { get; } = new Choice[]
    {
        new("Compact (90%)", 90), new("Normal (100%)", 100), new("Large (110%)", 110), new("Larger (120%)", 120), new("Largest (130%)", 130)
    };

    public IReadOnlyList<Choice> OverlayOpacities { get; } = new Choice[]
    {
        new("Solid", 100), new("Slightly see-through (85%)", 85), new("See-through (70%)", 70), new("Very see-through (55%)", 55)
    };

    /// <summary>Background opacity of the mini overlay; text always stays fully opaque.</summary>
    public int OverlayOpacityPercent
    {
        get => _host.Settings.OverlayOpacityPercent;
        set
        {
            if (_host.Settings.OverlayOpacityPercent == value) return;
            _host.Settings.OverlayOpacityPercent = value;
            _host.SaveSettings(out _);
            OnPropertyChanged();
            Applied?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Show this PC's live download / upload in the mini overlay.</summary>
    public bool OverlayShowActivity
    {
        get => _host.Settings.OverlayShowActivity;
        set
        {
            if (_host.Settings.OverlayShowActivity == value) return;
            _host.Settings.OverlayShowActivity = value;
            _host.SaveSettings(out _);
            OnPropertyChanged();
            Applied?.Invoke(this, EventArgs.Empty);
        }
    }

    public RelayCommand SelectThemeCommand { get; }
    public RelayCommand SelectAccentCommand { get; }
    /// <summary>Title-bar button: flips between dark and light.</summary>
    public RelayCommand ToggleThemeCommand { get; }

    public string Theme
    {
        get => _host.Settings.Theme;
        set
        {
            if (_host.Settings.Theme == value) return;
            _host.Settings.Theme = value;
            ApplyAndSave();
        }
    }

    public string Accent
    {
        get => _host.Settings.Accent;
        set
        {
            if (_host.Settings.Accent == value) return;
            _host.Settings.Accent = value;
            ApplyAndSave();
        }
    }

    public int UiScalePercent
    {
        get => _host.Settings.UiScalePercent;
        set
        {
            if (_host.Settings.UiScalePercent == value) return;
            _host.Settings.UiScalePercent = Math.Clamp(value, 90, 130);
            ApplyAndSave();
        }
    }

    /// <summary>Glyph for the title-bar toggle: a moon in light mode (switch to dark), a sun in dark mode.</summary>
    public string ToggleGlyph => ThemeManager.IsLight ? "" : "";
    public string ToggleTip => ThemeManager.IsLight ? "Switch to the dark theme" : "Switch to the light theme";

    private void ApplyAndSave()
    {
        ThemeManager.Apply(_host.Settings.Theme, _host.Settings.Accent);
        _host.SaveSettings(out _);
        Sync();
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes selection marks and swatches (also after Windows changes its theme while following it).</summary>
    public void Sync()
    {
        foreach (var t in Themes) t.IsSelected = t.Id == _host.Settings.Theme;
        foreach (var a in Accents) { a.IsSelected = a.Name == ThemeManager.CurrentAccent; a.RefreshSwatch(); }
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(UiScalePercent));
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        CommandManager.InvalidateRequerySuggested();
    }
}
