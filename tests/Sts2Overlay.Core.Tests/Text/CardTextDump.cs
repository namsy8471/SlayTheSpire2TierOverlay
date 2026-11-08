using System.Text.Json;
using Sts2Overlay.Core.Data;
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
        var locDir = Path.Combine(TestPaths.RepoRoot, "data", "local", "pck", "localization");
        var catalog = DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"));
        var vars = CardVarsFile.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "card-vars.v0.107.1.json")))
            .Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        using var w = new StreamWriter(target);
        foreach (var lang in new[] { "kor", "eng" })
        {
            var loc = new CardLocalization(lang, Table(Path.Combine(locDir, lang, "cards.json")), Table(Path.Combine(locDir, lang, "card_keywords.json")));
            foreach (var card in catalog.Cards)
            {
                var facts = new CardTextFacts(card.Cost ?? -1, card.CostX, card.StarCost, card.Keywords, card.MaxUpgrade ?? 0)
                {
                    GainsBlock = card.GainsBlock,
                    Target = card.Target,
                };
                var t = CardTextComposer.Compose(card.Id, facts, vars.GetValueOrDefault(card.Id), loc);
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

    private static Dictionary<string, string> Table(string path)
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return t;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) t[p.Name] = p.Value.GetString()!;
        return t;
    }
}
