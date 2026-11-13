using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Is the deck grid on screen the "pick a card to upgrade" screen of an event option? (pure, unit-tested)
/// <para>
/// WHY: some event options upgrade a card of the player's choice (Sapphire Seed's Eat, Aroma of Chaos, Spirit Grafter,
/// the Trial's merchant, Neow's Pomander, Tezcatara's cookie). The game opens the same screen as the rest site's Smith
/// for them (<c>CardSelectCmd.FromDeckForUpgrade</c>), but the grid probe alone cannot tell that screen from the
/// transform grid or a deck view, so the upgrade marks were only trusted at rest sites. In an event room the missing
/// piece of evidence is the option that was just clicked.
/// </para>
/// <code>
///  clicked option has "upgrade a card you choose" ──▶ grid opens ──▶ marks ──▶ grid closed and the event's
///                                                                            buttons are back ──▶ over
/// </code>
/// The pick is over once the event's buttons show again after the grid was seen: a deck view opened later in the same
/// room must not get upgrade marks. Not thread-safe: fed on the UI thread.
/// </summary>
public sealed class EventUpgradePick(EventCatalog catalog)
{
    private string? _key;
    private bool _seen;
    private bool _done;

    /// <summary>
    /// True when choosing this option opens the upgrade grid: it upgrades cards the player chooses, and it has no other
    /// effect that opens a deck grid of its own (remove / transform / enchant a chosen card), which the probe could not
    /// tell apart.
    /// </summary>
    public static bool OpensUpgradeGrid(EventOptionDef option)
    {
        var chosen = option.Effects.Where(e => string.Equals(e.How, "choose", StringComparison.Ordinal)).ToList();
        return chosen.Any(e => e.Type == EventEffectTypes.CardUpgrade)
            && chosen.All(e => e.Type is not (EventEffectTypes.CardRemove or EventEffectTypes.CardTransform or EventEffectTypes.Enchant));
    }

    /// <summary>True while <paramref name="clickedKey"/>'s upgrade grid is open or still to come (keep the mark layer ready).</summary>
    public bool Expects(string? clickedKey)
        => clickedKey is not null && !(clickedKey == _key && _done)
           && catalog.FindOption(clickedKey) is { } hit && OpensUpgradeGrid(hit.Option);

    // Deck upgrade dialogs preserve the save's deck order. The baseline selects the final MaxCount
    // legal instances, including duplicate copies. It does not score their upgrade values.
    public IReadOnlyList<DeckCard> BaselineCards(string? key, RunState run, DataPack livePack, bool deckMatchesSave)
    {
        if (!deckMatchesSave || key == null || catalog.FindOption(key) is not { } hit || !OpensUpgradeGrid(hit.Option)) return [];
        var effects = hit.Option.Effects.Where(e => e.How == "choose" && e.Type == EventEffectTypes.CardUpgrade).ToList();
        if (effects.Count != 1 || effects[0].Filter != null ||
            effects[0].Count is not { ValueKind: System.Text.Json.JsonValueKind.Number } n || !n.TryGetInt32(out int count) || count < 1) return [];
        var picks = new List<DeckCard>(Math.Min(count, run.Deck.Count));
        for (int i = run.Deck.Count - 1; i >= 0 && picks.Count < count; i--)
        {
            var card = run.Deck[i];
            if (livePack.FindCatalog(card.Id)?.MaxUpgrade is not { } max) return [];
            if (card.Upgrade < max) picks.Add(card);
        }
        picks.Reverse();
        return picks;
    }

    /// <summary>
    /// One recognised frame. <paramref name="clickedKey"/>: the option clicked last in this event room (null: none, or
    /// not in an event). Returns true when the open grid is that option's upgrade pick.
    /// </summary>
    public bool Update(string? clickedKey, bool gridOpen, bool buttonsOnScreen)
    {
        if (clickedKey is null || catalog.FindOption(clickedKey) is not { } hit || !OpensUpgradeGrid(hit.Option))
        {
            _key = null;
            return false;
        }
        if (clickedKey != _key)
        {
            _key = clickedKey;
            _seen = false;
            _done = false;
        }
        if (_done) return false;
        if (gridOpen)
        {
            _seen = true;
            return true;
        }
        // The grid was up and now the event's own buttons are: the card was picked (the result page's "proceed").
        if (_seen && buttonsOnScreen) _done = true;
        return false;
    }
}
