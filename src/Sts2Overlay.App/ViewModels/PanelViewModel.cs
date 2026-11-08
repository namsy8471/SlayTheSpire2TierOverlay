using System.Windows.Input;
using System.Windows.Media;

namespace Sts2Overlay.App.ViewModels;

public enum PanelTab
{
    Offer,
    Deck,
    Build,
    Path,
}

/// <summary>Root view model of the panel window. Sections are replaced wholesale by <see cref="Controllers.AppController"/>.</summary>
public sealed class PanelViewModel : ObservableObject
{
    private PanelTab _selectedTab;
    private bool _isCollapsed;
    private double _uiScale = 1.0;
    private Brush _background = new SolidColorBrush(Color.FromArgb(220, 18, 18, 24));
    private string _statusText = "게임 창을 찾는 중…";
    private string _packText = "";
    private string _runText = "런 정보 없음";
    private string? _messageText;
    private bool _clickThrough;
    private bool _isDemo;
    private OfferSectionVm _offer = OfferSectionVm.Empty;
    private DeckSectionVm _deck = DeckSectionVm.Empty;
    private BuildSectionVm _build = BuildSectionVm.Empty;
    private PathSectionVm _path = PathSectionVm.Empty;

    public PanelViewModel()
    {
        ToggleCollapseCommand = new RelayCommand(_ => IsCollapsed = !IsCollapsed);
    }

    public ICommand ToggleCollapseCommand { get; }

    public PanelTab SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => Set(ref _isCollapsed, value);
    }

    /// <summary>LayoutTransform factor = UI 배율 / monitor DPI scale (WPF applies the DPI part itself). See <see cref="Views.UiScale"/>.</summary>
    public double UiScale
    {
        get => _uiScale;
        set => Set(ref _uiScale, value);
    }

    private string _uiScaleText = "";

    /// <summary>"UI 150% (자동)" shown in the footer.</summary>
    public string UiScaleText
    {
        get => _uiScaleText;
        set => Set(ref _uiScaleText, value);
    }

    public Brush PanelBackground
    {
        get => _background;
        set => Set(ref _background, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string PackText
    {
        get => _packText;
        set => Set(ref _packText, value);
    }

    public string RunText
    {
        get => _runText;
        set => Set(ref _runText, value);
    }

    /// <summary>Transient line (capture saved, errors). Null hides it.</summary>
    public string? MessageText
    {
        get => _messageText;
        set => Set(ref _messageText, value);
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set => Set(ref _clickThrough, value);
    }

    public bool IsDemo
    {
        get => _isDemo;
        set => Set(ref _isDemo, value);
    }

    public OfferSectionVm Offer
    {
        get => _offer;
        set => Set(ref _offer, value);
    }

    public DeckSectionVm Deck
    {
        get => _deck;
        set => Set(ref _deck, value);
    }

    public BuildSectionVm Build
    {
        get => _build;
        set => Set(ref _build, value);
    }

    public PathSectionVm Path
    {
        get => _path;
        set => Set(ref _path, value);
    }
}
