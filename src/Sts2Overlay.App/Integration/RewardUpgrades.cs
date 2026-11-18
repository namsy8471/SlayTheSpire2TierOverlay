using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// A card reward's upgrade is read from the title colour (upgraded titles are green). That reading slips now and then:
/// the third card's first frames while it slides in read as not upgraded, and a glow or tint over a title can read as
/// green (decision logs vs. run histories, 27 runs: 6 combat reward cards kept a "+" they did not have, out of about
/// 1,300 reward cards). Where the tracker replays the screen's rolls from the run seed, its upgrade levels replace the
/// colour reading.
/// </summary>
public static class RewardUpgrades
{
    private static string? s_lastLogged;

    /// <summary>The result with each card's upgrade set to <paramref name="levels"/> where it lists the card.</summary>
    public static RecognitionResult Apply(RecognitionResult r, IReadOnlyDictionary<string, int>? levels)
    {
        if (levels is null || r.Cards.Count == 0) return r;
        bool changed = false;
        var cards = r.Cards.Select(c =>
        {
            if (!levels.TryGetValue(c.Card.Id, out int level) || level == c.Card.Upgrade) return c;
            changed = true;
            return c with { Card = c.Card with { Upgrade = level } };
        }).ToList();
        if (!changed) return r;
        string key = string.Join(",", r.Cards.Select(c => c.Card)) + " -> " + string.Join(",", cards.Select(c => c.Card));
        if (key != s_lastLogged)
        {
            s_lastLogged = key;
            AppLog.Info($"reward upgrades: title colour {key.Replace(" -> ", ", replay ")}");
        }
        return r with { Cards = cards };
    }
}
