using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Lagwise.App.ViewModels;

/// <summary>
/// A short message at the bottom of the window ("Report saved", "Could not save ..."), with an optional action such as
/// "Show in folder". It replaces message boxes for results that need no decision, and it is visible wherever the button
/// that caused it was on the page. Hides itself after a few seconds.
/// </summary>
public sealed class ToastViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private string _text = "";
    private bool _isVisible, _isError;
    private string _actionText = "";
    private Action? _action;

    public ToastViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => Hide();
        ActionCommand = new RelayCommand(() => { var a = _action; Hide(); a?.Invoke(); }, () => _action != null);
        CloseCommand = new RelayCommand(Hide);
    }

    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }
    public string ActionText { get => _actionText; private set { if (SetProperty(ref _actionText, value)) OnPropertyChanged(nameof(HasAction)); } }
    public bool HasAction => _actionText.Length > 0;
    public RelayCommand ActionCommand { get; }
    public RelayCommand CloseCommand { get; }

    public void Show(string text, string? actionText = null, Action? action = null)
    {
        _action = action;
        ActionText = action == null ? "" : actionText ?? "Open";
        IsError = false;
        Text = text;
        IsVisible = true;
        Restart(TimeSpan.FromSeconds(7));
    }

    /// <summary>A failure stays longer, and says what to try.</summary>
    public void Error(string text)
    {
        _action = null;
        ActionText = "";
        IsError = true;
        Text = text;
        IsVisible = true;
        Restart(TimeSpan.FromSeconds(12));
    }

    /// <summary>"Saved Lagwise_Report.md" with a "Show in folder" action that selects the file in Explorer.</summary>
    public void Saved(string what, string path) =>
        Show($"{what} saved: {Path.GetFileName(path)}", "Show in folder", () => ShowInFolder(path));

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }

    private void Restart(TimeSpan duration)
    {
        _timer.Stop();
        _timer.Interval = duration;
        _timer.Start();
    }

    private void ShowInFolder(string path)
    {
        try
        {
            SafeOpen.SelectFile(path);
        }
        catch (Exception ex)
        {
            Error($"Could not open the folder: {ex.Message}");
        }
    }

    /// <summary>Plain words for the usual reasons a file cannot be written.</summary>
    public static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows did not allow writing there. Choose another folder, such as Documents.",
        IOException io when io.HResult == unchecked((int)0x80070020) => "The file is open in another program. Close it, or save under another name.",
        IOException io => io.Message,
        _ => ex.Message
    };
}
