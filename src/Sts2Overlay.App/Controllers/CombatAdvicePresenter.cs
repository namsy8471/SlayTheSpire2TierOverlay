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
        if (m.Steps.Count > shown)
        {
            var more = m.Steps.Count - shown;
            rows.Add(new CombatStepVm("", Loc.T($"… {more}개 더", $"… {more} more"), IsEnding: true));
        }
        if (EndingText(m.Ending) is { } ending) rows.Add(new CombatStepVm("", ending, IsEnding: true));
        return new CombatAdviceSectionVm
        {
            Title = Title(m.Turn),
            DetailTip = Detail(m),
            IsUncertain = m.Uncertain,
            UncertainTip = m.Note.Length > 0 ? m.Note : Loc.T("계산한 손패가 실제 손패와 다를 수 있습니다", "The simulated hand may differ from the real one"),
            Steps = rows,
        };
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
