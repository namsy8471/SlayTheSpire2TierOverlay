using Sts2Overlay.Core.Map;

namespace Sts2Overlay.Core.Replay;

/// <summary>Per-room numbers of one group (segment × act × room type). All HP values are fractions of max HP.</summary>
public sealed record RoomStat
{
    /// <summary><c>all</c>, <c>solo</c>, <c>coop</c>, or an ascension bucket such as <c>asc3+</c>.</summary>
    public required string Segment { get; init; }

    /// <summary>1..3, or 0 for all acts together.</summary>
    public int Act { get; init; }

    /// <summary>Save-style room type: monster, elite, unknown, boss, treasure, shop, rest_site, ancient.</summary>
    public required string Room { get; init; }

    public int N { get; init; }
    public int Runs { get; init; }

    public double DamageMean { get; init; }
    public double DamageSd { get; init; }

    /// <summary>Standard error of <see cref="DamageMean"/> (sd / √n).</summary>
    public double DamageSe { get; init; }

    public double DamageP50 { get; init; }
    public double DamageP80 { get; init; }
    public double DamageP90 { get; init; }
    public double DamageMax { get; init; }

    /// <summary>Mean of damage ÷ deck-strength factor, i.e. the loss a strength-0.5 deck would take (what <see cref="PathHpLoss"/> holds).</summary>
    public double DamageNormalizedMean { get; init; }

    /// <summary>Mean of (HP after − HP before) ÷ max HP: includes in-room healing (e.g. post-combat relics).</summary>
    public double NetHpMean { get; init; }

    public double HealMean { get; init; }
    public double GoldMean { get; init; }
    public double GoldSe { get; init; }
    public int Deaths { get; init; }
    public double DeathRate { get; init; }
    public double HpBeforeMean { get; init; }
    public double StrengthMean { get; init; }
    public double PotionsMean { get; init; }
    public double RelicsMean { get; init; }
    public double CardRewardRate { get; init; }

    /// <summary>Share of shown card rewards where a card was taken (1 − skip rate).</summary>
    public double CardPickRate { get; init; }
}

/// <summary>What "?" rooms turned into.</summary>
public sealed record UnknownOutcome(string Segment, int Act, string Resolved, int N, double Share, double DamageMean, double GoldMean);

/// <summary>Rest-site behaviour of one segment.</summary>
public sealed record RestStat
{
    public required string Segment { get; init; }
    public int Visits { get; init; }
    public IReadOnlyDictionary<string, int> Choices { get; init; } = new Dictionary<string, int>();

    /// <summary>HEAL choices that were not capped by max HP (HP after &lt; max HP): the real heal amount.</summary>
    public int HealUncappedN { get; init; }

    public double HealUncappedMean { get; init; }
    public double HealUncappedMedian { get; init; }
    public double HealUncappedMin { get; init; }
    public double HealUncappedMax { get; init; }

    /// <summary>HEAL share per HP-on-arrival bucket (&lt;40%, 40–70%, ≥70%): (visits, heals).</summary>
    public IReadOnlyList<RestBucket> ByHp { get; init; } = [];

    /// <summary>
    /// Informational: the <see cref="PathWeights.UpgradeValue"/> at which the scorer's heal-or-smith rule (heal when
    /// <c>HpValue × heal × HpWeight ≥ UpgradeValue</c>) agrees most often with the recorded HEAL/SMITH choices, and
    /// that agreement. Not applied: it describes what the players did, not what is best.
    /// </summary>
    public double UpgradeValueMatchingChoices { get; init; } = double.NaN;

    public double MatchingAgreement { get; init; } = double.NaN;

    /// <summary>Agreement of the current <see cref="PathWeights.UpgradeValue"/> with the recorded choices.</summary>
    public double CurrentAgreement { get; init; } = double.NaN;
}

public sealed record RestBucket(string Bucket, int Visits, int Heals);

/// <summary>Average outcome of one event id (local report only; ids, no game text).</summary>
public sealed record EventStat(string EventId, int N, double DamageMean, double NetHpMean, double GoldNetMean, double MaxHpNetMean, double CardsMean, double RelicsMean);

/// <summary>Co-op vs single player for one room type, on act-normalised damage (each act divided by its pooled mean).</summary>
public sealed record CoopComparison(string Room, int SoloN, int CoopN, double SoloMean, double CoopMean, double Ratio, double DiffSe, double Z, bool Material);

/// <summary>
/// OLS fit of <c>damage ÷ group mean = a + b × (0.5 − strength)</c> over fights. If the scorer's deck-strength factor
/// is right, b ≈ <see cref="PathWeights.StrengthDamageSlope"/>.
/// </summary>
public sealed record StrengthFit(int N, double StrengthMean, double StrengthSd, double Intercept, double Slope, double SlopeSe);

/// <summary>One fitted weight: prior, measured value, sample size, and the shrunk value that goes into <see cref="PathWeights"/>.</summary>
public sealed record WeightFit(string Name, double Prior, double Measured, int N, double Fitted);

public sealed record PathStatsReport
{
    public int Runs { get; init; }
    public int SoloRuns { get; init; }
    public int CoopRuns { get; init; }
    public int PlayerFloors { get; init; }
    public IReadOnlyDictionary<int, int> RunsByAscension { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<string, int> RunsByBuild { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<RoomStat> Rooms { get; init; } = [];
    public IReadOnlyList<UnknownOutcome> Unknown { get; init; } = [];
    public IReadOnlyList<RestStat> Rest { get; init; } = [];
    public IReadOnlyList<EventStat> Events { get; init; } = [];
    public IReadOnlyList<CoopComparison> Coop { get; init; } = [];
    public StrengthFit? Strength { get; init; }

    /// <summary>Losses by death room type, all segments: (room, deaths).</summary>
    public IReadOnlyDictionary<string, int> DeathsByRoom { get; init; } = new Dictionary<string, int>();

    public double PriorStrength { get; init; }
    public bool CoopSeparate { get; init; }
    public IReadOnlyList<WeightFit> Fits { get; init; } = [];

    /// <summary>The weights after fitting (only the measured fields differ from the defaults passed in).</summary>
    public PathWeights? Fitted { get; init; }

    public RoomStat? Find(string segment, int act, string room)
        => Rooms.FirstOrDefault(r => r.Segment == segment && r.Act == act && r.Room == room);
}

/// <summary>
/// Measures the path advisor's room numbers from run history (PLAN §7.7) and fits <see cref="PathWeights"/>:
/// expected HP loss per room type × act (mean, percentiles, strength-normalised), gold per room, rest-site heal,
/// death rates, what "?" rooms became, co-op vs single player.
/// </summary>
public static class PathStats
{
    public const string All = "all";
    public const string Solo = "solo";
    public const string Coop = "coop";

    private static readonly string[] Rooms = ["monster", "elite", "unknown", "boss", "treasure", "shop", "rest_site", "ancient"];
    private static readonly string[] FightRooms = ["monster", "elite", "boss"];
    private static readonly string[] LossRooms = ["monster", "elite", "unknown", "boss"];

    /// <summary>
    /// Shrinkage toward a prior: the prior counts as <paramref name="k"/> pseudo-observations, the data as
    /// <paramref name="n"/> real ones. <c>(k × prior + n × measured) / (k + n)</c>. With n = 0 this is the prior; as
    /// n grows the measurement takes over. This is the posterior mean of a normal model with a normal prior whose
    /// variance is (data variance ÷ k).
    /// </summary>
    public static double Shrink(double prior, double measured, int n, double k)
    {
        if (n <= 0 || !double.IsFinite(measured)) return prior;
        if (k <= 0) return measured;
        return (k * prior + n * measured) / (k + n);
    }

    /// <summary>Linear-interpolated percentile (q in 0..1) of an already sorted list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var pos = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static string RoomName(MapNodeType t) => t switch
    {
        MapNodeType.Monster => "monster",
        MapNodeType.Elite => "elite",
        MapNodeType.RestSite => "rest_site",
        MapNodeType.Shop => "shop",
        MapNodeType.Treasure => "treasure",
        MapNodeType.Ancient => "ancient",
        MapNodeType.Boss => "boss",
        _ => "unknown",
    };

    public static string AscensionBucket(int a) => a switch { <= 0 => "asc0", <= 2 => "asc1-2", _ => "asc3+" };

    /// <summary>
    /// Computes every table and fits the weights.
    /// </summary>
    /// <param name="floors">Rows from <see cref="PathFloorExtractor.Extract"/> of distinct runs.</param>
    /// <param name="prior">Current weights; the HP table, heal and gold values act as priors. Other fields are copied.</param>
    /// <param name="legacyLoss">Prior for the HP tables (defaults to <see cref="PathHpLoss.Legacy"/>).</param>
    /// <param name="k">Prior strength in pseudo-observations.</param>
    /// <param name="strengthSlope">
    /// Deck-strength slope to normalise with and to put into the result. Null = fit it: the measured slope
    /// (<see cref="StrengthFit"/>) combined with the prior's <see cref="PathWeights.StrengthDamageSlope"/> (standard
    /// deviation <see cref="SlopePriorSd"/>) by precision weighting, clamped to 0..2.
    /// </param>
    public static PathStatsReport Compute(IReadOnlyList<PathFloor> floors, PathWeights prior, PathHpLoss? legacyLoss = null, double k = 8,
        double? strengthSlope = null)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(prior);
        legacyLoss ??= PathHpLoss.Legacy();

        var strengthFit = FitStrength(floors);
        var slope = strengthSlope ?? CombineSlope(prior.StrengthDamageSlope, strengthFit);
        var priorSlope = prior.StrengthDamageSlope;
        prior = Clone(prior);
        prior.StrengthDamageSlope = slope;

        var runs = floors.GroupBy(f => f.RunKey).Select(g => g.First()).ToArray();
        var rooms = new List<RoomStat>();
        foreach (var segment in new[] { All, Solo, Coop })
        {
            var seg = floors.Where(f => InSegment(f, segment)).ToArray();
            foreach (var room in Rooms)
            {
                for (var act = 0; act <= 3; act++)
                {
                    var rows = seg.Where(f => RoomName(f.Type) == room && (act == 0 || f.Act == act)).ToArray();
                    if (rows.Length > 0) rooms.Add(Room(segment, act, room, rows, prior));
                }
            }
        }
        foreach (var bucket in new[] { "asc0", "asc1-2", "asc3+" })
        {
            var seg = floors.Where(f => AscensionBucket(f.Ascension) == bucket).ToArray();
            foreach (var room in LossRooms)
            {
                for (var act = 0; act <= 3; act++)
                {
                    var rows = seg.Where(f => RoomName(f.Type) == room && (act == 0 || f.Act == act)).ToArray();
                    if (rows.Length > 0) rooms.Add(Room(bucket, act, room, rows, prior));
                }
            }
        }

        var report = new PathStatsReport
        {
            Runs = runs.Length,
            SoloRuns = runs.Count(r => !r.Multiplayer),
            CoopRuns = runs.Count(r => r.Multiplayer),
            PlayerFloors = floors.Count,
            RunsByAscension = runs.GroupBy(r => r.Ascension).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            RunsByBuild = runs.GroupBy(r => r.BuildId).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            Rooms = rooms,
            Unknown = UnknownOutcomes(floors),
            Rest = new[] { All, Solo, Coop }.Select(s => RestStats(s, floors.Where(f => InSegment(f, s) && f.Type == MapNodeType.RestSite).ToArray(), prior)).ToArray(),
            Events = Events(floors),
            Coop = LossRooms.Select(r => CompareCoop(r, floors, prior)).ToArray(),
            Strength = strengthFit,
            // One loss per run, even when both co-op players died at the same point.
            DeathsByRoom = floors.Where(f => f.Died).Select(f => (f.RunKey, f.NodeIndex, Room: RoomName(f.Type))).Distinct()
                .GroupBy(d => d.Room).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            PriorStrength = k,
        };
        var fitted = Fit(report, prior, legacyLoss, k);
        var slopeFit = new WeightFit(nameof(PathWeights.StrengthDamageSlope), priorSlope, strengthFit?.Slope ?? double.NaN, strengthFit?.N ?? 0, slope);
        return fitted with { Fits = [slopeFit, .. fitted.Fits] };
    }

    /// <summary>How sure the old guess of <see cref="PathWeights.StrengthDamageSlope"/> was (one standard deviation).</summary>
    public const double SlopePriorSd = 0.5;

    /// <summary>
    /// Precision-weighted mean of prior and measurement (each weighted by 1 / variance), clamped to 0..2. This is
    /// the same idea as <see cref="Shrink"/>, but both sides carry an explicit uncertainty.
    /// </summary>
    public static double CombineSlope(double prior, StrengthFit? fit)
    {
        if (fit is null || !double.IsFinite(fit.Slope) || !(fit.SlopeSe > 0)) return prior;
        var wp = 1 / (SlopePriorSd * SlopePriorSd);
        var wm = 1 / (fit.SlopeSe * fit.SlopeSe);
        return Math.Round(Math.Clamp((wp * prior + wm * fit.Slope) / (wp + wm), 0, 2), 2);
    }

    private static bool InSegment(PathFloor f, string segment) => segment switch
    {
        Solo => !f.Multiplayer,
        Coop => f.Multiplayer,
        _ => true,
    };

    private static double Mean(IEnumerable<double> xs)
    {
        double sum = 0;
        var n = 0;
        foreach (var x in xs)
        {
            sum += x;
            n++;
        }
        return n == 0 ? double.NaN : sum / n;
    }

    private static double Sd(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1));
    }

    private static double Normalized(PathFloor f, PathWeights p)
        => double.IsFinite(f.DeckStrength) ? f.DamageFraction / PathScorer.StrengthDamageFactor(f.DeckStrength, p) : f.DamageFraction;

    private static RoomStat Room(string segment, int act, string room, IReadOnlyList<PathFloor> rows, PathWeights p)
    {
        var dmg = rows.Select(f => f.DamageFraction).Order().ToArray();
        var gold = rows.Select(f => (double)f.GoldGained).ToArray();
        var sd = Sd(dmg);
        var rewards = rows.Count(f => f.CardReward);
        var deaths = rows.Count(f => f.Died);
        var strengths = rows.Select(f => f.DeckStrength).Where(double.IsFinite).ToArray();
        return new RoomStat
        {
            Segment = segment,
            Act = act,
            Room = room,
            N = rows.Count,
            Runs = rows.Select(f => f.RunKey).Distinct().Count(),
            DamageMean = dmg.Average(),
            DamageSd = sd,
            DamageSe = sd / Math.Sqrt(rows.Count),
            DamageP50 = Percentile(dmg, 0.5),
            DamageP80 = Percentile(dmg, 0.8),
            DamageP90 = Percentile(dmg, 0.9),
            DamageMax = dmg[^1],
            DamageNormalizedMean = Mean(rows.Select(f => Normalized(f, p))),
            NetHpMean = Mean(rows.Select(f => (f.HpAfter - f.HpBefore) / (double)Math.Max(1, f.MaxHpBefore))),
            HealMean = Mean(rows.Select(f => f.HealFraction)),
            GoldMean = gold.Average(),
            GoldSe = Sd(gold) / Math.Sqrt(gold.Length),
            Deaths = deaths,
            DeathRate = deaths / (double)rows.Count,
            HpBeforeMean = Mean(rows.Select(f => f.HpRatioBefore)),
            StrengthMean = strengths.Length == 0 ? double.NaN : strengths.Average(),
            PotionsMean = Mean(rows.Select(f => (double)f.PotionsGained)),
            RelicsMean = Mean(rows.Select(f => (double)f.RelicsGained)),
            CardRewardRate = rewards / (double)rows.Count,
            CardPickRate = rewards == 0 ? double.NaN : rows.Count(f => f.CardPicked) / (double)rewards,
        };
    }

    private static IReadOnlyList<UnknownOutcome> UnknownOutcomes(IReadOnlyList<PathFloor> floors)
    {
        var result = new List<UnknownOutcome>();
        foreach (var segment in new[] { All, Solo, Coop })
        {
            for (var act = 0; act <= 3; act++)
            {
                // One row per run and point: in co-op both players share the outcome of a "?" room.
                var points = floors.Where(f => f.Type == MapNodeType.Unknown && InSegment(f, segment) && (act == 0 || f.Act == act))
                    .GroupBy(f => (f.RunKey, f.NodeIndex)).ToArray();
                if (points.Length == 0) continue;
                foreach (var g in points.GroupBy(p => p.First().Resolved).OrderByDescending(g => g.Count()))
                {
                    var rows = g.SelectMany(p => p).ToArray();
                    result.Add(new UnknownOutcome(segment, act, g.Key, g.Count(), g.Count() / (double)points.Length,
                        rows.Average(f => f.DamageFraction), rows.Average(f => (double)f.GoldGained)));
                }
            }
        }
        return result;
    }

    private static RestStat RestStats(string segment, IReadOnlyList<PathFloor> rows, PathWeights p)
    {
        var choices = rows.SelectMany(f => f.RestChoices).GroupBy(c => c).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        // A heal is "uncapped" when it did not fill HP to max: then hp_healed is the full heal amount.
        var uncapped = rows.Where(f => f.RestChoices.Contains("HEAL") && f.HpAfter < f.MaxHpAfter && f.HpHealed > 0)
            .Select(f => f.HealFraction).ToArray();
        // Heal-or-smith rule of the scorer vs the recorded choice (visits with exactly one of HEAL / SMITH).
        var decided = rows.Where(f => f.RestChoices.Contains("HEAL") != f.RestChoices.Contains("SMITH"))
            .Select(f => (Heal: f.RestChoices.Contains("HEAL"),
                Value: p.HpValue * Math.Min(p.RestHeal, 1 - f.HpRatioBefore) * (1 + p.LowHpValueBoost * (1 - f.HpRatioBefore))))
            .ToArray();
        double Agreement(double u) => decided.Length == 0 ? double.NaN : decided.Count(d => (d.Value >= u) == d.Heal) / (double)decided.Length;
        var bestU = double.NaN;
        var bestAgreement = -1.0;
        for (var u = 0.0; u <= 30.0001; u += 0.5)
        {
            var a = Agreement(u);
            if (a > bestAgreement)
            {
                bestAgreement = a;
                bestU = u;
            }
        }
        RestBucket Bucket(string name, double lo, double hi)
        {
            var b = rows.Where(f => f.HpRatioBefore >= lo && f.HpRatioBefore < hi).ToArray();
            return new RestBucket(name, b.Length, b.Count(f => f.RestChoices.Contains("HEAL")));
        }
        return new RestStat
        {
            Segment = segment,
            Visits = rows.Count,
            Choices = choices,
            HealUncappedN = uncapped.Length,
            HealUncappedMean = uncapped.Length == 0 ? double.NaN : uncapped.Average(),
            HealUncappedMedian = Percentile(uncapped.Order().ToArray(), 0.5),
            HealUncappedMin = uncapped.Length == 0 ? double.NaN : uncapped.Min(),
            HealUncappedMax = uncapped.Length == 0 ? double.NaN : uncapped.Max(),
            ByHp = [Bucket("<40%", 0, 0.4), Bucket("40-70%", 0.4, 0.7), Bucket(">=70%", 0.7, 1.01)],
            UpgradeValueMatchingChoices = decided.Length == 0 ? double.NaN : bestU,
            MatchingAgreement = decided.Length == 0 ? double.NaN : bestAgreement,
            CurrentAgreement = Agreement(p.UpgradeValue),
        };
    }

    private static IReadOnlyList<EventStat> Events(IReadOnlyList<PathFloor> floors)
        => floors.Where(f => f.Type == MapNodeType.Unknown && f.EventId is not null)
            .GroupBy(f => f.EventId!)
            .Select(g => new EventStat(g.Key, g.Count(),
                g.Average(f => f.DamageFraction),
                g.Average(f => (f.HpAfter - f.HpBefore) / (double)Math.Max(1, f.MaxHpBefore)),
                g.Average(f => (double)(f.GoldAfter - f.GoldBefore)),
                g.Average(f => (double)(f.MaxHpAfter - f.MaxHpBefore)),
                g.Average(f => (double)f.CardsGained),
                g.Average(f => (double)f.RelicsGained)))
            .OrderByDescending(e => e.N).ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Each row's normalised damage is divided by the pooled (all players) mean of its act, so acts can be combined;
    /// then the co-op and solo means are compared with a Welch z. "Material" = |z| ≥ 2 and at least 15% apart.
    /// </summary>
    private static CoopComparison CompareCoop(string room, IReadOnlyList<PathFloor> floors, PathWeights p)
    {
        var rows = floors.Where(f => RoomName(f.Type) == room && f.Act is >= 1 and <= 3).ToArray();
        var actMean = rows.GroupBy(f => f.Act).ToDictionary(g => g.Key, g => g.Average(f => Normalized(f, p)));
        double Rel(PathFloor f) => actMean[f.Act] > 0 ? Normalized(f, p) / actMean[f.Act] : 1;
        var solo = rows.Where(f => !f.Multiplayer).Select(Rel).ToArray();
        var coop = rows.Where(f => f.Multiplayer).Select(Rel).ToArray();
        if (solo.Length < 2 || coop.Length < 2) return new CoopComparison(room, solo.Length, coop.Length, double.NaN, double.NaN, double.NaN, double.NaN, 0, false);
        var ms = solo.Average();
        var mc = coop.Average();
        var se = Math.Sqrt(Sd(solo) * Sd(solo) / solo.Length + Sd(coop) * Sd(coop) / coop.Length);
        var z = se > 0 ? (mc - ms) / se : 0;
        var ratio = ms > 0 ? mc / ms : double.NaN;
        return new CoopComparison(room, solo.Length, coop.Length, ms, mc, ratio, se, z, Math.Abs(z) >= 2 && Math.Abs(ratio - 1) >= 0.15);
    }

    private static StrengthFit? FitStrength(IReadOnlyList<PathFloor> floors)
    {
        var fights = floors.Where(f => FightRooms.Contains(RoomName(f.Type)) && double.IsFinite(f.DeckStrength) && f.Act is >= 1 and <= 3).ToArray();
        if (fights.Length < 10) return null;
        var groupMean = fights.GroupBy(f => (f.Type, f.Act, f.Multiplayer)).ToDictionary(g => g.Key, g => g.Average(f => f.DamageFraction));
        var x = fights.Select(f => 0.5 - f.DeckStrength).ToArray();
        var y = fights.Select(f => groupMean[(f.Type, f.Act, f.Multiplayer)] > 0 ? f.DamageFraction / groupMean[(f.Type, f.Act, f.Multiplayer)] : 1).ToArray();
        var mx = x.Average();
        var my = y.Average();
        var sxx = x.Sum(v => (v - mx) * (v - mx));
        if (sxx <= 0) return null;
        var b = x.Zip(y).Sum(t => (t.First - mx) * (t.Second - my)) / sxx;
        var a = my - b * mx;
        var resid = x.Zip(y).Sum(t => Math.Pow(t.Second - (a + b * t.First), 2));
        var se = Math.Sqrt(resid / Math.Max(1, fights.Length - 2) / sxx);
        var strengths = fights.Select(f => f.DeckStrength).ToArray();
        return new StrengthFit(fights.Length, strengths.Average(), Sd(strengths), a, b, se);
    }

    /// <summary>
    /// Shrinks every measured value toward its prior and writes the result into a copy of <paramref name="prior"/>.
    /// The HP tables use the strength-normalised mean, because the scorer multiplies them by the strength factor again.
    /// Co-op gets its own table only when at least one fight room differs materially (<see cref="CompareCoop"/>).
    /// </summary>
    private static PathStatsReport Fit(PathStatsReport report, PathWeights prior, PathHpLoss legacyLoss, double k)
    {
        var fits = new List<WeightFit>();
        var coopSeparate = report.Coop.Any(c => c.Material);

        PathHpLoss Table(string segment, string label)
        {
            var t = new PathHpLoss();
            foreach (var (room, type, dict) in new[]
                     {
                         ("monster", MapNodeType.Monster, t.Monster), ("elite", MapNodeType.Elite, t.Elite),
                         ("unknown", MapNodeType.Unknown, t.Unknown), ("boss", MapNodeType.Boss, t.Boss),
                     })
            {
                for (var act = 1; act <= 3; act++)
                {
                    var stat = report.Find(segment, act, room);
                    var pri = legacyLoss.For(type, act);
                    var value = Math.Round(Shrink(pri, stat?.DamageNormalizedMean ?? double.NaN, stat?.N ?? 0, k), 3);
                    dict[act] = value;
                    fits.Add(new WeightFit($"{label}.{Capitalize(room)}[{act}]", pri, stat?.DamageNormalizedMean ?? double.NaN, stat?.N ?? 0, value));
                }
            }
            return t;
        }

        var fitted = Clone(prior);
        fitted.HpLoss = Table(coopSeparate ? Solo : All, "HpLoss");
        fitted.CoopHpLoss = coopSeparate ? Table(Coop, "CoopHpLoss") : null;

        var rest = report.Rest.First(r => r.Segment == All);
        // Median, not mean: a few relics heal extra (up to 60%), the median is the plain rest-site heal.
        fitted.RestHeal = Math.Round(Shrink(prior.RestHeal, rest.HealUncappedMedian, rest.HealUncappedN, k), 2);
        fits.Add(new WeightFit(nameof(PathWeights.RestHeal), prior.RestHeal, rest.HealUncappedMedian, rest.HealUncappedN, fitted.RestHeal));

        int Gold(string name, string room, int pri)
        {
            var stat = report.Find(All, 0, room);
            var value = (int)Math.Round(Shrink(pri, stat?.GoldMean ?? double.NaN, stat?.N ?? 0, k));
            fits.Add(new WeightFit(name, pri, stat?.GoldMean ?? double.NaN, stat?.N ?? 0, value));
            return value;
        }
        fitted.MonsterGold = Gold(nameof(PathWeights.MonsterGold), "monster", prior.MonsterGold);
        fitted.EliteGold = Gold(nameof(PathWeights.EliteGold), "elite", prior.EliteGold);
        fitted.UnknownGold = Gold(nameof(PathWeights.UnknownGold), "unknown", prior.UnknownGold);
        fitted.TreasureGold = Gold(nameof(PathWeights.TreasureGold), "treasure", prior.TreasureGold);

        return report with { CoopSeparate = coopSeparate, Fits = fits, Fitted = fitted };
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Copy through JSON so dictionaries and nested tables are not shared with the original.</summary>
    private static PathWeights Clone(PathWeights p)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(p);
        var copy = System.Text.Json.JsonSerializer.Deserialize<PathWeights>(json) ?? new PathWeights();
        // Populate-handled dictionaries merge into the defaults; overwrite them so removed keys do not linger.
        copy.HpLoss = Copy(p.HpLoss);
        copy.CoopHpLoss = p.CoopHpLoss is null ? null : Copy(p.CoopHpLoss);
        return copy;

        static PathHpLoss Copy(PathHpLoss t) => new()
        {
            Monster = new(t.Monster), Elite = new(t.Elite), Unknown = new(t.Unknown), Boss = new(t.Boss),
        };
    }
}
