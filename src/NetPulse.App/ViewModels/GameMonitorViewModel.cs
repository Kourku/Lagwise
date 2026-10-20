using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.App.ViewModels;

/// <summary>An entry of the game picker: "All my games", or one game found on this PC.</summary>
public sealed class GameItem : ViewModelBase
{
    private ImageSource? _icon;

    public GameItem(GameProfile? profile, string subtitle, ImageSource? icon = null)
    {
        Profile = profile;
        Subtitle = subtitle;
        _icon = icon;
    }

    /// <summary>Null for "All my games".</summary>
    public GameProfile? Profile { get; }
    public string Id => Profile?.Id ?? GameMonitorService.AllGames;
    public bool IsAll => Profile == null;
    public string Name => Profile?.DisplayName ?? "All my games";
    public string Subtitle { get; }
    public string Monogram => Profile == null ? "" : GameIcons.MonogramOf(Profile.Id, Profile.DisplayName);
    public ImageSource? Icon { get => _icon; set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;
    public bool CanRemove => Profile != null;
    public override string ToString() => Name;
}

/// <summary>A saved game session in the "Recent sessions" list.</summary>
public sealed class RecentSessionItem
{
    public RecentSessionItem(SessionSummary s, ImageSource? icon)
    {
        Id = s.Id;
        Title = s.GameDisplayName ?? s.Name;
        var start = s.StartedUtc.ToLocalTime();
        var length = (s.EndedUtc ?? s.StartedUtc) - s.StartedUtc;
        When = $"{GameMonitorViewModel.Day(start)} {start:HH:mm}  ·  {VerdictViewModel.Duration(length)}" + (s.LagMarkerCount > 0 ? $"  ·  {s.LagMarkerCount} lag mark{(s.LagMarkerCount == 1 ? "" : "s")}" : "");
        Level = s.Verdict is VerdictLevel v ? VerdictViewModel.LevelOf(v) : "Unknown";
        Headline = s.VerdictHeadline ?? (s.IsComplete ? "" : "Interrupted recording");
        Icon = icon;
        Monogram = GameIcons.MonogramOf(s.GameId ?? "", Title);
    }

    public string Id { get; }
    public string Title { get; }
    public string When { get; }
    public string Level { get; }
    public string Headline { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public string Monogram { get; }
}

/// <summary>Presentation logic for the Game Monitor page. All state comes from the shared <see cref="GameMonitorService"/>.</summary>
public sealed class GameMonitorViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private readonly GameMonitorService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshQueued;
    private GameItem? _selectedGame;
    private GameMonitorSnapshot _snapshot = new();
    private bool _isMonitoring, _isVisible, _verdictBusy;
    private string _elapsed = "00:00:00", _actionMessage = "", _sessionMessage = "";
    private string _statusTitle = "Looking for your games...", _statusDetail = "", _statusLevel = "Info", _gamesFoundText = "";
    private string _routerPing = "-", _internetPing = "-", _jitter = "-", _loss = "-", _traffic = "-", _processor = "-";
    private string _routerLevel = "Unknown", _internetLevel = "Unknown", _jitterLevel = "Unknown", _lossLevel = "Unknown", _trafficLevel = "Unknown", _processorLevel = "Unknown";
    private string _routerCaption = "", _internetCaption = "", _trafficCaption = "", _processorCaption = "";
    private VerdictViewModel? _soFar, _lastVerdict;
    private string _lastVerdictTitle = "";
    private string? _lastSavedId;
    private DateTime _lastSoFar;

    /// <summary>Raised when the user asks to open a saved recording in History &amp; reports.</summary>
    public event EventHandler<string>? OpenRecordingRequested;

    public GameMonitorViewModel(NetPulseHost host, Dispatcher dispatcher)
    {
        _host = host;
        _monitor = host.GameMonitor;
        _dispatcher = dispatcher;

        StartCommand = new RelayCommand(Start, () => !IsMonitoring && SelectedGame != null);
        StopCommand = new RelayCommand(Stop, () => IsMonitoring);
        MarkLagCommand = new RelayCommand(MarkLag, () => IsMonitoring);
        AddGameCommand = new RelayCommand(AddGame, () => !IsMonitoring);
        RemoveGameCommand = new RelayCommand(RemoveGame, () => !IsMonitoring && SelectedGame?.CanRemove == true);
        RestoreGamesCommand = new RelayCommand(RestoreGames, () => !IsMonitoring && _host.Settings.IgnoredGames.Count > 0);
        RefreshGamesCommand = new RelayCommand(() => _ = RefreshGamesAsync(), () => !IsMonitoring);
        OpenStatusPageCommand = new RelayCommand(OpenStatusPage, () => StatusPageGame != null);
        OpenRecordingCommand = new RelayCommand(() => { if (_lastSavedId != null) OpenRecordingRequested?.Invoke(this, _lastSavedId); }, () => _lastSavedId != null && !IsMonitoring);
        OpenSessionCommand = new RelayCommand(p => { if (p is RecentSessionItem s) OpenRecordingRequested?.Invoke(this, s.Id); });
        OpenHistoryCommand = new RelayCommand(() => OpenRecordingRequested?.Invoke(this, ""));

        Games.Add(new GameItem(null, "Records whichever of your games starts"));
        _host.SessionSaved += (_, e) => _dispatcher.InvokeAsync(() => OnSessionSaved(e));
        _host.GamesChanged += (_, _) => _dispatcher.InvokeAsync(RebuildGames);
        _monitor.Updated += (_, _) => QueueRefresh();
        _host.Engine.MetricsUpdated += (_, _) => QueueRefresh();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed(), _dispatcher);
        _clock.Stop();

        // what was watched last time: all games unless one game was chosen
        string watch = string.IsNullOrEmpty(host.Settings.LastGameId) ? GameMonitorService.AllGames : host.Settings.LastGameId!;
        _monitor.Select(watch, out _);
        if (_monitor.Watching == null) _monitor.Select(GameMonitorService.AllGames, out _);
        RebuildGames();
        _ = RefreshGamesAsync();
        _ = LoadRecentAsync();
    }

    public ObservableCollection<GameItem> Games { get; } = new();
    public ObservableCollection<RecentSessionItem> RecentSessions { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand MarkLagCommand { get; }
    public RelayCommand AddGameCommand { get; }
    public RelayCommand RemoveGameCommand { get; }
    public RelayCommand RestoreGamesCommand { get; }
    public RelayCommand RefreshGamesCommand { get; }
    public RelayCommand OpenStatusPageCommand { get; }
    public RelayCommand OpenRecordingCommand { get; }
    public RelayCommand OpenSessionCommand { get; }
    /// <summary>Opens History &amp; reports (an empty recording id).</summary>
    public RelayCommand OpenHistoryCommand { get; }

    /// <summary>Set by the shell while the page is shown: the live tiles and the verdict so far are only worked out then.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value) || !value) return;
            QueueRefresh();
            _ = LoadRecentAsync();
        }
    }

    // ------------------------------------------------------------------ the game list

    public GameItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_selectedGame == value || value == null) return;
            if (IsMonitoring) { OnPropertyChanged(); return; }   // cannot switch while recording; the binding reverts
            if (!_monitor.Select(value.Id, out var message)) { ActionMessage = message; OnPropertyChanged(); return; }
            _selectedGame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusPageGame));
            OnPropertyChanged(nameof(HasStatusPage));
            OnPropertyChanged(nameof(StatusPageText));
            _host.Settings.LastGameId = value.Id;
            _host.SaveSettings(out _);
            ActionMessage = "";
            CommandManager.InvalidateRequerySuggested();
            QueueRefresh();
        }
    }

    public string GamesFoundText { get => _gamesFoundText; private set { if (SetProperty(ref _gamesFoundText, value)) OnPropertyChanged(nameof(HasIgnoredGames)); } }
    /// <summary>Some games were taken off the list (they can be brought back).</summary>
    public bool HasIgnoredGames => _host.Settings.IgnoredGames.Count > 0;

    private async Task RefreshGamesAsync()
    {
        try
        {
            await Task.Run(() => _host.RefreshGames());   // raises GamesChanged, which rebuilds the list
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Finding games", ex);
        }
    }

    /// <summary>The picker: all games, then the games found on this PC, most recently played first.</summary>
    private void RebuildGames()
    {
        var library = _monitor.Library;
        var running = _snapshot.Running.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = library
            .Where(p => p.Source != GameSource.BuiltIn || p.ExecutablePath != null || running.Contains(p.Id) || p.Id == _monitor.Watching)
            .OrderByDescending(p => p.LastPlayedUtc ?? DateTime.MinValue).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        string watching = _monitor.Watching ?? GameMonitorService.AllGames;
        var keepAll = Games.FirstOrDefault(g => g.IsAll) ?? new GameItem(null, "Records whichever of your games starts");
        var items = new List<GameItem> { keepAll };
        foreach (var p in shown) items.Add(new GameItem(p, SubtitleFor(p), IconFor(p)));

        _selectedGame = null;
        Games.Clear();
        foreach (var i in items) Games.Add(i);
        _selectedGame = items.FirstOrDefault(i => string.Equals(i.Id, watching, StringComparison.OrdinalIgnoreCase)) ?? keepAll;
        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(StatusPageGame));
        OnPropertyChanged(nameof(HasStatusPage));
        OnPropertyChanged(nameof(StatusPageText));

        int found = shown.Count;
        GamesFoundText = found == 0
            ? "No games found yet. Windows lists a game once it has run on this PC; or add one yourself."
            : $"{found} game{(found == 1 ? "" : "s")} found on this PC" + (_host.Settings.IgnoredGames.Count > 0 ? $"; {_host.Settings.IgnoredGames.Count} taken off the list" : "") + ".";
        CommandManager.InvalidateRequerySuggested();
        _ = LoadIconsAsync(shown);
        QueueRefresh();
    }

    public static string Day(DateTime local)
    {
        var today = DateTime.Today;
        return local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString(local.Year == today.Year ? "ddd d MMM" : "d MMM yyyy");
    }

    private static string SubtitleFor(GameProfile p)
    {
        if (p.LastPlayedUtc is DateTime last)
        {
            var local = last.ToLocalTime();
            int days = (DateTime.Today - local.Date).Days;
            return days <= 0 ? "Played today" : days == 1 ? "Played yesterday" : days < 14 ? $"Played {days} days ago" : $"Last played {local:d MMM yyyy}";
        }
        return p.Source switch
        {
            GameSource.AddedByYou => "Added by you",
            GameSource.Windows => "Found by Windows",
            _ => "Known to NetPulse"
        };
    }

    private ImageSource? IconFor(GameProfile p)
    {
        if (_icons.TryGetValue(p.Id, out var cached)) return cached;
        var icon = GameIcons.Load(_host.DataDirectory, p.Id);
        if (icon != null) _icons[p.Id] = icon;
        return icon;
    }

    /// <summary>Takes the icon of each game's own program the first time, in the background, and keeps it.</summary>
    private async Task LoadIconsAsync(List<GameProfile> games)
    {
        foreach (var p in games.Where(p => p.ExecutablePath != null && !_icons.ContainsKey(p.Id)).ToList())
        {
            _icons[p.Id] = null;    // tried once per run
            var icon = await GameIcons.CaptureAsync(_host.DataDirectory, p.Id, p.ExecutablePath!);
            if (icon == null) continue;
            _icons[p.Id] = icon;
            foreach (var item in Games.Where(g => g.Profile?.Id == p.Id)) item.Icon = icon;
            NotifyCard();
        }
    }

    private void AddGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the game's program",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        if (!_host.AddGame(dlg.FileName, out var error)) { ActionMessage = error ?? "That program could not be added."; return; }
        string id = GameLibrary.UserPrefix + GameLibrary.StableId(dlg.FileName);
        ActionMessage = $"Added {GameLibrary.NameFor(dlg.FileName)}. NetPulse now watches for it.";
        _ = Task.Run(() =>
        {
            _host.RefreshGames();
            _dispatcher.InvokeAsync(() =>
            {
                // an exe Windows already lists is that game, not a second one
                var item = Games.FirstOrDefault(g => g.Id == id) ?? Games.FirstOrDefault(g => g.Profile?.ExecutablePath is { } p && GameProcessDetector.SamePath(p, dlg.FileName));
                if (item != null && SelectedGame?.IsAll != true) SelectedGame = item;
            });
        });
    }

    private void RemoveGame()
    {
        var game = SelectedGame?.Profile;
        if (game == null) return;
        if (!_host.RemoveGame(game, out var error)) { ActionMessage = error ?? "Could not change the list."; return; }
        ActionMessage = game.Source == GameSource.AddedByYou
            ? $"{game.DisplayName} was removed."
            : $"{game.DisplayName} is off the list: NetPulse no longer watches for it. \"Show all games again\" brings it back.";
        _monitor.Select(GameMonitorService.AllGames, out _);
        _host.Settings.LastGameId = GameMonitorService.AllGames;
        _host.SaveSettings(out _);
        _ = RefreshGamesAsync();
    }

    private void RestoreGames()
    {
        if (!_host.RestoreGames(out var error)) { ActionMessage = error ?? "Could not change the list."; return; }
        ActionMessage = "Every game is back on the list.";
        _ = RefreshGamesAsync();
    }

    // ------------------------------------------------------------------ recording

    /// <summary>Start recording when a game starts and save when it closes. Applied and saved immediately.</summary>
    public bool AutoRecord
    {
        get => _host.Settings.AutoRecordGame;
        set
        {
            if (_host.Settings.AutoRecordGame == value) return;
            _host.Settings.AutoRecordGame = value;
            _host.SaveSettings(out _);
            OnPropertyChanged();
            ActionMessage = value
                ? "Automatic recording is on: a game starting starts a recording, and it is saved once the game has been closed for 20 seconds."
                : "Automatic recording is off: press Record now when you play.";
            QueueRefresh();
        }
    }

    public string MarkLagShortcutText => _host.Settings.MarkLagHotkey == HotkeyGesture.Off
        ? "Tip: set a keyboard shortcut for Mark lag in Settings, so you can mark lag without leaving the game."
        : $"In game, press {_host.Settings.MarkLagHotkey} when it lags. It works while the game has focus.";
    public void NotifyShortcutChanged() => OnPropertyChanged(nameof(MarkLagShortcutText));

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set { if (SetProperty(ref _isMonitoring, value)) { OnPropertyChanged(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsIdle => !IsMonitoring;

    /// <summary>The game being recorded (or "a game" while the recording waits for one).</summary>
    public string RecordingGameName => _monitor.RecordedGame?.DisplayName ?? "a game";

    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string ActionMessage { get => _actionMessage; private set { if (SetProperty(ref _actionMessage, value)) OnPropertyChanged(nameof(HasActionMessage)); } }
    public bool HasActionMessage => _actionMessage.Length > 0;
    public string SessionMessage { get => _sessionMessage; private set { if (SetProperty(ref _sessionMessage, value)) OnPropertyChanged(nameof(HasSessionMessage)); } }
    public bool HasSessionMessage => _sessionMessage.Length > 0;

    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    /// <summary>For the status mark: "Good" while a game runs, "Busy" while recording, "Info" otherwise.</summary>
    public string StatusLevel { get => _statusLevel; private set => SetProperty(ref _statusLevel, value); }
    /// <summary>The game shown at the top of the page (recorded, running or chosen); null when watching all with none running.</summary>
    public GameProfile? CurrentGame => _snapshot.Game;
    public ImageSource? CurrentIcon => CurrentGame == null ? null : IconFor(CurrentGame);
    public bool HasCurrentIcon => CurrentIcon != null;
    public string CurrentMonogram => CurrentGame == null ? "" : GameIcons.MonogramOf(CurrentGame.Id, CurrentGame.DisplayName);
    public bool HasCurrentGame => CurrentGame != null;

    private void Start()
    {
        if (_monitor.Start(out var message))
        {
            IsMonitoring = true;
            _clock.Start();
            SessionMessage = "";
            SoFar = null;
            ActionMessage = _monitor.RecordedGame == null
                ? "Recording. Start your game: the recording is about the first of your games that starts."
                : $"Recording {_monitor.RecordedGame.DisplayName}.";
        }
        else
        {
            ActionMessage = message;
        }
        QueueRefresh();
    }

    private void Stop()
    {
        var session = _monitor.Stop(out _);
        IsMonitoring = false;
        _clock.Stop();
        if (session == null) SessionMessage = "Nothing was being recorded.";
        // otherwise OnSessionSaved reports the result (it also covers recordings stopped automatically)
        ActionMessage = "";
        QueueRefresh();
    }

    private void MarkLag()
    {
        ActionMessage = _monitor.MarkLag() ? $"Lag marked at {DateTime.Now:HH:mm:ss}." : "Start a recording first.";
    }

    private void OnSessionSaved(SessionSavedEventArgs e)
    {
        var session = e.Session;
        if (!session.IsGameRecording)
        {
            _ = LoadRecentAsync();
            return;    // a plain network recording, saved from History
        }
        SoFar = null;
        if (e.Error == null)
        {
            _lastSavedId = session.Id;
            SessionMessage = $"Recording saved: {session.Name}.";
        }
        else
        {
            _lastSavedId = null;
            SessionMessage = $"The session was recorded but could not be saved: {e.Error}";
        }
        _ = ShowLastVerdictAsync(session);
        _ = LoadRecentAsync();
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ verdicts

    /// <summary>How the recording in progress has gone so far (worked out every few seconds while the page is shown).</summary>
    public VerdictViewModel? SoFar { get => _soFar; private set { if (SetProperty(ref _soFar, value)) { OnPropertyChanged(nameof(HasSoFar)); OnPropertyChanged(nameof(CardDetail)); } } }
    public bool HasSoFar => _soFar != null;

    /// <summary>The verdict of the last game session (this run, or the newest one saved).</summary>
    public VerdictViewModel? LastVerdict { get => _lastVerdict; private set { if (SetProperty(ref _lastVerdict, value)) { OnPropertyChanged(nameof(HasLastVerdict)); OnPropertyChanged(nameof(CardDetail)); } } }
    public bool HasLastVerdict => _lastVerdict != null;
    public string LastVerdictTitle { get => _lastVerdictTitle; private set => SetProperty(ref _lastVerdictTitle, value); }

    private double BusyBps => _host.Settings.HeavyTrafficMbps > 0 ? _host.Settings.HeavyTrafficMbps * 1_000_000.0 : SessionAnalysis.DefaultBusyBps;

    private async Task ShowLastVerdictAsync(MonitoringSession s)
    {
        try
        {
            double busy = BusyBps;
            var vm = await Task.Run(() => new VerdictViewModel(SessionVerdict.Build(s, busy), SessionAnalysis.Compute(s, busy)));
            LastVerdict = vm;
            var start = s.StartedUtc.ToLocalTime();
            LastVerdictTitle = $"{s.GameDisplayName ?? "Game session"}  ·  {Day(start)} {start:HH:mm}";
            if (_lastSavedId == null) _lastSavedId = s.Id;
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorLog.Write("Session verdict", ex);
        }
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            string? recording = _host.Recorder.CurrentId;   // its checkpoint is on disk, but it is not finished
            var list = await Task.Run(() => _host.Sessions.List().Where(x => x.IsGameRecording && x.Id != recording).Take(6).ToList());
            RecentSessions.Clear();
            foreach (var s in list) RecentSessions.Add(new RecentSessionItem(s, s.GameId == null ? null : IconFor(new GameProfile { Id = s.GameId, DisplayName = s.GameDisplayName ?? "", ProcessRules = Array.Empty<ProcessRule>() })));
            OnPropertyChanged(nameof(HasRecentSessions));
            if (LastVerdict == null && list.FirstOrDefault() is { } newest)
            {
                var session = await Task.Run(() => _host.Sessions.Load(newest.Id));
                if (session != null && LastVerdict == null) await ShowLastVerdictAsync(session);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // the list is a convenience; History shows the details
        }
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;

    private void UpdateSoFar()
    {
        if (!IsMonitoring || !IsVisible || _verdictBusy || DateTime.UtcNow - _lastSoFar < TimeSpan.FromSeconds(5)) return;
        _verdictBusy = true;
        _lastSoFar = DateTime.UtcNow;
        double busy = BusyBps;
        Task.Run(() =>
        {
            var snap = _host.Recorder.Snapshot();
            return snap == null ? null : new VerdictViewModel(SessionVerdict.Build(snap, busy), SessionAnalysis.Compute(snap, busy));
        }).ContinueWith(t =>
        {
            _verdictBusy = false;
            if (t.Status == TaskStatus.RanToCompletion && IsMonitoring) SoFar = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------ live tiles

    public string RouterPing { get => _routerPing; private set => SetProperty(ref _routerPing, value); }
    public string InternetPing { get => _internetPing; private set => SetProperty(ref _internetPing, value); }
    public string Jitter { get => _jitter; private set => SetProperty(ref _jitter, value); }
    public string Loss { get => _loss; private set => SetProperty(ref _loss, value); }
    public string Traffic { get => _traffic; private set => SetProperty(ref _traffic, value); }
    public string Processor { get => _processor; private set => SetProperty(ref _processor, value); }
    public string RouterLevel { get => _routerLevel; private set { if (SetProperty(ref _routerLevel, value)) OnPropertyChanged(nameof(RouterBadge)); } }
    public string InternetLevel { get => _internetLevel; private set { if (SetProperty(ref _internetLevel, value)) OnPropertyChanged(nameof(InternetBadge)); } }
    public string JitterLevel { get => _jitterLevel; private set { if (SetProperty(ref _jitterLevel, value)) OnPropertyChanged(nameof(JitterBadge)); } }
    public string LossLevel { get => _lossLevel; private set { if (SetProperty(ref _lossLevel, value)) OnPropertyChanged(nameof(LossBadge)); } }
    public string TrafficLevel { get => _trafficLevel; private set { if (SetProperty(ref _trafficLevel, value)) OnPropertyChanged(nameof(TrafficBadge)); } }
    public string ProcessorLevel { get => _processorLevel; private set { if (SetProperty(ref _processorLevel, value)) OnPropertyChanged(nameof(ProcessorBadge)); } }
    public string RouterBadge => Words(_routerLevel, "Steady", "Slow", "Trouble");
    public string InternetBadge => Words(_internetLevel, "Normal", "Higher", "No reply");
    public string JitterBadge => Words(_jitterLevel, "Steady", "Uneven", "Jumpy");
    public string LossBadge => Words(_lossLevel, "None", "Some", "High");
    public string TrafficBadge => Words(_trafficLevel, "Light", "Heavy", "Heavy");
    public string ProcessorBadge => Words(_processorLevel, "Fine", "Busy", "Flat out");

    private static string Words(string level, string good, string fair, string poor) => level switch
    {
        "Good" => good,
        "Fair" => fair,
        "Poor" => poor,
        _ => ""
    };
    public string RouterCaption { get => _routerCaption; private set => SetProperty(ref _routerCaption, value); }
    public string InternetCaption { get => _internetCaption; private set => SetProperty(ref _internetCaption, value); }
    public string TrafficCaption { get => _trafficCaption; private set => SetProperty(ref _trafficCaption, value); }
    public string ProcessorCaption { get => _processorCaption; private set => SetProperty(ref _processorCaption, value); }

    private void UpdateTiles()
    {
        var metrics = _host.Engine.GetCurrentMetrics();
        var nic = _host.Engine.CurrentNic;
        var gw = metrics.FirstOrDefault(m => m.IsGateway);
        if (gw == null || gw.TotalProbes == 0) { RouterPing = "-"; RouterLevel = "Unknown"; RouterCaption = "your router"; }
        else
        {
            RouterPing = gw.IsReachable ? $"{gw.CurrentLatency:F0}" : "no reply";
            bool slow = gw.IsReachable && gw.CurrentLatency > (nic?.IsWifi == true ? 25 : 6);
            RouterLevel = !gw.IsReachable || gw.PacketLossPercent > 4 ? "Poor" : slow || gw.PacketLossPercent > 0 ? "Fair" : "Good";
            RouterCaption = nic?.IsWifi == true ? $"over Wi-Fi{(nic.WifiSignalPercent is int s ? $", signal {s}%" : "")}" : "over cable";
        }

        var refs = metrics.Where(m => !m.IsGateway && !m.IsDnsResolver && m.Protocol == NetworkProtocol.Icmp && m.TotalProbes > 0).ToList();
        var answering = refs.Where(m => m.IsReachable && m.ReplyCount > 0).ToList();
        if (answering.Count == 0)
        {
            InternetPing = refs.Count == 0 ? "-" : "no reply"; InternetLevel = refs.Count == 0 ? "Unknown" : "Poor";
            Jitter = "-"; JitterLevel = "Unknown"; Loss = "-"; LossLevel = refs.Count == 0 ? "Unknown" : "Poor";
            InternetCaption = "reference servers, not the game's";
        }
        else
        {
            var ping = answering.Select(m => m.CurrentLatency).OrderBy(x => x).ElementAt(answering.Count / 2);
            InternetPing = $"{ping:F0}";
            bool elevated = answering.Count(m => m.IsLatencyElevated) * 2 > answering.Count;
            InternetLevel = elevated ? "Fair" : "Good";
            InternetCaption = elevated ? "higher than usual · not the game's server" : "reference servers, not the game's";
            double jitter = answering.Average(m => m.Jitter);
            Jitter = $"{jitter:F0}";
            JitterLevel = jitter < 10 ? "Good" : jitter < 30 ? "Fair" : "Poor";
            double loss = refs.Average(m => m.PacketLossPercent);
            Loss = loss < 0.05 ? "0" : $"{loss:F1}";
            LossLevel = loss < 0.5 ? "Good" : loss < 3 ? "Fair" : "Poor";
        }

        var a = _host.InternetAdapterActivity()?.Latest;
        if (a == null) { Traffic = "-"; TrafficLevel = "Unknown"; TrafficCaption = "this PC"; }
        else
        {
            double heavy = BusyBps;
            Traffic = AdapterActivityMonitor.FormatBps(Math.Max(a.RxBps, a.TxBps)).Split(' ')[0];
            TrafficCaption = $"{AdapterActivityMonitor.FormatBps(a.RxBps)} down, {AdapterActivityMonitor.FormatBps(a.TxBps)} up";
            TrafficLevel = Math.Max(a.RxBps, a.TxBps) >= heavy ? "Fair" : "Good";
            _trafficUnit = AdapterActivityMonitor.FormatBps(Math.Max(a.RxBps, a.TxBps)).Split(' ').Skip(1).FirstOrDefault() ?? "";
            OnPropertyChanged(nameof(TrafficUnit));
        }

        if (_host.ProcessorPercent is double cpu)
        {
            Processor = $"{cpu:F0}";
            ProcessorLevel = cpu < 80 ? "Good" : cpu < SessionVerdict.ProcessorBusyPercent ? "Fair" : "Poor";
            ProcessorCaption = cpu >= SessionVerdict.ProcessorBusyPercent ? "flat out: can stutter like lag" : "of all cores";
        }
        else { Processor = "-"; ProcessorLevel = "Unknown"; ProcessorCaption = "of all cores"; }
    }

    private string _trafficUnit = "";
    public string TrafficUnit => _trafficUnit;

    // ------------------------------------------------------------------ status page

    /// <summary>The game whose official status page can be opened: the one shown at the top, else the one chosen.</summary>
    public GameProfile? StatusPageGame => new[] { _snapshot.Game, SelectedGame?.Profile }.FirstOrDefault(p => p?.HasStatusPage == true);
    public bool HasStatusPage => StatusPageGame != null;
    public string StatusPageText => StatusPageGame is { } g ? $"Is {g.DisplayName} itself down? Check its official status page." : "";

    private void OpenStatusPage()
    {
        if (StatusPageGame?.StatusPage.Url is not { } url) return;
        try
        {
            SafeOpen.WebPage(url);   // opens in the browser on request; NetPulse itself does not contact the page
        }
        catch (Exception ex)
        {
            ActionMessage = $"Could not open the status page: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ refresh (UI thread only)

    private void QueueRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshQueued, 1) == 1) return; // coalesce bursts
        _dispatcher.InvokeAsync(() =>
        {
            System.Threading.Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var before = _snapshot;
        _snapshot = _monitor.Snapshot();
        var snap = _snapshot;

        bool wasMonitoring = IsMonitoring;
        IsMonitoring = snap.IsMonitoring;
        if (!wasMonitoring && snap.IsMonitoring)
        {
            SessionMessage = "";
            SoFar = null;
            _clock.Start();
            if (snap.StartedAutomatically) ActionMessage = $"{snap.Game?.DisplayName} started, so recording started by itself. It is saved once the game has been closed for 20 seconds.";
        }
        else if (wasMonitoring && !snap.IsMonitoring)
        {
            _clock.Stop();
        }

        UpdateStatus(snap);
        // a running game not on the list yet (a built-in game Windows has not listed) joins it
        if (snap.Running.Any(r => Games.All(g => g.Id != r.Id))) RebuildGames();
        if (before.Game?.Id != snap.Game?.Id) NotifyCard();
        OnPropertyChanged(nameof(RecordingGameName));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardDetail));
        OnPropertyChanged(nameof(StatusPageGame));
        OnPropertyChanged(nameof(HasStatusPage));
        OnPropertyChanged(nameof(StatusPageText));
        UpdateElapsed();

        if (!IsVisible) return;
        UpdateTiles();
        UpdateSoFar();
    }

    private void NotifyCard()
    {
        OnPropertyChanged(nameof(CurrentGame));
        OnPropertyChanged(nameof(CurrentIcon));
        OnPropertyChanged(nameof(HasCurrentIcon));
        OnPropertyChanged(nameof(CurrentMonogram));
        OnPropertyChanged(nameof(HasCurrentGame));
    }

    private void UpdateStatus(GameMonitorSnapshot snap)
    {
        bool all = snap.Watching == GameMonitorService.AllGames;
        string auto = AutoRecord ? "It is saved by itself once the game has been closed for 20 seconds." : "Press Stop & save when you are done.";
        if (snap.IsMonitoring && snap.Game != null && snap.Process?.State == GameRunState.Running)
        {
            StatusTitle = $"Recording {snap.Game.DisplayName}";
            StatusDetail = $"NetPulse is measuring your connection while you play. {auto}";
            StatusLevel = "Busy";
        }
        else if (snap.IsMonitoring && snap.Game != null)
        {
            StatusTitle = $"Recording {snap.Game.DisplayName}";
            StatusDetail = snap.Process?.State == GameRunState.LauncherOnly
                ? $"{snap.Process.Message} The recording goes on."
                : $"{snap.Game.DisplayName} is not running at the moment; the recording goes on.";
            StatusLevel = "Busy";
        }
        else if (snap.IsMonitoring)
        {
            StatusTitle = "Recording: waiting for a game";
            StatusDetail = "Start any of your games; the recording is about the first one that starts.";
            StatusLevel = "Busy";
        }
        else if (snap.Game != null && snap.Process?.State == GameRunState.Running)
        {
            StatusTitle = $"{snap.Game.DisplayName} is running";
            StatusDetail = _host.Recorder.IsRecording
                ? "Not recording it: a network recording is running (History & reports)."
                : !AutoRecord ? "Not recording. Press Record now to record this session."
                : _monitor.NotRecordingAgain == snap.Game.Id ? "Not recording: you stopped the recording while it runs. Press Record now to record again; it records by itself the next time it starts."
                : "Not recording yet.";
            StatusLevel = "Good";
        }
        else if (!all && snap.Process != null)
        {
            StatusTitle = snap.Process.Message;
            StatusDetail = AutoRecord ? $"NetPulse starts recording when {snap.Game?.DisplayName} starts." : "Automatic recording is off: press Record now when you play.";
            StatusLevel = snap.Process.State == GameRunState.LauncherOnly ? "Info" : "Unknown";
        }
        else
        {
            StatusTitle = "No game running";
            StatusDetail = AutoRecord ? "NetPulse starts recording when one of your games starts, and saves it when the game closes." : "Automatic recording is off: press Record now when you play.";
            StatusLevel = "Info";
        }
    }

    private void UpdateElapsed()
    {
        var start = _snapshot.StartedUtc;
        Elapsed = start.HasValue && IsMonitoring ? (DateTime.UtcNow - start.Value).ToString(@"hh\:mm\:ss") : "00:00:00";
        if (IsMonitoring) OnPropertyChanged(nameof(CardDetail));
    }

    // ------------------------------------------------------------------ Overview card

    public string CardTitle => IsMonitoring ? $"Recording {RecordingGameName}"
        : CurrentGame != null && _snapshot.Process?.State == GameRunState.Running ? $"{CurrentGame.DisplayName} is running"
        : _snapshot.Watching is { } w && w != GameMonitorService.AllGames && CurrentGame != null ? $"Watching for {CurrentGame.DisplayName}"
        : "Watching for your games";
    public string CardDetail => IsMonitoring
        ? (SoFar is { } v ? $"{Elapsed} so far: {v.Headline.ToLowerInvariant()}." : $"{Elapsed} so far.")
        : LastVerdict is { } last ? $"Last session: {last.Headline}." : StatusDetail;
}
