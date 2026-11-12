using System.Globalization;
using System.Text.Json;

namespace Sts2Overlay.App.Integration.CombatAdvice;

// The overlay side of the combat advisor protocol (docs/COMBAT_ADVISOR.md). The helper process prints one JSON object
// per line on stdout; each line replaces the previous one. This file only reads those lines: no game logic.

/// <summary>What the helper says about the fight.</summary>
public enum CombatAdviceState
{
    /// <summary>No fight is being followed: the section is hidden.</summary>
    Idle,

    /// <summary>A play order for the rest of this turn.</summary>
    Advice,

    /// <summary>The helper lost track of this fight (until the next idle / advice).</summary>
    Lost,

    /// <summary>The helper cannot follow this fight (until the next idle / advice).</summary>
    Unsupported,
}

/// <summary><c>kind</c> of a step. Anything else is <see cref="Unknown"/> and shown as its raw id.</summary>
public enum CombatStepKind
{
    Card,
    Potion,
    Unknown,
}

/// <summary>What to do after the steps.</summary>
public enum CombatEnding
{
    /// <summary><c>""</c> (or an unknown value): no closing row.</summary>
    None,

    /// <summary><c>end_turn</c>: end the turn.</summary>
    EndTurn,

    /// <summary><c>win</c>: the line kills every enemy.</summary>
    Win,

    /// <summary>
    /// <c>draw</c>: the last step draws cards, reshuffles or rolls something random, so what comes after it depends on
    /// information nobody has yet. The helper stops the line there and advises again once that play has resolved.
    /// </summary>
    AdviseAfterPlay,
}

/// <summary>One action of the advised order.</summary>
/// <param name="Id">The game's model id as in godot.log (<c>BASH</c>, <c>FIRE_POTION</c>), without the <c>CARD.</c> prefix.</param>
/// <param name="Upgraded">The card is upgraded (cards only).</param>
/// <param name="Hand">0-based position of the card in hand, left to right; null when not given.</param>
/// <param name="Target">Monster model id (<c>JAW_WORM</c>); null for an untargeted action.</param>
/// <param name="TargetSlot">0-based position of the target among the living enemies, left to right.</param>
/// <param name="TargetAmbiguous">Two living enemies could share the target's name: show <see cref="TargetSlot"/> too.</param>
public sealed record CombatAdviceStep(
    CombatStepKind Kind,
    string Id,
    bool Upgraded = false,
    int? Hand = null,
    string? Target = null,
    int? TargetSlot = null,
    bool TargetAmbiguous = false);

/// <summary>One parsed stdout line of the helper.</summary>
public sealed record CombatAdviceMessage(CombatAdviceState State)
{
    public static CombatAdviceMessage Idle { get; } = new(CombatAdviceState.Idle);

    /// <summary>Encounter model id (<c>JAW_WORM_WEAK</c>); null when not given.</summary>
    public string? Encounter { get; init; }

    public int? Turn { get; init; }
    public int? Energy { get; init; }
    public int? Hp { get; init; }
    public int? Block { get; init; }

    /// <summary>Actions of this turn the helper has already seen (they are not in <see cref="Steps"/>).</summary>
    public int? Played { get; init; }

    /// <summary>What to do from now on, in order.</summary>
    public IReadOnlyList<CombatAdviceStep> Steps { get; init; } = [];

    public CombatEnding Ending { get; init; }

    /// <summary><c>confidence</c> was not <c>exact</c>: the simulated hand may differ from the real one.</summary>
    public bool Uncertain { get; init; }

    /// <summary>Why the helper is unsure (tooltip of the uncertain tag); empty when none.</summary>
    public string Note { get; init; } = "";

    /// <summary>Why the fight is lost / unsupported (tooltip); empty when none.</summary>
    public string Reason { get; init; } = "";

    /// <summary>How long the search took, in milliseconds.</summary>
    public double? Ms { get; init; }
}

/// <summary>
/// Reads one stdout line of the helper. Pure and thread-safe; unit-tested. A line it cannot use returns null with a short
/// <c>problem</c> (the caller logs each kind of problem once); unknown fields are ignored.
/// </summary>
public static class CombatAdviceParser
{
    /// <summary>The protocol version this overlay reads. Lines with a higher <c>v</c> are ignored.</summary>
    public const int ProtocolVersion = 1;

    /// <returns>The message, or null when the line is blank (no problem) or unusable (<paramref name="problem"/> says why).</returns>
    public static CombatAdviceMessage? Parse(string? line, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "not a JSON object";
                return null;
            }
            if (Int(root, "v") is not { } v)
            {
                problem = "no protocol version (v)";
                return null;
            }
            if (v != ProtocolVersion)
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"protocol version {v} (this overlay reads {ProtocolVersion})");
                return null;
            }
            var state = Str(root, "state");
            switch (state)
            {
                case "idle":
                    return CombatAdviceMessage.Idle;
                case "lost":
                    return new CombatAdviceMessage(CombatAdviceState.Lost) { Encounter = Str(root, "encounter"), Reason = Str(root, "reason") ?? "" };
                case "unsupported":
                    return new CombatAdviceMessage(CombatAdviceState.Unsupported) { Encounter = Str(root, "encounter"), Reason = Str(root, "reason") ?? "" };
                case "advice":
                    return Advice(root);
                default:
                    problem = state is null ? "no state" : $"unknown state '{Short(state)}'";
                    return null;
            }
        }
        catch (JsonException)
        {
            problem = "not JSON";
            return null;
        }
    }

    private static CombatAdviceMessage Advice(JsonElement root)
    {
        var steps = new List<CombatAdviceStep>();
        if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || Str(s, "id") is not { Length: > 0 } id) continue; // nothing to show
                var kind = Str(s, "kind") switch
                {
                    "card" => CombatStepKind.Card,
                    "potion" => CombatStepKind.Potion,
                    _ => CombatStepKind.Unknown,
                };
                steps.Add(new CombatAdviceStep(kind, id, Bool(s, "upgraded"), Int(s, "hand"), Str(s, "target") is { Length: > 0 } t ? t : null,
                    Int(s, "targetSlot"), Bool(s, "targetAmbiguous")));
            }
        }
        return new CombatAdviceMessage(CombatAdviceState.Advice)
        {
            Encounter = Str(root, "encounter"),
            Turn = Int(root, "turn"),
            Energy = Int(root, "energy"),
            Hp = Int(root, "hp"),
            Block = Int(root, "block"),
            Played = Int(root, "played"),
            Steps = steps,
            Ending = Str(root, "ending") switch
            {
                "end_turn" => CombatEnding.EndTurn,
                "win" => CombatEnding.Win,
                "draw" => CombatEnding.AdviseAfterPlay,
                _ => CombatEnding.None,
            },
            // Only an explicit "exact" is trusted; a missing or unknown value shows the uncertain tag.
            Uncertain = Str(root, "confidence") != "exact",
            Note = Str(root, "note") ?? "",
            Ms = root.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : null,
        };
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Keeps a helper-made string short enough for one log line.</summary>
    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
