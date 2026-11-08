namespace Sts2Overlay.Vision.Rest;

/// <summary>
/// The options a rest-site button can offer (OptionId in the game's RestSiteOption subclasses, v0.107.1).
/// Heal and Smith are on every normal rest site; the rest come from co-op (Mend) or relics/cards.
/// </summary>
public enum RestOption
{
    /// <summary>The icon did not clearly match one known option (e.g. a new option after a game update).</summary>
    Unknown = 0,
    /// <summary>휴식: heal 30% of max HP.</summary>
    Heal,
    /// <summary>재련: upgrade a card (opens the card grid).</summary>
    Smith,
    /// <summary>치료: co-op only, heal another player.</summary>
    Mend,
    /// <summary>단련 (Girya).</summary>
    Lift,
    /// <summary>발굴 (Shovel).</summary>
    Dig,
    /// <summary>요리 (Meat Cleaver).</summary>
    Cook,
    /// <summary>복제 (Pael's Growth / Clone enchantment).</summary>
    Clone,
    /// <summary>부화 (Byrdonis Egg).</summary>
    Hatch,
    /// <summary>불붙이기 (Pumpkin Candle).</summary>
    Kindle,
    /// <summary>An icon that ships with the game (option_toke) but that no option uses in v0.107.1.</summary>
    Toke,
}

/// <summary>Animation state of a button, inferred from how large its picture is drawn.</summary>
public enum RestButtonState
{
    /// <summary>Idle (scale 1).</summary>
    Normal,
    /// <summary>Under the mouse / focused (scale 1.1, brighter).</summary>
    Hovered,
    /// <summary>Being clicked (scale 0.9).</summary>
    Pressed,
}

/// <summary>One recognised rest-site button.</summary>
/// <param name="Option">Which option the icon shows.</param>
/// <param name="Index">Position in the row, 0 = leftmost (the game's option order: Heal, Smith, Mend, relic options).</param>
/// <param name="Rect">The visible picture in 1920x1080 design units, at its current animation scale.</param>
/// <param name="Slot">The button's layout box (scale 1), handy for a marker that should not jump on hover.</param>
/// <param name="State">Idle, hovered or pressed.</param>
/// <param name="Enabled">False when the game greys the option out (e.g. Smith with nothing left to upgrade).</param>
/// <param name="Residual">Colour distance to the best icon signature (0..255 RMS, lower is better).</param>
/// <param name="Margin">How much worse the best <em>other</em> option fits (bigger = more certain identity).</param>
public sealed record RestButton(
    RestOption Option,
    int Index,
    RectF Rect,
    RectF Slot,
    RestButtonState State,
    bool Enabled,
    float Residual,
    float Margin);

/// <summary>Result of <see cref="RestSiteDetector.Detect"/>.</summary>
/// <param name="ChoiceScreen">True when the campfire's option buttons are on screen and clickable-looking.</param>
/// <param name="Buttons">The buttons left to right (empty unless <paramref name="ChoiceScreen"/>).</param>
/// <param name="CardGridOpen">
/// True when one of the game's full-screen deck card-select overlays is showing: the card grid or its
/// "before ▶▶▶ after" upgrade preview. At a rest site that is the Smith screen (or Cook's removal grid);
/// the game uses the same overlay for event/shop removals and pile views, so the caller should combine it
/// with knowing the player is at a rest site (save/log).
/// </param>
/// <param name="Detail">Probe values for logs and the bench (not for program logic).</param>
public sealed record RestSiteResult(bool ChoiceScreen, IReadOnlyList<RestButton> Buttons, bool CardGridOpen, string Detail)
{
    /// <summary>Nothing rest-related on screen.</summary>
    public static RestSiteResult None(bool grid, string detail) => new(false, [], grid, detail);

    /// <summary>The button for an option, if it is on screen.</summary>
    public RestButton? Find(RestOption option) => Buttons.FirstOrDefault(b => b.Option == option);
}
