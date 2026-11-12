using System.Globalization;
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

/// <summary>
/// Turns the helper's last line into the "이번 턴 추천 순서" section. Pure; unit-tested.
/// <para>
/// Hidden (<see cref="CombatAdviceSectionVm.Empty"/>) when there is no line yet, the helper is idle, or the overlay does
/// not think a fight is on screen (<paramref name="inCombat"/>: game running and the tracker in a combat room), whatever
/// the last line said: a helper that is slow to say "idle" must not leave old advice on the map screen.
/// </para>
/// </summary>
public static class CombatAdvicePresenter
{
    /// <summary>Rows shown at most; the rest is summed up in one "…" row so the panel never grows over the hand.</summary>
    public const int MaxSteps = 10;

    public static CombatAdviceSectionVm Build(CombatAdviceMessage? message, bool inCombat, ICombatNames names)
    {
        if (!inCombat || message is null) return CombatAdviceSectionVm.Empty;
        return message.State switch
        {
            CombatAdviceState.Advice => Advice(message, names),
            CombatAdviceState.Lost => Status(LostText, message.Reason),
            CombatAdviceState.Unsupported => Status(UnsupportedText, message.Reason),
            _ => CombatAdviceSectionVm.Empty,
        };
    }

    public static string LostText => Loc.T("이 전투는 추적이 끊겼습니다", "Lost track of this fight");

    public static string UnsupportedText => Loc.T("이 전투는 아직 지원하지 않습니다", "This fight is not supported yet");

    private static CombatAdviceSectionVm Status(string text, string reason)
        => new() { StatusText = text, StatusTip = reason.Length > 0 ? reason : null };

    private static CombatAdviceSectionVm Advice(CombatAdviceMessage m, ICombatNames names)
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
            IsUncertain = m.Uncertain,
            // The helper's note is English; a known draw note is already said (localized) by the closing row's tooltip.
            UncertainTip = m.Note.Length > 0 && reason is null
                ? m.Note
                : Loc.T("계산한 손패가 실제 손패와 다를 수 있습니다", "The simulated hand may differ from the real one"),
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
