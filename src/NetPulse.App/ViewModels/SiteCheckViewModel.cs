using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetPulse.Core.Services;
using NetPulse.Core.Sites;

namespace NetPulse.App.ViewModels;

/// <summary>One line of a site's step-by-step explanation.</summary>
public sealed record SiteStepRow(string Title, string Detail, string Level);

/// <summary>One site on the Site check page.</summary>
public sealed class SiteRow : ViewModelBase
{
    private SiteResult? _result;
    private ImageSource? _icon;
    private bool _checking, _expanded;

    public SiteRow(SiteDefinition site, ImageSource? icon)
    {
        Site = site;
        _icon = icon;
    }

    public SiteDefinition Site { get; }
    public string Name => Site.Name;
    public string Host => Site.Host;
    public string Monogram => Site.Monogram;
    public string Category => Site.Category;
    public bool HasStatusPage => Site.StatusApi != null;

    public ImageSource? Icon { get => _icon; set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;

    public bool IsChecking { get => _checking; set { if (SetProperty(ref _checking, value)) OnPropertyChanged(nameof(Level)); } }
    public bool IsExpanded { get => _expanded; set => SetProperty(ref _expanded, value); }

    public SiteResult? Result
    {
        get => _result;
        set
        {
            if (!SetProperty(ref _result, value)) return;
            Steps.Clear();
            if (value != null)
                foreach (var s in value.Steps)
                    Steps.Add(new SiteStepRow(s.Title, s.Detail, s.Outcome switch { StepOutcome.Ok => "Good", StepOutcome.Warning => "Fair", StepOutcome.Failed => "Poor", _ => "Unknown" }));
            foreach (var p in new[] { nameof(Level), nameof(VerdictText), nameof(Explanation), nameof(Advice), nameof(HasAdvice), nameof(AnswerText), nameof(CheckedText), nameof(HasResult) })
                OnPropertyChanged(p);
        }
    }

    public ObservableCollection<SiteStepRow> Steps { get; } = new();
    public bool HasResult => _result != null;

    /// <summary>"Good" up, "Fair" slow or a problem at the site, "Poor" blocked or unreachable, "Unknown" not checked, "Busy" checking.</summary>
    public string Level => _checking ? "Busy" : _result == null ? "Unknown" : LevelOf(_result.Verdict);

    public static string LevelOf(SiteVerdict v) => v switch
    {
        SiteVerdict.Up => "Good",
        SiteVerdict.Slow or SiteVerdict.SiteProblem => "Fair",
        SiteVerdict.NoSuchSite => "Unknown",
        _ => "Poor"
    };

    public string VerdictText => _checking ? "Checking..." : _result?.Headline ?? "Not checked yet";
    public string Explanation => _result?.Explanation ?? "";
    public string Advice => _result?.Advice ?? "";
    public bool HasAdvice => Advice.Length > 0;
    public string AnswerText => _result?.AnswerMs is double ms ? (ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms") : "";
    public string CheckedText => _result == null ? "" : $"Checked {DateTime.SpecifyKind(_result.CheckedUtc, DateTimeKind.Utc).ToLocalTime():HH:mm}";
}

/// <summary>
/// Site check: is a site down, or blocked between you and it, and where? Runs only when asked (or every few minutes when
/// switched on), and notes changes in the event timeline.
/// </summary>
public sealed class SiteCheckViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly ToastViewModel _toast;
    private readonly DispatcherTimer _autoTimer;
    private readonly Dictionary<string, SiteVerdict> _lastVerdict = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _checking;
    private string _summary = "Not checked yet. Press Check all to look at every site below.";
    private string _progress = "", _newSite = "", _addError = "";
    private DateTime? _lastCheck;

    public SiteCheckViewModel(NetPulseHost host, Dispatcher dispatcher, ToastViewModel toast)
    {
        _host = host;
        _dispatcher = dispatcher;
        _toast = toast;
        CheckAllCommand = new RelayCommand(() => _ = CheckAsync(Sites.ToList(), quiet: false), () => !_checking && Sites.Count > 0);
        CheckOneCommand = new RelayCommand(p => { if (p is SiteRow r) _ = CheckAsync(new[] { r }, quiet: false); }, _ => !_checking);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _checking);
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(p => { if (p is SiteRow r) Remove(r); });
        ShowHiddenCommand = new RelayCommand(ShowHidden, () => HiddenCount > 0);
        ToggleCommand = new RelayCommand(p => { if (p is SiteRow r) r.IsExpanded = !r.IsExpanded; });
        _autoTimer = new DispatcherTimer(TimeSpan.FromMinutes(Math.Max(5, host.Settings.SiteCheckEveryMinutes)), DispatcherPriority.Background,
            (_, _) => { if (!_checking) _ = CheckAsync(Sites.ToList(), quiet: true); }, dispatcher);
        _autoTimer.IsEnabled = host.Settings.SiteCheckAuto;
        Rebuild();
    }

    public ObservableCollection<SiteRow> Sites { get; } = new();
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand CheckOneCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ShowHiddenCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public bool IsChecking { get => _checking; private set { if (SetProperty(ref _checking, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string ProgressText { get => _progress; private set => SetProperty(ref _progress, value); }
    public string NewSite { get => _newSite; set { if (SetProperty(ref _newSite, value)) AddError = ""; } }
    public string AddError { get => _addError; private set { if (SetProperty(ref _addError, value)) OnPropertyChanged(nameof(HasAddError)); } }
    public bool HasAddError => _addError.Length > 0;
    public int HiddenCount => _host.Settings.SiteCheckHidden.Count(id => SiteCatalog.Popular.Any(s => s.Id == id));
    public string ShowHiddenText => HiddenCount == 1 ? "Show the 1 popular site you removed" : $"Show the {HiddenCount} popular sites you removed";
    public bool HasHidden => HiddenCount > 0;

    /// <summary>Check every so often and note changes in the event timeline. Off by default: nothing is contacted unless asked.</summary>
    public bool AutoCheck
    {
        get => _host.Settings.SiteCheckAuto;
        set
        {
            if (_host.Settings.SiteCheckAuto == value) return;
            _host.Settings.SiteCheckAuto = value;
            _host.SaveSettings(out _);
            _autoTimer.IsEnabled = value;
            OnPropertyChanged();
            if (value && _lastCheck == null && !_checking) _ = CheckAsync(Sites.ToList(), quiet: true);
        }
    }

    public string AutoCheckText => $"Check every {_host.Settings.SiteCheckEveryMinutes} minutes while NetPulse runs, and note changes in the event timeline";

    private string IconFolder => Path.Combine(_host.DataDirectory, "site-icons");

    /// <summary>The latest result of every site checked this session (for the exported report).</summary>
    public IReadOnlyList<SiteResult> LastResults() => Sites.Where(r => r.Result != null).Select(r => r.Result!).ToList();

    private void Rebuild()
    {
        var hidden = new HashSet<string>(_host.Settings.SiteCheckHidden, StringComparer.OrdinalIgnoreCase);
        var defs = SiteCatalog.Popular.Where(s => !hidden.Contains(s.Id))
            .Concat(_host.Settings.SiteCheckHosts.Select(SiteCatalog.Custom).Where(s => s != null).Select(s => s!))
            .ToList();
        var old = Sites.ToDictionary(r => r.Site.Id, StringComparer.OrdinalIgnoreCase);
        Sites.Clear();
        foreach (var d in defs)
            Sites.Add(old.TryGetValue(d.Id, out var keep) ? keep : new SiteRow(d, LoadIcon(d)));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(ShowHiddenText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void Add()
    {
        var site = SiteCatalog.Custom(NewSite);
        if (site == null) { AddError = "That does not look like a site on the internet. Type its address, for example example.com."; return; }
        if (Sites.Any(r => string.Equals(r.Host, site.Host, StringComparison.OrdinalIgnoreCase))) { AddError = $"{site.Host} is already in the list."; return; }
        if (_host.Settings.SiteCheckHosts.Count >= Core.Models.AppSettings.MaxSiteCheckHosts) { AddError = $"You can add up to {Core.Models.AppSettings.MaxSiteCheckHosts} sites."; return; }
        _host.Settings.SiteCheckHosts.Add(site.Host);
        _host.SaveSettings(out _);
        NewSite = "";
        Rebuild();
        var row = Sites.FirstOrDefault(r => r.Site.Id == site.Id);
        if (row != null && !_checking) _ = CheckAsync(new[] { row }, quiet: false);
    }

    private void Remove(SiteRow row)
    {
        if (row.Site.BuiltIn) { if (!_host.Settings.SiteCheckHidden.Contains(row.Site.Id)) _host.Settings.SiteCheckHidden.Add(row.Site.Id); }
        else _host.Settings.SiteCheckHosts.RemoveAll(h => string.Equals(h, row.Host, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings(out _);
        _lastVerdict.Remove(row.Site.Id);
        Rebuild();
        UpdateSummary();
    }

    private void ShowHidden()
    {
        _host.Settings.SiteCheckHidden.Clear();
        _host.SaveSettings(out _);
        Rebuild();
    }

    private async Task CheckAsync(IReadOnlyList<SiteRow> rows, bool quiet)
    {
        if (_checking || rows.Count == 0) return;
        IsChecking = true;
        _cts = new CancellationTokenSource();
        foreach (var r in rows) r.IsChecking = true;
        int done = 0;
        ProgressText = $"Checking {rows.Count} site{(rows.Count == 1 ? "" : "s")}...";
        var byId = rows.ToDictionary(r => r.Site.Id, StringComparer.OrdinalIgnoreCase);
        var progress = new Progress<SiteResult>(res =>
        {
            if (!byId.TryGetValue(res.Site.Id, out var row)) return;
            row.IsChecking = false;
            row.Result = res;
            ProgressText = $"Checked {++done} of {rows.Count}...";
        });
        try
        {
            var results = await _host.SiteChecks.CheckAsync(rows.Select(r => r.Site).ToList(), progress, _cts.Token);
            foreach (var res in results)
            {
                if (!byId.TryGetValue(res.Site.Id, out var row)) continue;
                row.IsChecking = false;
                row.Result = res;
                NoteChange(res, quiet);
                if (res.IsReachable && !row.HasIcon) _ = FetchIconAsync(row);
            }
            _lastCheck = DateTime.UtcNow;
            ProgressText = "";
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Check cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText = $"The check could not finish: {ex.GetBaseException().Message}";
            ErrorLog.Write("Site check", ex);
        }
        finally
        {
            foreach (var r in rows) r.IsChecking = false;
            IsChecking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>A site that changes state is noted in the timeline (not on the first check of a session, which has nothing to compare with).</summary>
    private void NoteChange(SiteResult res, bool quiet)
    {
        bool had = _lastVerdict.TryGetValue(res.Site.Id, out var before);
        _lastVerdict[res.Site.Id] = res.Verdict;
        if (!had || SiteRow.LevelOf(before) == SiteRow.LevelOf(res.Verdict)) return;
        _host.Events.LogEvent("SiteCheck", res.IsReachable ? "Info" : "Warning",
            $"{res.Site.Name}: {res.Headline.ToLowerInvariant()} (was {SiteDiagnosis.Headline(before).ToLowerInvariant()}). {res.Explanation}", res.Site.Name);
        if (!quiet && !res.IsReachable) _toast.Show($"{res.Site.Name}: {res.Headline.ToLowerInvariant()}.");
    }

    private void UpdateSummary()
    {
        var checkedRows = Sites.Where(r => r.Result != null).ToList();
        if (checkedRows.Count == 0) { Summary = "Not checked yet. Press Check all to look at every site below."; return; }
        int up = checkedRows.Count(r => r.Result!.IsReachable);
        var parts = new List<string> { $"{up} of {checkedRows.Count} sites reachable." };
        foreach (var g in checkedRows.Where(r => !r.Result!.IsReachable).GroupBy(r => r.Result!.Headline))
            parts.Add($"{g.Key}: {string.Join(", ", g.Select(r => r.Name))}.");
        if (checkedRows.Any(r => r.Result!.Verdict == SiteVerdict.Slow))
            parts.Add($"Slow: {string.Join(", ", checkedRows.Where(r => r.Result!.Verdict == SiteVerdict.Slow).Select(r => r.Name))}.");
        if (_lastCheck is DateTime t) parts.Add($"Last check {DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime():HH:mm}.");
        Summary = string.Join(" ", parts);
    }

    /// <summary>The site's own icon (favicon), fetched after it answered and kept for a month; the letters stay until then.</summary>
    private async Task FetchIconAsync(SiteRow row)
    {
        string? path = await SiteCheckService.EnsureIconAsync(row.Site, IconFolder, CancellationToken.None, row.Result?.IconHref);
        if (path == null) return;
        var image = LoadIconFile(path);
        if (image != null) row.Icon = image;
    }

    private ImageSource? LoadIcon(SiteDefinition site)
    {
        var path = new[] { ".ico", ".png", ".gif", ".jpg" }.Select(e => Path.Combine(IconFolder, site.Host + e)).FirstOrDefault(File.Exists);
        return path == null ? null : LoadIconFile(path);
    }

    /// <summary>Decodes a downloaded icon, picking the frame closest to 32 pixels; anything unreadable is ignored.</summary>
    private static ImageSource? LoadIconFile(string path)
    {
        try
        {
            // downloaded from the internet: refuse anything larger than an icon before the image decoder sees it
            if (new FileInfo(path).Length > SiteCheckService.MaxIconBytes) return null;
            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.Where(f => f.PixelWidth <= 512 && f.PixelHeight <= 512)
                .OrderBy(f => Math.Abs(f.PixelWidth - 32)).ThenByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame == null) return null;
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
