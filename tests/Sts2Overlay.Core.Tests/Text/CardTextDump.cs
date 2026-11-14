using Sts2Overlay.Core.Text;

namespace Sts2Overlay.Core.Tests.Text;

/// <summary>
/// Opt-in: with CARD_TEXT_DUMP=&lt;file&gt; set, writes every card's rendered text (both languages, base and upgraded) for a
/// manual read-through against the game. Does nothing otherwise.
/// </summary>
public class CardTextDump
{
    [Fact]
    public void Dump_all_cards_when_asked()
    {
        var target = Environment.GetEnvironmentVariable("CARD_TEXT_DUMP");
        if (string.IsNullOrEmpty(target)) return;
        var (catalog, vars) = LocalCardTextData.LoadNumbers();
        using var w = new StreamWriter(target);
        foreach (var lang in new[] { "kor", "eng" })
        {
            var loc = LocalCardTextData.Localization(lang);
            foreach (var card in catalog.Cards)
            {
                var t = CardTextComposer.Compose(card.Id, LocalCardTextData.Facts(card), vars[card.Id], loc);
                if (t is null) continue;
                foreach (var f in new[] { t.Base, t.Upgraded })
                {
                    if (f is null) continue;
                    var body = string.Concat(f.Body.Select(r => r.Style switch
                    {
                        CardTextStyle.Keyword => "<" + r.Text + ">",
                        CardTextStyle.Changed => "*" + r.Text + "*",
                        CardTextStyle.Energy => r.Text + "[E]",
                        CardTextStyle.Star => r.Text + "[S]",
                        _ => r.Text,
                    })).Replace("\n", " / ");
                    w.WriteLine($"{lang}\t{card.Id}\t{f.Title}\t{f.Cost}{(f.CostChanged ? "!" : "")}\t{f.StarCost}\t{body}");
                }
            }
        }
    }
}
