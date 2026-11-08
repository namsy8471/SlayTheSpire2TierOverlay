using Sts2Overlay.Core.Combat;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// Run facts the event scorer needs beyond <see cref="Model.RunState"/>: how many players are in the run, and the
/// damage forecast for timed "kill the dummy" fights.
/// </summary>
public sealed record EventContext
{
    public int PlayerCount { get; init; } = 1;

    /// <summary>
    /// Damage of all players together in a fight against a target that never acts, over the first
    /// <see cref="ForecastTurns"/> turns. Null = unknown (the dummy fight's win chance is then not modelled).
    /// </summary>
    public DamageForecast? Forecast { get; init; }

    public int ForecastTurns { get; init; } = 3;

    /// <summary>Short Korean notes about how the forecast was made (e.g. "동료 피해는 내 덱 기준 추정").</summary>
    public IReadOnlyList<string> ForecastNotes { get; init; } = [];
}

/// <summary>The training dummy's HP rules (BattlewornDummy event).</summary>
public static class DummyFight
{
    public const string EventId = "EVENT.BATTLEWORN_DUMMY";

    /// <summary>
    /// <c>Creature.ScaleHpForMultiplayer</c>: one player keeps the base HP; more players multiply it by the player count
    /// and <c>MultiplayerScalingModel.GetMultiplayerScaling</c> (act index 0 → 1.1, 1 → 1.2, 2 → 1.2 outside boss rooms),
    /// truncated to an integer.
    /// </summary>
    public static int ScaleHp(double baseHp, int playerCount, int actIndex)
    {
        if (playerCount <= 1) return (int)baseHp;
        var factor = actIndex switch { 0 => 1.1m, 1 => 1.2m, _ => 1.2m };
        return (int)((decimal)baseHp * playerCount * factor);
    }

    /// <summary>
    /// How the advisor simulates player <paramref name="player"/> (0 = me): a few hundred 3-turn fights, each card choice
    /// tried out twice ahead (<see cref="SimOptions.Rollouts"/>). Every player gets its own seed, so two copies of one deck
    /// don't draw the same hands.
    /// </summary>
    public static SimOptions SimOptionsFor(int player)
        => new() { Seed = new SimOptions().Seed + player, Rollouts = 2, Iterations = 300 };

    /// <summary>Everyone's damage on the dummy together; the players are simulated side by side on worker threads.</summary>
    public static DamageForecast Forecast(TurnSimulator sim, IReadOnlyList<SimPlayer> players)
    {
        var parts = new DamageForecast[players.Count];
        Parallel.For(0, players.Count, k => parts[k] = sim.Forecast(players[k], SimOptionsFor(k)));
        return parts.Length == 1 ? parts[0] : DamageForecast.Combine(parts);
    }
}
