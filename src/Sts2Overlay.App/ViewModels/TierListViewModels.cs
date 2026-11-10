using System.Windows.Input;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.App.ViewModels;

// 티어표 tab (Controllers/AppController.Tiers.cs, Controllers/TierListPresenter.cs).
// Unlike the other sections this one is a long-lived object: the search box binds to SearchText, and swapping the object
// under a focused TextBox would reset its caret and break an IME composition. The card tiles are long-lived too, so a deck
// change only flips their owned marks instead of rebuilding ~90 tiles.

/// <summary>A toggle chip (character, card type, rarity, co-op cards). <see cref="IsOn"/> = selected / shown.</summary>
public sealed record TierChipVm(string Text, bool IsOn, ICommand Command, string? ToolTip = null);

/// <summary>One card tile of the 티어표 tab.</summary>
public sealed class TierCardVm : ObservableObject
{
    private readonly Func<CardText?>? _text;
    private CardText? _textValue;
    private bool _textLoaded;
    private int _owned;
    private string? _actMarker;
    private string? _actMarkerTier;

    /// <param name="text">Effect text, composed the first time the hover tooltip asks for it.</param>
    public TierCardVm(Func<CardText?>? text = null)
    {
        _text = text;
    }

    public required string Id { get; init; }

    /// <summary>Localized name (a readable name made from the id when the local names are missing).</summary>
    public required string Name { get; init; }

    /// <summary>The pack's base tier "S".."D", or "?" for an unrated card.</summary>
    public string TierText { get; init; } = "?";

    /// <summary>Energy cost shown on the portrait ("1", "X"); null for unplayable or unknown.</summary>
    public string? CostText { get; init; }

    /// <summary>Exists only in multiplayer runs (협동 tag).</summary>
    public bool IsCoop { get; init; }

    /// <summary>The pack marks the rating as a guess (changed in a later game version, or low confidence).</summary>
    public bool IsEstimate { get; init; }

    /// <summary>"티어 A · 희귀 · 공격 · 비용 1" line of the tooltip.</summary>
    public string DetailText { get; init; } = "";

    /// <summary>"막별 티어: 1막 A · 3막 C" when the pack rates some act differently; null otherwise.</summary>
    public string? ActTiersText { get; init; }

    /// <summary>The pack's one-line tip, in the current language; null when it has none.</summary>
    public string? Tip { get; init; }

    public CardArtVm? Art { get; init; }

    public CardText? Text
    {
        get
        {
            if (!_textLoaded)
            {
                _textValue = _text?.Invoke();
                _textLoaded = true;
            }
            return _textValue;
        }
    }

    /// <summary>Copies in the current deck (0 = none).</summary>
    public int Owned
    {
        get => _owned;
        set
        {
            if (!Set(ref _owned, value)) return;
            Raise(nameof(IsOwned));
            Raise(nameof(OwnedText));
            Raise(nameof(OwnedLine));
        }
    }

    public bool IsOwned => _owned > 0;

    /// <summary>The check on the portrait: "✓", or "✓2" for two copies.</summary>
    public string OwnedText => _owned > 1 ? $"✓{_owned}" : "✓";

    /// <summary>Tooltip line "덱에 2장"; null when not in the deck.</summary>
    public string? OwnedLine => _owned > 0 ? Loc.T($"덱에 {_owned}장", $"{_owned} in deck") : null;

    /// <summary>"1막 A": the current act's tier when it differs from the base tier; null otherwise (or with no run).</summary>
    public string? ActMarker => _actMarker;

    /// <summary>Tier letter of <see cref="ActMarker"/>, for its colour.</summary>
    public string? ActMarkerTier => _actMarkerTier;

    public void SetActMarker(string? text, string? tier)
    {
        if (_actMarker == text && _actMarkerTier == tier) return;
        _actMarker = text;
        _actMarkerTier = tier;
        Raise(nameof(ActMarker));
        Raise(nameof(ActMarkerTier));
    }
}

/// <summary>One tier section: header (tier letter in its colour + card count) and the tiles.</summary>
/// <param name="TierText">"S".."D", or "?" for the unrated section.</param>
public sealed record TierGroupVm(string TierText, string Title, IReadOnlyList<TierCardVm> Cards);

/// <summary>The 티어표 tab.</summary>
public sealed class TierListVm : ObservableObject
{
    private IReadOnlyList<TierChipVm> _characters = [];
    private IReadOnlyList<TierChipVm> _typeChips = [];
    private IReadOnlyList<TierChipVm> _rarityChips = [];
    private IReadOnlyList<TierChipVm> _optionChips = [];
    private IReadOnlyList<TierGroupVm> _groups = [];
    private string? _emptyText;
    private string _summaryText = "";
    private string? _noMatchText;
    private string _searchText = "";

    /// <summary>Raised when the user edits the search box (the controller regroups the tiles).</summary>
    public event Action? SearchChanged;

    /// <summary>The five characters + 무색.</summary>
    public IReadOnlyList<TierChipVm> Characters
    {
        get => _characters;
        set => Set(ref _characters, value);
    }

    public IReadOnlyList<TierChipVm> TypeChips
    {
        get => _typeChips;
        set => Set(ref _typeChips, value);
    }

    public IReadOnlyList<TierChipVm> RarityChips
    {
        get => _rarityChips;
        set => Set(ref _rarityChips, value);
    }

    /// <summary>The "협동 카드" chip (empty when the pool has no co-op card).</summary>
    public IReadOnlyList<TierChipVm> OptionChips
    {
        get => _optionChips;
        set => Set(ref _optionChips, value);
    }

    public IReadOnlyList<TierGroupVm> Groups
    {
        get => _groups;
        set => Set(ref _groups, value);
    }

    /// <summary>Why there is no list (pack failed to load, no tier data); null when there is one.</summary>
    public string? EmptyText
    {
        get => _emptyText;
        set
        {
            if (Set(ref _emptyText, value)) Raise(nameof(HasContent));
        }
    }

    public bool HasContent => _emptyText is null;

    /// <summary>"87장 중 40장 표시".</summary>
    public string SummaryText
    {
        get => _summaryText;
        set => Set(ref _summaryText, value);
    }

    /// <summary>"조건에 맞는 카드가 없습니다" when the filters leave nothing; null otherwise.</summary>
    public string? NoMatchText
    {
        get => _noMatchText;
        set => Set(ref _noMatchText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) SearchChanged?.Invoke();
        }
    }

    /// <summary>The ✕ in the search box.</summary>
    public ICommand ClearSearchCommand { get; }

    public TierListVm()
    {
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
    }
}
