namespace Sts2Overlay.Core.Data;

/// <summary>
/// <c>events.stats.json</c> (optional): how often top players chose each event option and each ancient option
/// (docs/DATA_SCHEMA.md, docs/research/v2-stats/events.md). <see cref="Events.EventScorer"/> blends these rates into the
/// option scores; without the file the scores are exactly the effect formula.
/// <code>
/// { "version": 1, "source": "opgg-top-a10-2026-11",
///   "events":   { "ABYSSAL_BATHS": { "INITIAL": { "k": 2, "options": { "ABSTAIN": { "choiceRate": 0.648, "byAct": { "3": 0.7 } } } } } },
///   "ancients": { "NEOW": { "LEAD_PAPERWEIGHT": { "pickRate": 0.62 } } } }
/// </code>
/// Event ids, pages and options are the three parts of the option key <c>ABYSSAL_BATHS.pages.INITIAL.options.ABSTAIN</c>.
/// Ancient option ids are the last part of <c>NEOW.pages.INITIAL.options.LEAD_PAPERWEIGHT</c> (the relic id without
/// <c>RELIC.</c>, as <c>NeowOptions</c> builds the keys). Every other field (n, players, bootstrap shares) is evidence
/// and ignored at runtime.
/// </summary>
public sealed class EventStats
{
    /// <summary>How many options an ancient shows at once when the offered set is unknown (neutral rate 1/3).</summary>
    public const int DefaultAncientOffered = 3;

    public int Version { get; set; } = 1;

    public string? Source { get; set; }

    /// <summary>Event id (without <c>EVENT.</c>) → page → page stats.</summary>
    public Dictionary<string, Dictionary<string, EventPageStats>> Events { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Ancient id (without <c>EVENT.</c>) → option id → pick rate when offered.</summary>
    public Dictionary<string, Dictionary<string, AncientOptionStats>> Ancients { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The measured rate of an option key and the rate it would have with no preference, or null when the option has no
    /// stats. Events: <c>byAct[act]</c> if present, else <c>choiceRate</c>, neutral <c>1/k</c>. Ancients (page
    /// <c>INITIAL</c>, not in <see cref="Events"/>): <c>pickRate</c>, neutral <c>1/offeredCount</c>.
    /// </summary>
    /// <param name="optionKey">E.g. <c>ABYSSAL_BATHS.pages.INITIAL.options.ABSTAIN</c>.</param>
    /// <param name="act">The current act (1-based).</param>
    /// <param name="offeredCount">How many options the ancient shows; null = <see cref="DefaultAncientOffered"/>.</param>
    public MeasuredChoice? Find(string optionKey, int act, int? offeredCount = null)
    {
        if (!TrySplitKey(optionKey, out var eventId, out var page, out var option)) return null;

        if (Events.TryGetValue(eventId, out var pages) && pages.TryGetValue(page, out var p)
            && p.Options.TryGetValue(option, out var o) && p.K >= 1)
        {
            return o.ByAct is not null && o.ByAct.TryGetValue(act, out var actRate)
                ? new MeasuredChoice(actRate, 1.0 / p.K, FromAct: true)
                : new MeasuredChoice(o.ChoiceRate, 1.0 / p.K, FromAct: false);
        }

        if (page == "INITIAL" && Ancients.TryGetValue(eventId, out var ancient) && ancient.TryGetValue(option, out var a))
        {
            var shown = Math.Max(1, offeredCount ?? DefaultAncientOffered);
            return new MeasuredChoice(a.PickRate, 1.0 / shown, false, FromAncient: true);
        }

        return null;
    }

    /// <summary><c>X.pages.P.options.O</c> → (X, P, O). False for any other shape.</summary>
    public static bool TrySplitKey(string key, out string eventId, out string page, out string option)
    {
        const string pages = ".pages.", options = ".options.";
        eventId = page = option = "";
        var i = key.IndexOf(pages, StringComparison.Ordinal);
        if (i <= 0) return false;
        var j = key.IndexOf(options, i + pages.Length, StringComparison.Ordinal);
        if (j < 0) return false;
        eventId = key[..i];
        page = key[(i + pages.Length)..j];
        option = key[(j + options.Length)..];
        return page.Length > 0 && option.Length > 0;
    }
}

/// <summary>One event page: <see cref="K"/> = options seen on the page in the sample (neutral rate 1/k).</summary>
public sealed class EventPageStats
{
    public int K { get; set; }

    public Dictionary<string, EventOptionStats> Options { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Share of visits to the page on which top players chose this option.</summary>
public sealed class EventOptionStats
{
    public double ChoiceRate { get; set; }

    /// <summary>Act (1-based) → rate, only where that act differs stably from the overall rate.</summary>
    public Dictionary<int, double>? ByAct { get; set; }

    public int N { get; set; }

    public int Players { get; set; }
}

/// <summary>Share of the times an ancient offered this option that top players took it.</summary>
public sealed class AncientOptionStats
{
    public double PickRate { get; set; }

    public int Offered { get; set; }

    public int Players { get; set; }
}

/// <summary>A measured choice rate and the rate of a choice made at random among the shown options.</summary>
/// <param name="Rate">Measured share, 0..1.</param>
/// <param name="Neutral">1/k for events, 1/offered for ancients.</param>
/// <param name="FromAct">True when the rate is the act-specific one.</param>
/// <param name="FromAncient">
/// True for an ancient's <c>pickRate</c>. That rate is measured on the very same pick as the option's relic in
/// <c>relics.json</c> (<c>signal: "ancient:…"</c>), so it already carries the relic's value.
/// </param>
public readonly record struct MeasuredChoice(double Rate, double Neutral, bool FromAct, bool FromAncient = false);
