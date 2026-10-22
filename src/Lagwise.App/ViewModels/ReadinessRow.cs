using System.Windows.Media;
using Lagwise.Core.Services;

namespace Lagwise.App.ViewModels;

/// <summary>One line of the "Ready to play?" checklist. Updated in place so the list does not flicker.</summary>
public sealed class ReadinessRow : ViewModelBase
{
    private string _title = "", _detail = "", _glyph = "";
    private Brush _brush = System.Windows.Media.Brushes.Gray;
    private bool _isUntestedLoad;

    public ReadinessRow(ReadinessItem item) { Id = item.Id; Update(item); }

    public string Id { get; }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string Glyph { get => _glyph; private set => SetProperty(ref _glyph, value); }
    public Brush Brush { get => _brush; private set => SetProperty(ref _brush, value); }
    /// <summary>The bufferbloat line offers a shortcut to the test while it has never been run.</summary>
    public bool IsUntestedLoad { get => _isUntestedLoad; private set => SetProperty(ref _isUntestedLoad, value); }

    public void Update(ReadinessItem item)
    {
        Title = item.Title;
        Detail = item.Detail;
        (Glyph, Brush) = MainViewModel.StatusVisual(item.Status);
        IsUntestedLoad = item.Id == "load" && item.Status == CheckStatus.Unknown;
    }
}
