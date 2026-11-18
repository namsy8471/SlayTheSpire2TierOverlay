using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// Cards without art of their own (CardModel.MissingPortraitPath; in v0.113.1 45 of The Fool's 90 cards) all show the
/// same placeholder picture, so the screen reader can only tell that such a card is there. On a card reward the
/// tracker replays the screen's rolls from the run seed (<see cref="ICardOfferSink.ReplayedRewardCards"/>): when the
/// replay explains the screen with exactly one reward (same count, and every card read from its own art in its place),
/// the placeholders take the replayed names. Anywhere else, or when no reward or more than one fits (an elite's two
/// rewards and a screen of placeholders only), they are left out.
/// </summary>
public static class MissingPortraitCards
{
    private static string? s_lastLogged;

    public static RecognitionResult Resolve(RecognitionResult r, ICardOfferSink? replay)
    {
        if (!r.Cards.Any(c => c.MissingPortrait)) return r;
        IReadOnlyList<IReadOnlyList<string>> rewards = r.ScreenKind == ScreenKind.Reward && replay is not null ? replay.ReplayedRewardCards(r.Cards.Count) : [];
        var fits = rewards.Select(names => Named(r.Cards, names)).OfType<IReadOnlyList<RecognizedCard>>()
            .DistinctBy(c => string.Join(",", c.Select(x => x.Card.Id))).ToList();
        IReadOnlyList<string>? names = fits.Count == 1 ? fits[0].Select(c => c.Card.Id).ToList() : null;
        IReadOnlyList<RecognizedCard> cards = fits.Count == 1 ? fits[0] : r.Cards.Where(c => !c.MissingPortrait).ToList();
        string key = $"{r.ScreenKind}: {string.Join(",", r.Cards.Select(c => c.MissingPortrait ? "?" : c.Card.Id))} -> {string.Join(",", cards.Select(c => c.Card.Id))}";
        if (key != s_lastLogged)
        {
            s_lastLogged = key;
            AppLog.Info($"cards without art: {key}{(names is null ? "" : " (replay " + string.Join(",", names) + ")")}");
        }
        if (cards.Count == 0) return RecognitionResult.Other with { RestSite = r.RestSite };
        return r with { Cards = cards };
    }

    /// <summary>The cards with every placeholder named from <paramref name="names"/> (screen order), or null when the
    /// replay does not explain the screen.</summary>
    internal static IReadOnlyList<RecognizedCard>? Named(IReadOnlyList<RecognizedCard> cards, IReadOnlyList<string> names)
    {
        if (names.Count != cards.Count) return null;
        var result = new List<RecognizedCard>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            RecognizedCard c = cards[i];
            if (!c.MissingPortrait)
            {
                if (c.Card.Id != names[i]) return null; // a card read from its own art sits elsewhere in the replay
                result.Add(c);
            }
            else result.Add(c with { Card = c.Card with { Id = names[i] }, MissingPortrait = false });
        }
        return result;
    }
}
