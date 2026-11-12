using System.Globalization;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.CombatAdvice;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Controllers;

/// <summary>Names for the play order rows, in the overlay language.</summary>
public interface ICombatNames
{
    /// <summary>Card name for a log id (<c>BASH</c>), with "+" when <paramref name="upgraded"/>.</summary>
    string Card(string id, bool upgraded);

    /// <summary>Potion name for a log id (<c>FIRE_POTION</c>), or null when unknown.</summary>
    string? Potion(string id);

    /// <summary>Monster name for a model id (<c>JAW_WORM</c>), or null when unknown.</summary>
    string? Monster(string id);
}

/// <summary>The app's names: cards from the catalog (<see cref="CardInfoProvider"/>), monsters and potions from the game's localization.</summary>
public sealed class CombatNames(CardInfoProvider cards, CombatTextProvider text) : ICombatNames
{
    // The log prints a card's Id.Entry ("BASH"); the catalog and the save use the full id ("CARD.BASH").
    public string Card(string id, bool upgraded)
        => cards.Name(new CardRef(id.StartsWith("CARD.", StringComparison.Ordinal) ? id : "CARD." + id, upgraded ? 1 : 0));

    public string? Potion(string id) => text.PotionName(id);

    public string? Monster(string id) => text.MonsterName(id);
}

/// <summary>Where the player is, as far as the play order cares (<see cref="CombatAdvicePresenter.PlaceOf"/>).</summary>
public enum CombatAdvicePlace
{
    /// <summary>No fight can be on screen (map, shop, rest site, rewards, menu; no game window; helper not running).</summary>
    Elsewhere,

    /// <summary>The tracker says a fight is on.</summary>
    Fight,

    /// <summary>
    /// An event room. An event can start a fight in its own room, and the log does not say so until the first card is
    /// played, so here the helper's own state decides: it is idle unless it follows such a fight.
    /// </summary>
    EventRoom,
}

/// <summary>
/// Turns the helper's last line into the "이번 턴 추천 순서" section. Pure; unit-tested.
/// <para>
/// Hidden (<see cref="CombatAdviceSectionVm.Empty"/>) when there is no line yet, the helper is idle, or the overlay does
/// not think a fight can be on screen (<see cref="CombatAdvicePlace.Elsewhere"/>), whatever the last line said: a helper
/// that is slow to say "idle" must not leave old advice on the map screen.
/// </para>
/// <para>
/// In an event room (<see cref="CombatAdvicePlace.EventRoom"/>) the helper's line is shown as it is. The game builds the
/// event's combat room when the event page opens, so the helper may already send the fight's first turn while the
/// player is still reading the event: that line (uncertain, turn 1, nothing played) gets the muted "전투를 고르면" tag
/// (<see cref="IsEventPreview"/>) until the helper reports progress.
/// </para>
/// </summary>
public static class CombatAdvicePresenter
{
    /// <summary>Rows shown at most; the rest is summed up in one "…" row so the panel never grows over the hand.</summary>
    public const int MaxSteps = 10;

    /// <summary>
    /// The visibility rule's first half: is a fight possible where the player is?
    /// <code>
    ///  helper not running, or no game window ─▶ Elsewhere (hidden)
    ///  tracker hint = Combat                 ─▶ Fight     (shown unless the helper is idle)
    ///  tracker in an event room              ─▶ EventRoom (shown unless the helper is idle; first turn marked "전투를 고르면")
    ///  anything else (map, shop, rewards …)  ─▶ Elsewhere (hidden)
    /// </code>
    /// </summary>
    /// <param name="helperRunning">The helper process is started (single-player run in progress, feature on).</param>
    /// <param name="gameRunning">The game window exists.</param>
    /// <param name="hint">The tracker's screen hint (<see cref="GameStateSnapshot.Hint"/>).</param>
    /// <param name="inEventRoom"><see cref="GameStateSnapshot.InEventRoom"/>.</param>
    public static CombatAdvicePlace PlaceOf(bool helperRunning, bool gameRunning, ScreenHint hint, bool inEventRoom)
    {
        if (!helperRunning || !gameRunning) return CombatAdvicePlace.Elsewhere;
        if (hint == ScreenHint.Combat) return CombatAdvicePlace.Fight;
        return inEventRoom ? CombatAdvicePlace.EventRoom : CombatAdvicePlace.Elsewhere;
    }

    public static CombatAdviceSectionVm Build(CombatAdviceMessage? message, CombatAdvicePlace place, ICombatNames names)
    {
        if (place == CombatAdvicePlace.Elsewhere || message is null) return CombatAdviceSectionVm.Empty;
        return message.State switch
        {
            CombatAdviceState.Advice => Advice(message, names, preview: place == CombatAdvicePlace.EventRoom && IsEventPreview(message)),
            CombatAdviceState.Lost => Status(LostText, message.Reason),
            CombatAdviceState.Unsupported => Status(UnsupportedText, message.Reason),
            _ => CombatAdviceSectionVm.Empty,
        };
    }

    /// <summary>
    /// Advice the helper made before it could know the fight is on: unsure, first turn, nothing played yet. In an event
    /// room that is the state while the event page is still open. Any progress (an action seen, a later turn, an exact
    /// line) ends it.
    /// </summary>
    public static bool IsEventPreview(CombatAdviceMessage m) => m.Uncertain && m.Turn == 1 && (m.Played ?? 0) == 0;

    /// <summary>The muted header tag of a preview: the order holds if the player picks the event's fight option.</summary>
    public static string PreviewHintText => Loc.T("전투를 고르면", "if you fight");

    /// <summary>The preview tag's tooltip (instead of the helper's English note).</summary>
    public static string PreviewTipText => Loc.T(
        "이벤트에서 전투를 고른다고 가정한 첫 턴 추천입니다. 아직 고르기 전이라면 미리 보기로만 참고하세요. 계산한 손패가 실제 손패와 다를 수 있습니다.",
        "The first turn's order, assuming you pick the event's fight option. If you have not chosen yet, it is only a preview. The simulated hand may differ from the real one.");

    /// <summary>
    /// The note of a fight that started from an event (docs/COMBAT_ADVISOR.md), e.g.
    /// "event fight from PUNCH_OFF: assumes the options I_CAN_TAKE_THEM, then FIGHT": the helper cannot see which options
    /// were picked in single player, so it assumes the ones that lead to the fight.
    /// </summary>
    public static bool IsEventFightNote(string note) => note.StartsWith("event fight from", StringComparison.OrdinalIgnoreCase);

    /// <summary>The uncertain tag's tooltip for <see cref="IsEventFightNote"/> (instead of the English note with its ids).</summary>
    public static string EventFightTipText => Loc.T(
        "이벤트에서 시작한 전투라서, 이벤트에서 고른 선택지를 가정해 계산했습니다. 계산한 손패가 실제 손패와 다를 수 있습니다.",
        "This fight started from an event, so the options picked in the event are assumed. The simulated hand may differ from the real one.");

    public static string LostText => Loc.T("이 전투는 추적이 끊겼습니다", "Lost track of this fight");

    public static string UnsupportedText => Loc.T("이 전투는 아직 지원하지 않습니다", "This fight is not supported yet");

    private static CombatAdviceSectionVm Status(string text, string reason)
        => new() { StatusText = text, StatusTip = reason.Length > 0 ? reason : null };

    private static CombatAdviceSectionVm Advice(CombatAdviceMessage m, ICombatNames names, bool preview)
    {
        var rows = new List<CombatStepVm>();
        var shown = Math.Min(m.Steps.Count, MaxSteps);
        for (var i = 0; i < shown; i++) rows.Add(Step(i + 1, m.Steps[i], names));
        var more = m.Steps.Count - shown;
        var reason = DrawReasonOf(m.Note);
        if (m.Ending == CombatEnding.AdviseAfterPlay)
        {
            // "이 카드" is the last step. When the cap hides it among the "N개 더", the two rows merge into one so the pause
            // does not seem to come after the 10th row.
            var text = more > 0 ? AdviseAfterMoreText(more) : AdviseAfterPlayText;
            rows.Add(new CombatStepVm("", text, IsEnding: true, Tip: AdviseAfterPlayTip(reason), IsSoft: true));
        }
        else
        {
            if (more > 0) rows.Add(new CombatStepVm("", Loc.T($"… {more}개 더", $"… {more} more"), IsEnding: true));
            if (EndingText(m.Ending) is { } ending) rows.Add(new CombatStepVm("", ending, IsEnding: true));
        }
        return new CombatAdviceSectionVm
        {
            Title = Title(m.Turn),
            DetailTip = Detail(m),
            // A preview says more than "불확실" (why it is unsure, and that the fight may not have started): one tag, not two.
            IsUncertain = m.Uncertain && !preview,
            // The helper's note is English; a known draw note is already said (localized) by the closing row's tooltip,
            // and the event-fight note has its own localized text.
            UncertainTip = IsEventFightNote(m.Note) ? EventFightTipText
                : m.Note.Length > 0 && reason is null ? m.Note
                : Loc.T("계산한 손패가 실제 손패와 다를 수 있습니다", "The simulated hand may differ from the real one"),
            PreviewHint = preview ? PreviewHintText : null,
            PreviewTip = preview ? PreviewTipText : null,
            Steps = rows,
        };
    }

    /// <summary>Why the helper stopped at a <c>draw</c> step, read from its (English) note; null for any other note.</summary>
    public enum DrawReason
    {
        CardsDrawn,
        PileOrder,
        Random,
    }

    /// <summary>The three notes the helper sends with <c>ending: "draw"</c> (docs/COMBAT_ADVISOR.md); anything else → null.</summary>
    public static DrawReason? DrawReasonOf(string note)
    {
        if (!note.StartsWith("next steps depend on", StringComparison.OrdinalIgnoreCase)) return null;
        if (note.Contains("random", StringComparison.OrdinalIgnoreCase)) return DrawReason.Random;
        if (note.Contains("order", StringComparison.OrdinalIgnoreCase)) return DrawReason.PileOrder;
        if (note.Contains("drawn", StringComparison.OrdinalIgnoreCase)) return DrawReason.CardsDrawn;
        return null;
    }

    /// <summary>The closing row of a <c>draw</c> line: the advice pauses at the last step.</summary>
    public static string AdviseAfterPlayText => Loc.T("… 이 카드를 낸 뒤 다시 추천", "… more after this play");

    /// <summary>The same, when the row cap hides the last steps: one row instead of "… N개 더" + the pause.</summary>
    public static string AdviseAfterMoreText(int more) => Loc.T($"… {more}개 더 낸 뒤 다시 추천", $"… {more} more, then new advice");

    /// <summary>
    /// Tooltip of the pause row: why the list stops (localized from the helper's note), and that this is on purpose: the
    /// overlay never shows cards that are not drawn yet or results that are not rolled yet.
    /// </summary>
    public static string AdviseAfterPlayTip(DrawReason? reason)
    {
        var why = reason switch
        {
            DrawReason.CardsDrawn => Loc.T("다음 행동은 이 카드로 뽑을 카드에 따라 달라집니다.", "What comes next depends on the cards this play draws."),
            DrawReason.PileOrder => Loc.T("다음 행동은 섞인 뽑을 카드 더미의 새 순서에 따라 달라집니다.", "What comes next depends on the draw pile's new order."),
            DrawReason.Random => Loc.T("다음 행동은 무작위 결과에 따라 달라집니다.", "What comes next depends on a random result."),
            _ => Loc.T("다음 행동은 뽑거나 무작위로 정해질 결과에 따라 달라집니다.", "What comes next depends on what is drawn or rolled."),
        };
        return why + Loc.T(" 아직 모르는 카드나 결과를 미리 보여 주지 않도록, 이 카드를 낸 뒤에 이어서 추천합니다.",
            " So that nothing you could not know yet is shown, the rest is advised after this play.");
    }

    /// <summary>"이번 턴 추천 순서 · 2턴" / "This turn's plays · Turn 2".</summary>
    public static string Title(int? turn)
    {
        var head = Loc.T("이번 턴 추천 순서", "This turn's plays");
        return turn is { } t ? head + Loc.T($" · {t}턴", $" · Turn {t}") : head;
    }

    public static string? EndingText(CombatEnding ending) => ending switch
    {
        CombatEnding.EndTurn => Loc.T("턴 종료", "End turn"),
        CombatEnding.Win => Loc.T("마무리!", "Finish!"),
        _ => null,
    };

    /// <summary>The small tag next to the header when the helper is unsure.</summary>
    public static string UncertainTag => Loc.T("불확실", "uncertain");

    private static CombatStepVm Step(int number, CombatAdviceStep s, ICombatNames names)
    {
        var numberText = number.ToString(CultureInfo.InvariantCulture) + ".";
        var (name, tag) = s.Kind switch
        {
            CombatStepKind.Card => (names.Card(s.Id, s.Upgraded), (string?)null),
            CombatStepKind.Potion => (names.Potion(s.Id) ?? Prettify(s.Id), Loc.T("포션", "Potion")),
            _ => (s.Id, null), // a kind this overlay does not know: the raw id
        };
        string? tip = s.Kind == CombatStepKind.Card && s.Hand is { } h
            ? Loc.T($"손패 왼쪽에서 {h + 1}번째 카드", $"Card {h + 1} from the left in your hand")
            : null;
        return new CombatStepVm(numberText, name, TargetText(s, names), tag, Tip: tip);
    }

    /// <summary>
    /// The target's name; with its position ("턱벌레 (2번째)") only when the helper says two living enemies could share the
    /// name. Without a name for the id: "2번째 적" from the position, else the prettified id.
    /// </summary>
    public static string? TargetText(CombatAdviceStep s, ICombatNames names)
    {
        if (s.Target is null && s.TargetSlot is null) return null;
        var name = s.Target is { } id ? names.Monster(id) : null;
        if (name is null)
        {
            if (s.TargetSlot is { } slot) return Loc.T($"{slot + 1}번째 적", $"enemy {slot + 1}");
            return Prettify(s.Target!);
        }
        return s.TargetAmbiguous && s.TargetSlot is { } n ? name + Loc.T($" ({n + 1}번째)", $" (#{n + 1})") : name;
    }

    private static string? Detail(CombatAdviceMessage m)
    {
        var parts = new List<string>();
        if (m.Energy is { } e) parts.Add(Loc.T($"에너지 {e}", $"Energy {e}"));
        if (m.Hp is { } hp) parts.Add(Loc.T($"체력 {hp}", $"HP {hp}"));
        if (m.Block is { } b) parts.Add(Loc.T($"방어도 {b}", $"Block {b}"));
        if (m.Played is { } p && p > 0) parts.Add(Loc.T($"이번 턴 {p}번 행동함", $"{p} actions taken this turn"));
        if (m.Ms is { } ms) parts.Add(ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string Prettify(string id)
    {
        var dot = id.IndexOf('.', StringComparison.Ordinal);
        var raw = dot >= 0 ? id[(dot + 1)..] : id;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
    }
}
