using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lagwise.Core.Security;
using Lagwise.Core.Services;

namespace Lagwise.App.ViewModels;

/// <summary>One line of the security check.</summary>
public sealed record SecurityRow(string Title, string Detail, string Advice, string Level)
{
    public bool HasAdvice => Advice.Length > 0;
}

/// <summary>
/// Security check: what Windows reports about this PC (shown as soon as the page opens, nothing is sent) and, when asked,
/// what a few harmless connections show about your DNS and secure connections.
/// </summary>
public sealed class SecurityViewModel : ViewModelBase
{
    private readonly LagwiseHost _host;
    private SecurityEvidence? _full;
    private bool _checking, _isVisible;
    private string _headline = "Open this page to see how this PC is protected.", _level = "Unknown", _checkedText = "", _progress = "";

    public SecurityViewModel(LagwiseHost host)
    {
        _host = host;
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !_checking);
    }

    public ObservableCollection<SecurityRow> Items { get; } = new();
    public RelayCommand CheckCommand { get; }
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Level { get => _level; private set => SetProperty(ref _level, value); }
    public string CheckedText { get => _checkedText; private set => SetProperty(ref _checkedText, value); }
    public string ProgressText { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsChecking { get => _checking; private set { if (SetProperty(ref _checking, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>On screen: the local part is read again (it costs nothing and sends nothing).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value) && value && !_checking) _ = RefreshLocalAsync(); }
    }

    private async Task RefreshLocalAsync()
    {
        var local = await Task.Run(() => _host.Security.Local());
        // keep the network results of an earlier check; the local part is always fresh
        var evidence = _full == null ? local : new SecurityEvidence
        {
            Firewall = local.Firewall, OnWifi = local.OnWifi, WifiAuthentication = local.WifiAuthentication, WifiCipher = local.WifiCipher, Listeners = local.Listeners,
            MadeUpNameYourDns = _full.MadeUpNameYourDns, MadeUpNamePublicDns = _full.MadeUpNamePublicDns, Tls = _full.Tls, Upnp = _full.Upnp
        };
        Show(SecurityCheck.Evaluate(evidence, DateTime.UtcNow));
    }

    private async Task CheckAsync()
    {
        IsChecking = true;
        ProgressText = "Checking your DNS, secure connections and router...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _full = await _host.Security.FullAsync(cts.Token);
            var report = SecurityCheck.Evaluate(_full, DateTime.UtcNow);
            Show(report);
            CheckedText = $"Checked {DateTime.Now:HH:mm}.";
            if (report.Overall is SecurityStatus.Poor or SecurityStatus.Fair)
                _host.Events.LogEvent("SecurityCheck", report.Overall == SecurityStatus.Poor ? "Warning" : "Info",
                    $"Security check: {report.Headline} " + string.Join(" ", report.Items.Where(i => i.Status is SecurityStatus.Poor or SecurityStatus.Fair).Select(i => $"{i.Title}: {i.Detail}")));
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "The check took too long and was stopped.";
        }
        catch (Exception ex)
        {
            ProgressText = $"The check could not finish: {ex.GetBaseException().Message}";
            ErrorLog.Write("Security check", ex);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Show(SecurityReport r)
    {
        Headline = r.Headline;
        Level = LevelOf(r.Overall);
        Items.Clear();
        foreach (var i in r.Items) Items.Add(new SecurityRow(i.Title, i.Detail, i.Advice, LevelOf(i.Status)));
    }

    public static string LevelOf(SecurityStatus s) => s switch
    {
        SecurityStatus.Good => "Good",
        SecurityStatus.Fair => "Fair",
        SecurityStatus.Poor => "Poor",
        SecurityStatus.Info => "Info",
        _ => "Unknown"
    };
}
