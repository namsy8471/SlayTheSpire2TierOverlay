using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// 티어표 tab: the whole card tier list of one pool (a character or colourless), with type / rarity / co-op filters and a
/// name search. Built lazily: nothing is made until the tab is shown, and then only again when the pool, a filter, the
/// language or the pack changes. Game-state snapshots (one per log line) only update the owned marks and the act marker of
/// the tiles already made. The tab is never selected automatically.
/// </summary>
public sealed partial class AppController
{
    private string _tierPool = TierListPresenter.Ironclad;
    private string? _tierRunCharacter;
    private bool _tierMultiplayer;
    private bool? _tierCoopChoice; // the "협동 카드" chip's override; null = follow the run type
    private readonly HashSet<string> _tierHiddenTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tierHiddenRarities = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int> _tierOwned = new Dictionary<string, int>(StringComparer.Ordinal);
    private int? _tierAct;

    /// <summary>Cards of <see cref="_tierPool"/> and their tiles, valid for <see cref="_tierCacheKey"/> (pool, language, pack).</summary>
    private IReadOnlyList<TierListEntry> _tierEntries = [];
    private readonly Dictionary<string, (TierListEntry Entry, TierCardVm Vm)> _tierTiles = new(StringComparer.Ordinal);
    private string? _tierCacheKey;

    /// <summary>The tab must be rebuilt the next time it is shown.</summary>
    private bool _tiersDirty = true;

    private void InitTiers()
    {
        _tierPool = TierListPresenter.DefaultPool(_settings.TierCharacter);
        _vm.Tiers.SearchChanged += () => InvalidateTiers(entries: false);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.SelectedTab)) RefreshTiers();
        };
        // Names, chips and tooltips are baked in the language of the moment; the pack and the local names can be reloaded.
        Loc.Changed += () => _ui.BeginInvoke(() => InvalidateTiers(entries: true));
        PackReloaded += () => InvalidateTiers(entries: true);
    }

    /// <summary>
    /// Snapshot stage: the run's character becomes the selected pool when a run (or another character) starts, and the
    /// owned marks / act markers follow the deck. Cheap when nothing changed.
    /// </summary>
    private void UpdateTiers(GameStateSnapshot snap)
    {
        var run = snap.Run;
        if (run?.Character != _tierRunCharacter)
        {
            _tierRunCharacter = run?.Character;
            if (TierListPresenter.IsPool(run?.Character) && run!.Character != _tierPool)
            {
                _tierPool = run.Character;
                _tiersDirty = true;
            }
        }
        var multiplayer = run?.IsMultiplayer == true;
        if (multiplayer != _tierMultiplayer)
        {
            _tierMultiplayer = multiplayer;
            _tierCoopChoice = null; // a new kind of run starts with its own default
            _tiersDirty = true;
        }

        var owned = TierListPresenter.OwnedCounts(run, _engine.Pack.Canonical);
        var act = run?.Act;
        var marksChanged = act != _tierAct || !TierListPresenter.SameCounts(owned, _tierOwned);
        _tierOwned = owned;
        _tierAct = act;
        if (_tiersDirty) RefreshTiers();
        else if (marksChanged) ApplyTierMarks();
    }

    /// <param name="entries">The card list itself is stale (pool, language or pack changed), not just the filters.</param>
    private void InvalidateTiers(bool entries)
    {
        if (entries) _tierCacheKey = null;
        _tiersDirty = true;
        RefreshTiers();
    }

    /// <summary>Builds the tab if it is showing and something changed since the last build.</summary>
    private void RefreshTiers()
    {
        if (!_tiersDirty || _vm.SelectedTab != PanelTab.Tiers) return;
        _tiersDirty = false;
        try
        {
            BuildTiers();
        }
        catch (Exception ex)
        {
            AppLog.Error("tier list build failed", ex);
        }
    }

    private void BuildTiers()
    {
        var t = _vm.Tiers;
        var pack = _engine.Pack;
        var key = $"{_tierPool}|{Loc.CurrentCode}|{_engineVersion}";
        if (key != _tierCacheKey)
        {
            _tierEntries = TierListPresenter.Entries(pack, _tierPool);
            _tierTiles.Clear();
            _tierCacheKey = key;
        }

        t.Characters = TierListPresenter.Pools
            .Select(p => new TierChipVm(TierListPresenter.PoolName(p), p == _tierPool, new RelayCommand(_ => SelectTierPool(p))))
            .ToArray();
        if (!_packLoaded || _tierEntries.Count == 0)
        {
            t.EmptyText = _packLoaded ? TierListPresenter.NoData : PackText();
            t.Groups = [];
            t.TypeChips = t.RarityChips = t.OptionChips = [];
            t.SummaryText = "";
            t.NoMatchText = null;
            return;
        }
        t.EmptyText = null;

        t.TypeChips = TierListPresenter.TypesOf(_tierEntries)
            .Select(k => new TierChipVm(TierListPresenter.TypeName(k), !_tierHiddenTypes.Contains(k), new RelayCommand(_ => ToggleTierFilter(_tierHiddenTypes, k))))
            .ToArray();
        t.RarityChips = TierListPresenter.RaritiesOf(_tierEntries)
            .Select(k => new TierChipVm(TierListPresenter.RarityName(k), !_tierHiddenRarities.Contains(k), new RelayCommand(_ => ToggleTierFilter(_tierHiddenRarities, k))))
            .ToArray();
        var showCoop = TierListPresenter.ShowCoop(_tierMultiplayer, _tierCoopChoice);
        t.OptionChips = _tierEntries.Any(e => e.CoopOnly)
            ? [new TierChipVm(TierListPresenter.CoopChip, showCoop, new RelayCommand(_ => ToggleTierCoop(showCoop)), TierListPresenter.CoopChipToolTip)]
            : [];

        var filter = new TierListFilter(_tierHiddenTypes, _tierHiddenRarities, showCoop, t.SearchText);
        var groups = TierListPresenter.Group(_tierEntries, filter, _cards.Name);
        t.Groups = groups
            .Select(g => new TierGroupVm(g.TierText, TierListPresenter.GroupTitle(g.TierText, g.Cards.Count), g.Cards.Select(TierTile).ToArray()))
            .ToArray();
        var shown = groups.Sum(g => g.Cards.Count);
        t.SummaryText = TierListPresenter.Summary(shown, _tierEntries.Count);
        t.NoMatchText = shown == 0 ? TierListPresenter.NoMatch : null;
        ApplyTierMarks();
    }

    /// <summary>The tile of a card, made once per pool / language / pack (filters and searches reuse it).</summary>
    private TierCardVm TierTile(TierListEntry e)
    {
        if (_tierTiles.TryGetValue(e.Id, out var made)) return made.Vm;
        var lang = Loc.CurrentCode;
        var vm = new TierCardVm(() => _cardText.For(e.Id, lang))
        {
            Id = e.Id,
            Name = _cards.Name(e.Id),
            TierText = e.Tier?.ToString() ?? "?",
            CostText = TierListPresenter.CostText(e),
            IsCoop = e.CoopOnly,
            IsEstimate = e.IsEstimate,
            DetailText = TierListPresenter.DetailText(e),
            ActTiersText = TierListPresenter.ActTiersText(e),
            Tip = e.Rating?.LocalTip,
            Art = ArtOf(e.Id),
        };
        _tierTiles[e.Id] = (e, vm);
        return vm;
    }

    /// <summary>Owned counts and the current act's tier marker on every tile made so far.</summary>
    private void ApplyTierMarks()
    {
        foreach (var (entry, vm) in _tierTiles.Values)
        {
            vm.Owned = _tierOwned.GetValueOrDefault(entry.Id);
            var marker = TierListPresenter.ActMarker(entry, _tierAct);
            vm.SetActMarker(marker?.Text, marker?.Tier);
        }
    }

    private void SelectTierPool(string pool)
    {
        _settings.TierCharacter = pool; // remembered for when there is no run (saved with the other settings on exit)
        if (pool == _tierPool) return;
        _tierPool = pool;
        InvalidateTiers(entries: true);
    }

    private void ToggleTierFilter(HashSet<string> hidden, string key)
    {
        if (!hidden.Remove(key)) hidden.Add(key);
        InvalidateTiers(entries: false);
    }

    private void ToggleTierCoop(bool shownNow)
    {
        _tierCoopChoice = !shownNow;
        InvalidateTiers(entries: false);
    }
}
