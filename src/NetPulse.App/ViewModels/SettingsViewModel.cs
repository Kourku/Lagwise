using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using NetPulse.Core.Models;
using NetPulse.Core.Services;

namespace NetPulse.App.ViewModels;

public sealed record Choice(string Label, int Value);

public sealed class TargetEditRow : ViewModelBase
{
    private readonly Action _changed;
    private bool _enabled;

    public TargetEditRow(TargetDraft draft, Action changed)
    {
        Draft = draft;
        _enabled = draft.Enabled;
        _changed = changed;
    }

    public TargetDraft Draft { get; }
    public string Name => Draft.IsGateway ? "Your router (found automatically)" : Draft.Name;
    public string Host => Draft.IsGateway ? "your default gateway" : Draft.Host;
    public string Method => Draft.MethodLabel;
    public string Kind => Draft.IsGateway ? "Router" : Draft.IsDnsResolver ? "DNS lookup" : Draft.IsBuiltIn ? "Built-in reference" : "Added by you";
    public bool CanToggle => !Draft.IsGateway;
    public bool CanRemove => !Draft.IsBuiltIn;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            Draft.Enabled = value;
            _changed();
        }
    }
}

/// <summary>Edits a <see cref="SettingsDraft"/>. Nothing reaches the running engine until Save passes validation.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private SettingsDraft _draft;
    private bool _loading;
    private bool _isDirty;
    private string _statusText = "";
    private bool _statusIsError;
    private string _latencyText = "";
    private string _lossText = "";

    private int _probeInterval, _recentWindow, _historyMinutes, _dedupSeconds;
    private bool _spikeOn, _lossOn, _minimizeToTray, _closeToTray, _startWithWindows, _lagSound;
    private string _hotkey = "Ctrl+Shift+F9";
    private int _retentionDays;

    private string _newName = "", _newHost = "", _newPort = "443";
    private int _newMethod;
    private string _addError = "";

    public event EventHandler? Applied;

    public SettingsViewModel(NetPulseHost host)
    {
        _host = host;
        _draft = SettingsDraft.FromSettings(host.Settings, host.WindowsIntegration?.IsStartWithWindowsEnabled() ?? false);

        SaveCommand = new RelayCommand(Save, () => IsDirty);
        RevertCommand = new RelayCommand(Revert, () => IsDirty);
        DefaultsCommand = new RelayCommand(LoadDefaults);
        AddTargetCommand = new RelayCommand(AddTarget);
        RemoveTargetCommand = new RelayCommand(p => RemoveTarget(p as TargetEditRow));
        ResetTargetsCommand = new RelayCommand(ResetTargets);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        Load(_draft);
    }

    public IReadOnlyList<Choice> ProbeIntervals { get; } = new Choice[]
    {
        new("Every 0.5 s - most detail, most traffic", 500), new("Every 1 s - recommended", 1000), new("Every 2 s", 2000), new("Every 5 s - lightest", 5000)
    };
    public IReadOnlyList<Choice> RecentWindows { get; } = new Choice[]
    {
        new("30 seconds - reacts fastest", 30), new("1 minute - recommended", 60), new("2 minutes", 120), new("5 minutes - smoothest", 300)
    };
    public IReadOnlyList<Choice> HistoryWindows { get; } = new Choice[]
    {
        new("5 minutes", 5), new("15 minutes - recommended", 15), new("30 minutes", 30), new("1 hour", 60)
    };
    public IReadOnlyList<Choice> DedupChoices { get; } = new Choice[]
    {
        new("Show every occurrence", 0), new("Within 15 s", 15), new("Within 30 s - recommended", 30),
        new("Within 1 minute", 60), new("Within 2 minutes", 120)
    };
    public IReadOnlyList<Choice> RetentionChoices { get; } = new Choice[]
    {
        new("Keep forever", 0), new("Delete after 30 days", 30), new("Delete after 90 days", 90), new("Delete after 1 year", 365)
    };
    public IReadOnlyList<string> HotkeyChoices { get; } = HotkeyGesture.Presets;
    public IReadOnlyList<string> NewMethods { get; } = new[] { "Ping (ICMP)", "TCP connect", "HTTPS handshake" };

    public ObservableCollection<TargetEditRow> Targets { get; } = new();

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand DefaultsCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand ResetTargetsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public string DataFolder => _host.DataDirectory;
    public string SettingsFile => _host.SettingsService.FilePath;
    public string ProviderName => _host.TransferProvider.DisplayName;
    public string ProviderDisclosure => _host.TransferProvider.Disclosure;

    public int ProbeIntervalMs { get => _probeInterval; set => Set(ref _probeInterval, value); }
    public int RecentWindowSeconds { get => _recentWindow; set => Set(ref _recentWindow, value); }
    public int HistoryWindowMinutes { get => _historyMinutes; set => Set(ref _historyMinutes, value); }
    public int EventDedupSeconds { get => _dedupSeconds; set => Set(ref _dedupSeconds, value); }
    public bool NotifyOnLatencySpike { get => _spikeOn; set => Set(ref _spikeOn, value); }
    public bool NotifyOnPacketLoss { get => _lossOn; set => Set(ref _lossOn, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public string MarkLagHotkey { get => _hotkey; set => Set(ref _hotkey, value); }
    public bool MarkLagSound { get => _lagSound; set => Set(ref _lagSound, value); }
    public bool DesktopAlerts { get => _alerts; set => Set(ref _alerts, value); }
    public bool AlertsOnlyInBackground { get => _alertsBg; set => Set(ref _alertsBg, value); }
    private bool _alerts, _alertsBg;
    public int RecordingRetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }
    /// <summary>Set by the window when Windows refuses the shortcut (another program already uses it).</summary>
    public string HotkeyStatus { get => _hotkeyStatus; set => SetProperty(ref _hotkeyStatus, value); }
    private string _hotkeyStatus = "";
    public string LatencySpikeText { get => _latencyText; set => Set(ref _latencyText, value); }
    public string PacketLossText { get => _lossText; set => Set(ref _lossText, value); }

    public string NewName { get => _newName; set { SetProperty(ref _newName, value); AddError = ""; } }
    public string NewHost { get => _newHost; set { SetProperty(ref _newHost, value); AddError = ""; } }
    public string NewPort { get => _newPort; set { SetProperty(ref _newPort, value); AddError = ""; } }
    public int NewMethodIndex { get => _newMethod; set { if (SetProperty(ref _newMethod, value)) { OnPropertyChanged(nameof(ShowPort)); AddError = ""; } } }
    public bool ShowPort => NewMethodIndex != 0;
    public string AddError { get => _addError; private set { if (SetProperty(ref _addError, value)) OnPropertyChanged(nameof(HasAddError)); } }
    public bool HasAddError => !string.IsNullOrEmpty(_addError);

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (SetProperty(ref _isDirty, value)) { CommandManager.InvalidateRequerySuggested(); OnPropertyChanged(nameof(ShowSaveBar)); } }
    }

    public string StatusText { get => _statusText; private set { if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(ShowSaveBar)); } }

    /// <summary>
    /// The Save / Discard bar only appears while something is waiting to be saved, and briefly afterwards to confirm.
    /// Appearance and Detail level apply instantly and never need it; a permanently visible, greyed-out bar looked broken.
    /// </summary>
    public bool ShowSaveBar => IsDirty || _statusText.Length > 0;
    private System.Windows.Threading.DispatcherTimer? _statusTimer;

    /// <summary>Clears a confirmation after a few seconds, which also hides the bar again. Errors stay until the next change.</summary>
    private void ClearStatusSoon()
    {
        _statusTimer?.Stop();
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => { _statusTimer?.Stop(); if (!IsDirty && !StatusIsError) StatusText = ""; };
        _statusTimer.Start();
    }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public string DirtyText => IsDirty ? "You have unsaved changes." : "All changes are saved.";

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
        StatusText = "";
        OnPropertyChanged(nameof(DirtyText));
    }

    // ------------------------------------------------------------------ load / save

    private void Load(SettingsDraft d)
    {
        _loading = true;
        try
        {
            _draft = d;
            ProbeIntervalMs = Nearest(ProbeIntervals, d.ProbeIntervalMs);
            RecentWindowSeconds = Nearest(RecentWindows, d.RecentWindowSeconds);
            HistoryWindowMinutes = Nearest(HistoryWindows, d.HistoryWindowMinutes);
            EventDedupSeconds = Nearest(DedupChoices, d.EventDedupSeconds);
            NotifyOnLatencySpike = d.NotifyOnLatencySpike;
            NotifyOnPacketLoss = d.NotifyOnPacketLoss;
            MinimizeToTray = d.MinimizeToTray;
            CloseToTray = d.CloseToTray;
            StartWithWindows = d.StartWithWindows;
            MarkLagHotkey = HotkeyChoices.Contains(d.MarkLagHotkey) ? d.MarkLagHotkey : HotkeyChoices[0];
            MarkLagSound = d.MarkLagSound;
            DesktopAlerts = d.DesktopAlerts;
            AlertsOnlyInBackground = d.AlertsOnlyInBackground;
            RecordingRetentionDays = Nearest(RetentionChoices, d.RecordingRetentionDays);
            LatencySpikeText = d.LatencySpikeThresholdMs.ToString("0.#", CultureInfo.CurrentCulture);
            PacketLossText = d.PacketLossThresholdPercent.ToString("0.#", CultureInfo.CurrentCulture);

            Targets.Clear();
            foreach (var t in d.Targets) Targets.Add(new TargetEditRow(t, MarkDirty));
        }
        finally
        {
            _loading = false;
        }
        IsDirty = false;
        OnPropertyChanged(nameof(DirtyText));
    }

    private static int Nearest(IReadOnlyList<Choice> choices, int value) =>
        choices.OrderBy(c => Math.Abs(c.Value - value)).First().Value;

    private void Revert()
    {
        Load(SettingsDraft.FromSettings(_host.Settings, _host.WindowsIntegration?.IsStartWithWindowsEnabled() ?? false));
        StatusIsError = false;
        StatusText = "Changes discarded.";
        ClearStatusSoon();
    }

    private void LoadDefaults()
    {
        var defaults = SettingsDraft.FromSettings(AppSettings.CreateDefault(), _startWithWindows);
        Load(defaults);
        MarkDirty();
        StatusIsError = false;
        StatusText = "Default settings loaded. Press Save to keep them.";
    }

    private void Save()
    {
        var errors = new List<string>();
        if (!TryParse(LatencySpikeText, out double spike)) errors.Add("The latency spike limit must be a number, for example 100.");
        if (!TryParse(PacketLossText, out double loss)) errors.Add("The packet loss limit must be a number, for example 5.");

        _draft.ProbeIntervalMs = ProbeIntervalMs;
        _draft.RecentWindowSeconds = RecentWindowSeconds;
        _draft.HistoryWindowMinutes = HistoryWindowMinutes;
        _draft.EventDedupSeconds = EventDedupSeconds;
        _draft.NotifyOnLatencySpike = NotifyOnLatencySpike;
        _draft.NotifyOnPacketLoss = NotifyOnPacketLoss;
        _draft.MinimizeToTray = MinimizeToTray;
        _draft.CloseToTray = CloseToTray;
        _draft.StartWithWindows = StartWithWindows;
        _draft.MarkLagHotkey = MarkLagHotkey;
        _draft.MarkLagSound = MarkLagSound;
        _draft.DesktopAlerts = DesktopAlerts;
        _draft.AlertsOnlyInBackground = AlertsOnlyInBackground;
        _draft.RecordingRetentionDays = RecordingRetentionDays;
        _draft.LatencySpikeThresholdMs = spike;
        _draft.PacketLossThresholdPercent = loss;
        _draft.Targets = Targets.Select(r => r.Draft).ToList();

        if (errors.Count == 0) errors.AddRange(_draft.Validate());
        if (errors.Count > 0)
        {
            StatusIsError = true;
            StatusText = string.Join("  ", errors);
            return;
        }

        // Start with Windows touches the registry, so do it first and keep the toggle honest if Windows refuses.
        string? startupNote = null;
        if (_host.WindowsIntegration != null && _host.WindowsIntegration.IsStartWithWindowsEnabled() != _draft.StartWithWindows)
        {
            if (!_host.WindowsIntegration.SetStartWithWindows(_draft.StartWithWindows))
            {
                _draft.StartWithWindows = !_draft.StartWithWindows;
                _loading = true; StartWithWindows = _draft.StartWithWindows; _loading = false;
                startupNote = " Windows would not change the start-up setting, so it was left as it was.";
            }
        }

        _draft.ApplyTo(_host.Settings);
        _host.Engine.SyncTargets();
        _host.ApplyRetention();

        if (!_host.SaveSettings(out var error))
        {
            StatusIsError = true;
            StatusText = $"The settings are active but could not be written to disk: {error}";
            return;
        }

        IsDirty = false;
        OnPropertyChanged(nameof(DirtyText));
        StatusIsError = startupNote != null;
        StatusText = "Settings saved. They are already in effect." + startupNote;
        ClearStatusSoon();
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ------------------------------------------------------------------ targets

    private void AddTarget()
    {
        var protocol = NewMethodIndex switch { 1 => NetworkProtocol.Tcp, 2 => NetworkProtocol.Https, _ => NetworkProtocol.Icmp };
        var error = TargetValidator.Validate(NewName, NewHost, protocol, NewPort, Targets.Select(t => t.Draft), out int port);
        if (error != null)
        {
            AddError = error;
            return;
        }

        var draft = new TargetDraft { Name = NewName.Trim(), Host = NewHost.Trim(), Protocol = protocol, Port = port, Enabled = true };
        Targets.Add(new TargetEditRow(draft, MarkDirty));
        NewName = NewHost = "";
        NewPort = "443";
        AddError = "";
        MarkDirty();
    }

    private void RemoveTarget(TargetEditRow? row)
    {
        if (row == null || !row.CanRemove) return;
        Targets.Remove(row);
        MarkDirty();
    }

    private void ResetTargets()
    {
        Targets.Clear();
        foreach (var t in SettingsDraft.DefaultTargets()) Targets.Add(new TargetEditRow(t, MarkDirty));
        MarkDirty();
        StatusIsError = false;
        StatusText = "Default targets restored. Press Save to keep them.";
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            Process.Start(new ProcessStartInfo(_host.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusText = $"Could not open the folder: {ex.Message}";
        }
    }
}
