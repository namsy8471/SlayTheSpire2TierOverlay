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
    [Theory]
    [InlineData("kor")]
    [InlineData("eng")]
    public void Every_card_renders_without_unknowns(string lang)
    {
        var loc = LocalCardTextData.Localization(lang);
        if (loc.Cards.Count == 0)
        {
            output.WriteLine($"skipped: no extracted {lang} localization");
            return;
        }
        var (catalog, vars) = LocalCardTextData.LoadNumbers();

        var failures = new List<string>();
        int rendered = 0, faces = 0;
        foreach (var card in catalog.Cards)
        {
            var missing = new List<string>();
            var text = CardTextComposer.Compose(card.Id, LocalCardTextData.Facts(card), vars[card.Id], loc, missing);
            if (text is null)
            {
                failures.Add($"{card.Id}: no title");
                continue;
            }
            rendered++;
            if (missing.Count > 0) failures.Add($"{card.Id}: unknown {string.Join(",", missing.Distinct())}");
            foreach (var face in new[] { text.Base, text.Upgraded })
            {
                if (face is null) continue;
                faces++;
                var plain = CardTextRenderer.ToPlainText(face.Body);
                if (plain.IndexOfAny(['{', '}']) >= 0) failures.Add($"{card.Id}: leftover braces");
                if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"\[/?[a-z]+\]")) failures.Add($"{card.Id}: leftover tag");
                if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"[<>=]-?\d+\?")) failures.Add($"{card.Id}: leftover condition");
                if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"\b[a-zA-Z]+\(\)")) failures.Add($"{card.Id}: leftover formatter");
            }
        }
        output.WriteLine($"{catalog.GameVersion} {lang}: rendered {rendered}/{catalog.Cards.Count} cards, {faces} faces, problems {failures.Count}");
        foreach (var f in failures.Distinct().Take(40)) output.WriteLine(f);
        Assert.Empty(failures.Distinct());
        Assert.Equal(catalog.Cards.Count + catalog.Cards.Count(c => c.MaxUpgrade > 0), faces);
    }
}
