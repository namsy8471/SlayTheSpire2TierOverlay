using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Text;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Text;

/// <summary>
/// Renders every catalog card, base and upgraded, in Korean and English with the committed card-vars file and the
/// locally extracted game localization (<c>data/local/pck/localization</c>, gitignored). Skips (passes) without it.
/// Target: 100% of cards rendered with no unknown variable and no template syntax left over.
/// </summary>
public class CardTextCoverageTests(ITestOutputHelper output)
{
    private static string LocDir => Path.Combine(TestPaths.RepoRoot, "data", "local", "pck", "localization");

    private static Dictionary<string, string> Table(string path)
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return t;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) t[p.Name] = p.Value.GetString()!;
        return t;
    }

    [Theory]
    [InlineData("kor")]
    [InlineData("eng")]
    public void Every_card_renders_without_unknowns(string lang)
    {
        var cards = Table(Path.Combine(LocDir, lang, "cards.json"));
        if (cards.Count == 0)
        {
            output.WriteLine($"skipped: no extracted {lang} localization");
            return;
        }
        var loc = new CardLocalization(lang, cards, Table(Path.Combine(LocDir, lang, "card_keywords.json")));
        var catalog = DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"));
        var vars = CardVarsFile.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "card-vars.v0.107.1.json")))
            .Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var failures = new List<string>();
        int rendered = 0;
        foreach (var card in catalog.Cards)
        {
            var missing = new List<string>();
            var facts = new CardTextFacts(card.Cost ?? -1, card.CostX, card.StarCost, card.Keywords, card.MaxUpgrade ?? 0)
            {
                GainsBlock = card.GainsBlock,
                Target = card.Target,
            };
            var text = CardTextComposer.Compose(card.Id, facts, vars.GetValueOrDefault(card.Id), loc, missing);
            if (text is null)
            {
                failures.Add($"{card.Id}: no title");
                continue;
            }
            rendered++;
            foreach (var face in new[] { text.Base, text.Upgraded })
            {
                if (face is null) continue;
                var plain = CardTextRenderer.ToPlainText(face.Body);
                if (missing.Count > 0) failures.Add($"{card.Id}: unknown {string.Join(",", missing.Distinct())}");
                if (plain.IndexOfAny(['{', '}']) >= 0) failures.Add($"{card.Id}: leftover braces in \"{plain}\"");
                if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"\[/?[a-z]+\]")) failures.Add($"{card.Id}: leftover tag in \"{plain}\"");
                if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"[<>=]-?\d+\?")) failures.Add($"{card.Id}: leftover condition in \"{plain}\"");
                missing.Clear();
            }
        }
        output.WriteLine($"{lang}: rendered {rendered}/{catalog.Cards.Count}, problems {failures.Count}");
        foreach (var f in failures.Distinct().Take(40)) output.WriteLine(f);
        Assert.Empty(failures.Distinct());
    }
}
