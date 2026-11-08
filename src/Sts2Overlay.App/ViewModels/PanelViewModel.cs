using System.Windows.Input;
using System.Windows.Media;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.ViewModels;

public enum PanelTab
{
    Offer,
    Deck,
    Build,
    Path,
    Review,
}

/// <summary>Root view model of the panel window. Sections are replaced wholesale by <see cref="Controllers.AppController"/>.</summary>
public sealed class PanelViewModel : ObservableObject
{
    private PanelTab _selectedTab;
    private bool _isCollapsed;
    private double _uiScale = 1.0;
    private Brush _background = new SolidColorBrush(Color.FromArgb(220, 18, 18, 24));
    private string _statusText = Loc.T("게임 창을 찾는 중…", "Looking for the game window…");
    private string _packText = "";
    private string _runText = Loc.T("런 정보 없음", "No run info");
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
        // ---- forecast / run review (m2/forecast-review) ----
        ShowReviewCommand = new RelayCommand(_ =>
        {
            Review = Review with { ShowBanner = false };
            SelectedTab = PanelTab.Review;
        });
        DismissReviewCommand = new RelayCommand(_ => Review = Review with { ShowBanner = false });
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

    // ---- local resources / version guard (Services.LocalResourceService) ----
    private string? _resourceBanner;

    /// <summary>
    /// Persistent notice above the footer: resource preparation progress/failure and the game-vs-pack version warning.
    /// Unlike <see cref="MessageText"/> it does not time out. Null hides it.
    /// </summary>
    public string? ResourceBanner
    {
        get => _resourceBanner;
        set => Set(ref _resourceBanner, value);
    }
    // ---- end local resources ----

    private string? _updateText;

    /// <summary>"새 버전 v0.2.0 준비됨 …" once an update was downloaded (Services.UpdateService). Null hides it.</summary>
    public string? UpdateText
    {
        get => _updateText;
        set => Set(ref _updateText, value);
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

    // ---- forecast / run review (m2/forecast-review) ----------------------------------------------------

    private ForecastSectionVm _forecast = ForecastSectionVm.Empty;
    private ReviewSectionVm _review = ReviewSectionVm.Empty;

    /// <summary>Monte Carlo build-completion block at the top of the 빌드 tab.</summary>
    public ForecastSectionVm Forecast
    {
        get => _forecast;
        set => Set(ref _forecast, value);
    }

    /// <summary>Last finished run's review (리뷰 tab) and its "new review" banner.</summary>
    public ReviewSectionVm Review
    {
        get => _review;
        set => Set(ref _review, value);
    }

    /// <summary>Banner "보기": open the 리뷰 tab and hide the banner.</summary>
    public ICommand ShowReviewCommand { get; }

    /// <summary>Banner "닫기": hide the banner; the review stays in the 리뷰 tab.</summary>
    public ICommand DismissReviewCommand { get; }
}
