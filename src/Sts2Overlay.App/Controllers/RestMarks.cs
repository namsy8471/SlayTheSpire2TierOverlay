using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Rest;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Rest-site marks on the game screen (pure, unit-tested): "추천!" on the 휴식 or 재련 button the
/// <see cref="RestSiteAdvisor"/> picked, and on the Smith grid the card to upgrade (2nd and 3rd choices tagged).
/// All rectangles are in 1920x1080 design px, like the map marks.
/// </summary>
public static class RestMarks
{
    /// <summary>Marks for the campfire's option buttons. Empty when the advised button isn't on screen or is greyed out.</summary>
    public static MapMarkPlan Choice(RestSiteResult rest, RestSiteAdvice advice, Func<CardRef, string> name)
    {
        var want = advice.Action == RestAction.Heal ? RestOption.Heal : RestOption.Smith;
        if (rest.Find(want) is not { Enabled: true } button) return MapMarkPlan.Empty;

        var ribbon = advice.Confidence == RestConfidence.Close ? Loc.T("추천! (박빙)", "Pick! (close)") : Loc.T("추천!", "Pick!");
        var note = advice.Action == RestAction.Smith && advice.BestUpgrade is { } best
            ? Loc.T($"강화할 카드: {name(best.Card)}\n{advice.Reason}", $"Upgrade: {name(best.Card)}\n{advice.Reason}")
            : advice.Reason;
        // The layout slot, not the drawn picture: the picture grows by 10 % on hover, the mark should not jump.
        var s = button.Slot;
        return MapMarkPlan.Empty with { Rects = [new ScreenRectMark(s.X, s.Y, s.Width, s.Height, ScreenRectMarkKind.Best, ribbon, note)] };
    }

    /// <summary>
    /// Marks for the Smith card grid: the best upgrade ("강화 추천!") and the next two ("2순위", "3순위"), on the first
    /// visible copy of each. <paramref name="cards"/> are the recognised grid cards with their design-px rectangles.
    /// </summary>
    public static MapMarkPlan Grid(IReadOnlyList<(CardRef Card, RectF Rect)> cards, RestSiteAdvice advice, Func<CardRef, string> name)
    {
        var rects = new List<ScreenRectMark>();
        var used = new HashSet<int>();
        var top = advice.TopUpgrades(3);
        for (var rank = 0; rank < top.Count; rank++)
        {
            var want = top[rank].Card;
            var i = Enumerable.Range(0, cards.Count).FirstOrDefault(j => !used.Contains(j) && cards[j].Card == want, -1);
            if (i < 0) continue; // scrolled out of view
            used.Add(i);
            var r = cards[i].Rect;
            rects.Add(rank == 0
                ? new ScreenRectMark(r.X, r.Y, r.Width, r.Height, ScreenRectMarkKind.Best, Loc.T("강화 추천!", "Upgrade!"), Chips(top[rank]))
                : new ScreenRectMark(r.X, r.Y, r.Width, r.Height, ScreenRectMarkKind.Candidate, Loc.T($"{rank + 1}순위", $"#{rank + 1}")));
        }
        // The grid scrolls and only part of the deck is recognised at a time, so the ranking is also pinned in the empty
        // strip left of the grid (columns start at x≈250; the back button is lower, at y 715).
        if (top.Count > 0)
            rects.Add(new ScreenRectMark(NoteX, NoteY, NoteWidth, 0, ScreenRectMarkKind.Note, Loc.T("강화 추천", "Upgrade picks"),
                string.Join('\n', top.Select((u, k) => $"{k + 1}. {name(u.Card)}"))));
        return rects.Count == 0 ? MapMarkPlan.Empty : MapMarkPlan.Empty with { Rects = rects };
    }

    /// <summary>Side panel with the upgrade ranking, design px (left of the Smith grid, below the relic row).</summary>
    public const float NoteX = 15, NoteY = 185, NoteWidth = 220;

    private static string? Chips(Core.Scoring.UpgradeCandidate c) =>
        c.Chips.Count == 0 ? null : string.Join(" · ", c.Chips.Take(2).Select(ch => ch.Label));
}
