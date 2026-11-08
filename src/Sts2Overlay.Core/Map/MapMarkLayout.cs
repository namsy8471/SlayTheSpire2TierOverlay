using System.Globalization;

namespace Sts2Overlay.Core.Map;

/// <summary>How a path segment on the map is drawn.</summary>
public enum MapLineKind
{
    /// <summary>The recommended path: bold gold.</summary>
    Best,

    /// <summary>The runner-up paths: thin and faint, only where they leave the recommended one.</summary>
    Alternative,
}

/// <summary>A path segment between two nodes, in design px on screen (scroll applied), already shortened so it doesn't cover the icons.</summary>
public sealed record MapLineMark(float X1, float Y1, float X2, float Y2, MapLineKind Kind);

/// <summary>What is pinned on one node.</summary>
public enum MapNodeMarkKind
{
    /// <summary>The node to click next: ring + "추천!" ribbon.</summary>
    Recommended,

    /// <summary>A co-op partner's current vote: "동료" tag.</summary>
    Partner,

    /// <summary>A "?" node with the odds of what it turns into (Caption).</summary>
    UnknownOdds,
}

/// <summary>A mark pinned on node <paramref name="Coord"/>, whose centre is at (X, Y) design px on screen.</summary>
public sealed record MapNodeMark(MapCoord Coord, float X, float Y, MapNodeMarkKind Kind, string? Caption);

/// <summary>How a rectangle mark is drawn.</summary>
public enum ScreenRectMarkKind
{
    /// <summary>The recommendation: gold outline + ribbon.</summary>
    Best,

    /// <summary>A runner-up: thin outline + small tag.</summary>
    Candidate,

    /// <summary>No target, just a note panel at the rectangle's top (title = Ribbon, lines = Note), e.g. a fixed side panel.</summary>
    Note,
}

/// <summary>
/// A mark around a rectangle of the game screen (design px), e.g. a rest-site button or a card in the upgrade grid:
/// <paramref name="Ribbon"/> above it, <paramref name="Note"/> (optional) in a chip below it.
/// </summary>
public sealed record ScreenRectMark(float X, float Y, float Width, float Height, ScreenRectMarkKind Kind, string Ribbon, string? Note = null);

/// <summary>
/// Everything the game-screen mark layer draws: path lines and node marks on the map screen, or rectangle marks on
/// other screens (rest site). Coordinates are 1920x1080 design px.
/// </summary>
public sealed record MapMarkPlan(IReadOnlyList<MapLineMark> Lines, IReadOnlyList<MapNodeMark> Nodes)
{
    public static MapMarkPlan Empty { get; } = new([], []);

    /// <summary>Rectangle marks (rest-site buttons, upgrade-grid cards).</summary>
    public IReadOnlyList<ScreenRectMark> Rects { get; init; } = [];

    public bool IsEmpty => Lines.Count == 0 && Nodes.Count == 0 && Rects.Count == 0;
}

/// <summary>
/// Turns the path recommendation into marks on the game's own map (pure, unit-tested). Positions come from
/// <see cref="MapScreenLayout"/> (where the game draws each node) plus the scroll the map watcher measured, in the
/// 1920x1080 design space; the window scales them to the real game size.
/// </summary>
public static class MapMarkLayout
{
    /// <summary>Visible map area in design px: below the top bar, above the bottom edge.</summary>
    public const float VisibleTop = 95, VisibleBottom = 1075;

    /// <summary>Half-size of a normal node icon (56x56 box) plus a small gap, so lines stop short of the icons.</summary>
    private const float NodeGap = 34;

    /// <param name="map">The act map.</param>
    /// <param name="layout">Where the game draws each node at scroll 0.</param>
    /// <param name="scroll">Measured map scroll (design px).</param>
    /// <param name="rec">Path recommendation for the current position, or null.</param>
    /// <param name="partnerVotes">Co-op partners' current votes.</param>
    /// <param name="odds">Odds of the next "?" room, or null.</param>
    public static MapMarkPlan Build(
        MapGraph map,
        MapScreenLayout layout,
        float scroll,
        PathRecommendation? rec,
        IReadOnlyList<MapCoord> partnerVotes,
        UnknownRoomOdds? odds)
    {
        var lines = new List<MapLineMark>();
        var nodes = new List<MapNodeMark>();

        bool At(MapCoord c, out float x, out float y)
        {
            if (layout.Centers.TryGetValue(c, out var p))
            {
                (x, y) = (p.X, p.Y + scroll);
                return true;
            }
            (x, y) = (0, 0);
            return false;
        }

        bool Visible(float y) => y >= VisibleTop && y <= VisibleBottom;

        float Radius(MapCoord c) =>
            c == map.Boss || c == map.SecondBoss ? 130
            : c == map.Start && map.Nodes.TryGetValue(c, out var s) && s.Type == MapNodeType.Ancient ? 95
            : NodeGap;

        if (rec is { IsEmpty: false })
        {
            var best = rec.Top[0];
            var bestEdges = new HashSet<(MapCoord, MapCoord)>();
            AddPath(rec.Origin, best.Coords, MapLineKind.Best, bestEdges, lines);
            foreach (var alt in rec.Top.Skip(1)) AddPath(rec.Origin, alt.Coords, MapLineKind.Alternative, bestEdges, lines);

            if (rec.NextStep is { } next && At(next, out var nx, out var ny) && Visible(ny))
                nodes.Add(new MapNodeMark(next, nx, ny, MapNodeMarkKind.Recommended, "추천!"));

            // The save's "?" odds are for the NEXT "?" room only: the immediate choices that are "?", and the first "?"
            // further along the recommended path (it is the next "?" if that path is taken).
            if (odds is not null && OddsText(odds) is { } text)
            {
                var targets = rec.NextSteps.Where(s => s.Type == MapNodeType.Unknown).Select(s => s.Coord).ToList();
                var firstOnPath = best.Coords.Select((c, i) => (c, t: best.Types[i])).FirstOrDefault(p => p.t == MapNodeType.Unknown);
                if (firstOnPath.t == MapNodeType.Unknown && !targets.Contains(firstOnPath.c)) targets.Add(firstOnPath.c);
                foreach (var c in targets)
                    if (At(c, out var ux, out var uy) && Visible(uy))
                        nodes.Add(new MapNodeMark(c, ux, uy, MapNodeMarkKind.UnknownOdds, text));
            }
        }

        foreach (var g in partnerVotes.GroupBy(c => c))
            if (At(g.Key, out var px, out var py) && Visible(py))
                nodes.Add(new MapNodeMark(g.Key, px, py, MapNodeMarkKind.Partner, g.Count() > 1 ? $"동료 ×{g.Count()}" : "동료"));

        return new MapMarkPlan(lines, nodes);

        void AddPath(MapCoord origin, IReadOnlyList<MapCoord> coords, MapLineKind kind, HashSet<(MapCoord, MapCoord)> bestEdges, List<MapLineMark> into)
        {
            var from = origin;
            foreach (var to in coords)
            {
                var edge = (from, to);
                if (kind == MapLineKind.Best) bestEdges.Add(edge);
                if ((kind == MapLineKind.Best || !bestEdges.Contains(edge)) && Segment(from, to, kind) is { } seg
                    && !into.Contains(seg))
                    into.Add(seg);
                from = to;
            }
        }

        MapLineMark? Segment(MapCoord a, MapCoord b, MapLineKind kind)
        {
            if (!At(a, out var x1, out var y1) || !At(b, out var x2, out var y2)) return null;
            if (Math.Max(y1, y2) < VisibleTop || Math.Min(y1, y2) > VisibleBottom) return null;
            float dx = x2 - x1, dy = y2 - y1, len = MathF.Sqrt(dx * dx + dy * dy);
            float ra = Radius(a), rb = Radius(b);
            if (len <= ra + rb + 4) return null;
            float ux = dx / len, uy = dy / len;
            return new MapLineMark(x1 + ux * ra, y1 + uy * ra, x2 - ux * rb, y2 - uy * rb, kind);
        }
    }

    /// <summary>"전투 10% · 상점 3% · 보물 2% · 이벤트 85%"; outcomes that can't happen (negative odds) are left out.</summary>
    public static string? OddsText(UnknownRoomOdds o)
    {
        var parts = new List<string>();
        void Add(string name, double p)
        {
            if (p > 0) parts.Add($"{name} {Pct(p)}");
        }
        Add("전투", o.Monster);
        Add("엘리트", o.Elite);
        Add("상점", o.Shop);
        Add("보물", o.Treasure);
        Add("이벤트", o.Event);
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>Whole percent, or one decimal when the value isn't whole (2.5 %), so the parts still add up to 100.</summary>
    private static string Pct(double p)
    {
        double v = p * 100;
        return v.ToString(Math.Abs(v - Math.Round(v)) > 0.05 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";
    }
}

/// <summary>
/// Chances that the NEXT "?" room is a fight / elite / shop / treasure (save <c>odds.unknown_map_point_*_odds_value</c>);
/// the rest is an event. The save holds the values for the next "?" room; the game changes them after each "?" room,
/// so they say nothing exact about later ones. A negative value (elite outside its act) means "can't happen".
/// </summary>
public sealed record UnknownRoomOdds(double Monster, double Elite, double Shop, double Treasure)
{
    public double Event => Math.Max(0, 1 - Math.Max(0, Monster) - Math.Max(0, Elite) - Math.Max(0, Shop) - Math.Max(0, Treasure));

    /// <summary>Null unless at least the monster odds are present.</summary>
    public static UnknownRoomOdds? From(IReadOnlyDictionary<string, double> odds)
    {
        double Get(string k) => odds.TryGetValue($"unknown_map_point_{k}_odds_value", out var v) ? v : -1;
        return odds.ContainsKey("unknown_map_point_monster_odds_value")
            ? new UnknownRoomOdds(Get("monster"), Get("elite"), Get("shop"), Get("treasure"))
            : null;
    }
}
