namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Event pages the game picks at random, replayed from the event's own stream (<see cref="RngSeeds.EventStream"/>).
/// events.json names such a page by a placeholder in an option's <c>next</c> (the Trial's <c>RANDOM_SCENARIO</c>);
/// this says which real page the placeholder is in this run.
/// <para>
/// The Trial (decompiled <c>Trial.Accept</c>): <c>switch (Rng.NextInt(3))</c> → MERCHANT / NOBLE / NONDESCRIPT. It is
/// the first draw on the event's stream whichever way the player gets there (Accept at once, or Reject and then
/// Accept: Reject draws nothing, and the entrant number comes from the unseeded <c>Rng.Chaotic</c>).
/// </para>
/// </summary>
public static class EventRandomPages
{
    public const string TrialEventId = "EVENT.TRIAL";

    /// <summary>The <c>next</c> of the Trial's two Accept options in events.json.</summary>
    public const string TrialPlaceholder = "RANDOM_SCENARIO";

    private static readonly string[] s_trialScenarios = ["MERCHANT", "NOBLE", "NONDESCRIPT"];

    /// <summary>True for an event with a page this class can replay.</summary>
    public static bool Handles(string eventId) => eventId == TrialEventId;

    /// <summary>The Trial's defendant page for an event stream seed.</summary>
    public static string TrialScenario(ulong eventSeed) => s_trialScenarios[new GameRng(eventSeed).NextInt(3)];

    /// <summary>Placeholder page → the real page of this run; null for an event without random pages.</summary>
    public static IReadOnlyDictionary<string, string>? For(string eventId, ulong eventSeed) =>
        eventId == TrialEventId
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [TrialPlaceholder] = TrialScenario(eventSeed) }
            : null;
}
