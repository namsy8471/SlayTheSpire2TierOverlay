using System.Globalization;
using System.Text.RegularExpressions;
using Sts2Overlay.Core.Map;

namespace Sts2Overlay.GameState.Log;

/// <summary>
/// Turns one godot.log line into a typed <see cref="LogEvent"/>, or null for the thousands of lines we don't need.
/// Formats were copied from the real v0.107.1 logs and cross-checked against the decompiled source.
/// </summary>
public static partial class LogLineParser
{
    // [GeneratedRegex] makes the compiler emit the matching code at build time (fast, no runtime
    // Regex construction). Patterns are anchored with ^ so most lines are rejected after one char.

    [GeneratedRegex(@"^\[(?:INFO|DEBUG|VERYDEBUG|WARN|ERROR)\]\s?")]
    private static partial Regex LevelPrefix();

    [GeneratedRegex(@"^Embarking on a (?:(?<mode>CUSTOM|DAILY) )?(?:singleplayer )?(?<ch>[A-Z0-9_]+) run(?: with (?<n>\d+) players)?\. Ascension: (?<asc>\d+) Seed: (?<seed>\S+)")]
    private static partial Regex EmbarkSingle();

    [GeneratedRegex(@"^Embarking on a (?:(?<mode>CUSTOM|DAILY) )?multiplayer run\. Players: (?<players>.*)\. Ascension: (?<asc>\d+) Seed: (?<seed>\S+)")]
    private static partial Regex EmbarkMulti();

    [GeneratedRegex(@"Player (?<id>\d+), (?<ch>[A-Z0-9_]+)")]
    private static partial Regex LobbyPlayerRx();

    [GeneratedRegex(@"^Continuing run with character: (?<ch>CHARACTER\.[A-Z0-9_]+)")]
    private static partial Regex Continue();

    [GeneratedRegex(@"Our player id: (?<id>\d+)|Local player (?<id>\d+) is ready")]
    private static partial Regex LocalId();

    [GeneratedRegex(@"^Profile-scoped data path initialized: user://steam/(?<id>\d+)/(?<profile>profile\d+)?")]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"^Player vote changed for (?<p>\d+): .*->MapVote \(gen: (?<gen>\d+) coord: \((?<col>\d+), (?<row>\d+)\)\)")]
    private static partial Regex Vote();

    [GeneratedRegex(@"^Preloading '(?<name>[^']+)' assets\.\.\.")]
    private static partial Regex Preload();

    [GeneratedRegex(@"^Creating NCombatRoom with mode=(?<mode>\w+) encounter=(?<enc>[A-Z0-9_]+)\.")]
    private static partial Regex CombatRoom();

    [GeneratedRegex(@"^(?<ch>CHARACTER\.[A-Z0-9_]+) has (?<res>won against|lost to) encounter (?<enc>ENCOUNTER\.[A-Z0-9_]+)\.")]
    private static partial Regex CombatResult();

    [GeneratedRegex(@"^(?<ch>CHARACTER\.[A-Z0-9_]+) fought (?<enc>ENCOUNTER\.[A-Z0-9_]+) for the first time and (?<res>WON|LOST)")]
    private static partial Regex CombatFirstTime();

    [GeneratedRegex(@"^(?<ch>CHARACTER\.[A-Z0-9_]+) has won a run")]
    private static partial Regex RunWon();

    [GeneratedRegex(@"^Card rarity: Rolled (?<roll>\S+), need < (?<need>\S+) for rare \(offset = (?<off>[^)]+)\)")]
    private static partial Regex Rarity();

    // "Player 1 obtained CARD.X from card reward" / old builds: "Obtained CARD.X from card reward".
    [GeneratedRegex(@"^(?:Player (?<p>\d+) obtained|Obtained) (?<what>.+?) from (?<src>special card reward|card reward|potion reward|relic reward|gold reward|reward)$")]
    private static partial Regex Obtained();

    [GeneratedRegex(@"^Player (?<p>\d+) chose cards \[(?<cards>[^\]]*)\]")]
    private static partial Regex Chose();

    [GeneratedRegex(@"^Player (?<p>\d+) (?<verb>using|discarding) potion (?<id>[A-Z0-9_]+)")]
    private static partial Regex PotionUse();

    [GeneratedRegex(@"\[RestSiteSynchronizer\] Rest site option index \d+ chosen for player (?<p>\d+) with success (?<ok>True|False)\. Option: (?<opt>\w+)")]
    private static partial Regex RestOption();

    // Co-op Debug only, local player's own pick from a treasure chest (single player logs nothing for chests).
    [GeneratedRegex(@"\[TreasureRoomRelicSynchronizer\] Relic index \d+ \((?<id>RELIC\.[A-Z0-9_]+) \(\d+\)\) is being picked by local player (?<p>\d+)")]
    private static partial Regex TreasurePick();

    [GeneratedRegex(@"^\[EventSynchronizer\] Option index \d+ chosen for player (?<p>\d+) in event (?<ev>EVENT\.[A-Z0-9_]+)\. Choice key: (?<key>\S+)")]
    private static partial Regex EventOption();

    [GeneratedRegex(@"^\[EventSynchronizer\] Event (?<ev>EVENT\.[A-Z0-9_]+) began for player (?<p>\d+) with options: ")]
    private static partial Regex EventBegan();

    [GeneratedRegex(@"textKey: (?<key>[A-Za-z0-9_.]+)")]
    private static partial Regex TextKey();

    [GeneratedRegex(@"^\[ActionQueueSet\] Enqueueing action PlayCardAction card: (?<card>CARD\.[A-Z0-9_]+) \(\d+\) index: (?<idx>\d+) targetid: \S* ?to player queue owned by (?<p>\d+)")]
    private static partial Regex PlayCard();

    [GeneratedRegex(@"^\[RunLocationTargetedMessageBuffer\] Handling map-targeted message RewardObtainedMessage type: (?:Card|Relic) .*? skipped: False (?:Card|Relic): (?<id>(?:CARD|RELIC)\.[A-Z0-9_]+) \(\d+\) from (?<p>\d+)")]
    private static partial Regex RemotePurchase();

    private const string HopperSteal = "Monster THIEVING_HOPPER performing move THIEVERY_MOVE";

    [GeneratedRegex(@"^Asset not cached: res://scenes/screens/card_selection/(?<screen>[a-z_]+)\.tscn")]
    private static partial Regex SelectScreen();

    [GeneratedRegex(@"^Wrote (?<bytes>\d+) bytes to path=(?<path>.+?) save_dir=")]
    private static partial Regex Wrote();

    [GeneratedRegex(@"^Saved run history: (?<file>\S+)")]
    private static partial Regex HistorySaved();

    [GeneratedRegex(@"^(?:\[Sentry\.NET\] Initialized: .*\brelease=|Release Version: )(?<v>v?\d+(?:\.\d+)+\S*)")]
    private static partial Regex ReleaseVersion();

    public static LogEvent? Parse(string rawLine)
    {
        if (rawLine.Length < 10) return null;
        // Strip "[INFO] " etc. Lines without a level (raw Godot output, stack traces) are kept as-is
        // and simply fail every pattern below.
        string line = LevelPrefix().Replace(rawLine, "", 1).TrimEnd('\r');

        // Cheap first-character dispatch keeps the hot path (thousands of combat lines) fast.
        Match m;
        switch (line[0])
        {
            case 'C':
                if ((m = Rarity().Match(line)).Success)
                {
                    return TryDouble(m, "roll", out double roll) && TryDouble(m, "need", out double need) && TryDouble(m, "off", out double off)
                        ? new CardRarityRolledEvent(roll, need, off) { RollFloat = ParseFloat(m.Groups["roll"].Value) }
                        : null;
                }
                if ((m = CombatRoom().Match(line)).Success)
                {
                    string enc = "ENCOUNTER." + m.Groups["enc"].Value;
                    return new CombatStartedEvent(enc, KindOf(enc), VisualOnly: m.Groups["mode"].Value == "VisualOnly");
                }
                if ((m = CombatResult().Match(line)).Success)
                {
                    string enc = m.Groups["enc"].Value, ch = m.Groups["ch"].Value;
                    return m.Groups["res"].Value == "won against"
                        ? new CombatWonEvent(ch, enc, KindOf(enc))
                        : new CombatLostEvent(ch, enc, KindOf(enc));
                }
                if ((m = CombatFirstTime().Match(line)).Success)
                {
                    string enc = m.Groups["enc"].Value, ch = m.Groups["ch"].Value;
                    return m.Groups["res"].Value == "WON"
                        ? new CombatWonEvent(ch, enc, KindOf(enc))
                        : new CombatLostEvent(ch, enc, KindOf(enc));
                }
                if ((m = RunWon().Match(line)).Success) return new RunWonEvent(m.Groups["ch"].Value);
                if ((m = Continue().Match(line)).Success) return new RunContinuedEvent(m.Groups["ch"].Value);
                return null;

            case 'P':
                if ((m = Obtained().Match(line)).Success) return ParseObtained(m);
                if ((m = Vote().Match(line)).Success)
                {
                    return new MapVoteEvent(ULong(m, "p"), Int(m, "gen"), new MapCoord(Int(m, "col"), Int(m, "row")));
                }
                if ((m = Preload().Match(line)).Success) return ParsePreload(m.Groups["name"].Value);
                if ((m = Chose().Match(line)).Success)
                {
                    var cards = m.Groups["cards"].Value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(c => "CARD." + c) // the log prints Id.Entry, the save uses the full id
                        .ToList();
                    return new CardsChosenEvent(ULong(m, "p"), cards);
                }
                if ((m = PotionUse().Match(line)).Success)
                    return new PotionUsedEvent(ULong(m, "p"), "POTION." + m.Groups["id"].Value, m.Groups["verb"].Value == "discarding");
                if ((m = ProfilePath().Match(line)).Success)
                    return new LocalPlayerIdEvent(ULong(m, "id"), FromProfilePath: true, m.Groups["profile"] is { Success: true } p ? p.Value : null);
                return null;

            case 'O':
                return (m = Obtained().Match(line)).Success ? ParseObtained(m) : null;

            case 'E':
                if ((m = EmbarkMulti().Match(line)).Success)
                {
                    var players = LobbyPlayerRx().Matches(m.Groups["players"].Value)
                        .Select(p => new LobbyPlayer(ulong.Parse(p.Groups["id"].Value, CultureInfo.InvariantCulture), "CHARACTER." + p.Groups["ch"].Value))
                        .ToList();
                    return new RunStartedEvent(true, null, Int(m, "asc"), m.Groups["seed"].Value, players, ModeOf(m));
                }
                if ((m = EmbarkSingle().Match(line)).Success)
                {
                    bool multi = m.Groups["n"].Success && Int(m, "n") > 1; // "DAILY X run with 2 players"
                    string ch = "CHARACTER." + m.Groups["ch"].Value;
                    return new RunStartedEvent(multi, ch, Int(m, "asc"), m.Groups["seed"].Value, [], ModeOf(m));
                }
                return null;

            case 'W':
                if ((m = Wrote().Match(line)).Success)
                {
                    // path=user://steam/<id>/profile1\saves\current_run.save → keep only run saves.
                    string file = m.Groups["path"].Value.Replace('\\', '/');
                    file = file[(file.LastIndexOf('/') + 1)..];
                    return file.StartsWith("current_run", StringComparison.Ordinal) ? new RunSaveWrittenEvent(file, long.Parse(m.Groups["bytes"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture)) : null;
                }
                return null;

            case 'S':
                return (m = HistorySaved().Match(line)).Success ? new RunHistorySavedEvent(m.Groups["file"].Value) : null;

            case 'M':
                // Thousands of monster-move lines per run: only the deck-changing one becomes an event.
                return line.StartsWith(HopperSteal, StringComparison.Ordinal) ? new MonsterMoveEvent("MONSTER.THIEVING_HOPPER", "THIEVERY_MOVE") : null;

            case 'A':
                if ((m = SelectScreen().Match(line)).Success) return new CardSelectScreenEvent(m.Groups["screen"].Value);
                return line.StartsWith("Abandoning an in-progress run", StringComparison.Ordinal) ? new RunAbandonedEvent() : null;

            case 'R':
                return (m = ReleaseVersion().Match(line)).Success ? new GameVersionEvent(m.Groups["v"].Value) : null;

            case '[':
                if (line.StartsWith("[Sentry.NET]", StringComparison.Ordinal))
                    return (m = ReleaseVersion().Match(line)).Success ? new GameVersionEvent(m.Groups["v"].Value) : null;
                if ((m = PlayCard().Match(line)).Success)
                    return new CardPlayedEvent(ULong(m, "p"), m.Groups["card"].Value, Int(m, "idx"));
                if ((m = EventOption().Match(line)).Success)
                    return new EventOptionChosenEvent(ULong(m, "p"), m.Groups["ev"].Value, m.Groups["key"].Value);
                if ((m = EventBegan().Match(line)).Success)
                {
                    var keys = TextKey().Matches(line, m.Length).Select(k => k.Groups["key"].Value).ToList();
                    return new EventStartedEvent(ULong(m, "p"), m.Groups["ev"].Value, keys);
                }
                if ((m = RemotePurchase().Match(line)).Success)
                    return new RemoteShopPurchaseEvent(ULong(m, "p"), m.Groups["id"].Value);
                if ((m = RestOption().Match(line)).Success)
                    return new RestSiteOptionEvent(ULong(m, "p"), m.Groups["opt"].Value, m.Groups["ok"].Value == "True");
                if ((m = TreasurePick().Match(line)).Success) return new RelicObtainedEvent(ULong(m, "p"), m.Groups["id"].Value);
                if ((m = LocalId().Match(line)).Success) return new LocalPlayerIdEvent(ULong(m, "id"), FromProfilePath: false);
                return null;

            default:
                return null;
        }
    }

    /// <summary>Encounter id suffix → combat kind. Event fights (<c>..._EVENT_ENCOUNTER</c>) and oddities → Unknown.</summary>
    public static CombatKind KindOf(string encounterId) =>
        encounterId.EndsWith("_BOSS", StringComparison.Ordinal) ? CombatKind.Boss
        : encounterId.EndsWith("_ELITE", StringComparison.Ordinal) ? CombatKind.Elite
        : encounterId.EndsWith("_WEAK", StringComparison.Ordinal) || encounterId.EndsWith("_NORMAL", StringComparison.Ordinal) ? CombatKind.Normal
        : CombatKind.Unknown;

    private static LogEvent? ParsePreload(string name)
    {
        if (name.StartsWith("Act=", StringComparison.Ordinal)) return new ActEnteredEvent("ACT." + name[4..]);
        if (name == "MainMenuEssentials") return new MainMenuEvent();
        if (!name.EndsWith(" Room", StringComparison.Ordinal)) return null; // "Common", "characters=...", ...
        RoomKind kind = name switch
        {
            "Combat Room" => RoomKind.Combat,
            "Merchant Room" => RoomKind.Merchant,
            "RestSite Room" => RoomKind.RestSite,
            "Event Room" => RoomKind.Event,
            "Treasure Room" => RoomKind.Treasure,
            _ => RoomKind.Other,
        };
        return new RoomEnteredEvent(kind, name);
    }

    private static LogEvent? ParseObtained(Match m)
    {
        ulong? player = m.Groups["p"].Success ? ULong(m, "p") : null;
        string what = m.Groups["what"].Value, src = m.Groups["src"].Value;
        if (what.StartsWith("CARD.", StringComparison.Ordinal)) return new CardObtainedEvent(player, what, Special: src == "special card reward");
        if (what.StartsWith("POTION.", StringComparison.Ordinal)) return new PotionObtainedEvent(player, what);
        if (what.StartsWith("RELIC.", StringComparison.Ordinal)) return new RelicObtainedEvent(player, what);
        if (what == "card removal") return player is { } p ? new CardRemovalRewardEvent(p) : null;
        if (what.EndsWith(" gold", StringComparison.Ordinal)
            && int.TryParse(what.AsSpan(0, what.Length - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gold))
            return new GoldObtainedEvent(player, gold);
        return null;
    }

    private static string ModeOf(Match m) => m.Groups["mode"].Success ? m.Groups["mode"].Value.ToLowerInvariant() : "standard";

    private static int Int(Match m, string g) => int.Parse(m.Groups[g].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static ulong ULong(Match m, string g) => ulong.Parse(m.Groups[g].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);

    // NumberStyles.Float accepts "1E-05" too, which .NET prints for tiny floats.
    private static bool TryDouble(Match m, string g, out double v) =>
        double.TryParse(m.Groups[g].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
}
