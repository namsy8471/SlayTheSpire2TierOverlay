using System.Text.Json;
using Sts2Overlay.Core.Text;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Text;

public class RelicTextTests(ITestOutputHelper output)
{
    // Made-up strings in the game's template format; the real text stays in the extracted localization.
    private static RelicLocalization Loc(string lang, params (string Key, string Value)[] rows)
        => new(lang, rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal));

    [Fact]
    public void Fills_numbers_and_energy_icons()
    {
        var loc = Loc("eng", ("TEST_LAMP.title", "Test [gold]Lamp[/gold]"),
            ("TEST_LAMP.description", "Start each combat with [blue]{Block}[/blue] Block and gain {Energy:energyIcons()}."));
        var vars = new RelicVars { Id = "RELIC.TEST_LAMP", Vars = new() { ["Block"] = 10, ["Energy"] = 2 } };
        var text = RelicTextComposer.Compose("RELIC.TEST_LAMP", vars, loc)!;
        Assert.Equal("Test Lamp", text.Title);
        Assert.Equal("Start each combat with 10 Block and gain 2[E].", CardTextRenderer.ToPlainText(text.Body));
        Assert.Contains(text.Body, r => r.Style == CardTextStyle.Blue && r.Text == "10");
    }

    [Fact]
    public void Unknown_relic_has_no_text_and_missing_vars_are_reported()
    {
        var loc = Loc("kor", ("TEST_CUP.title", "시험 잔"), ("TEST_CUP.description", "체력을 {Heal} 회복합니다."));
        Assert.Null(RelicTextComposer.Compose("RELIC.NOPE", null, loc));
        var missing = new List<string>();
        var text = RelicTextComposer.Compose("TEST_CUP", null, loc, missing)!;
        Assert.Equal("체력을 ? 회복합니다.", CardTextRenderer.ToPlainText(text.Body));
        Assert.Equal(["Heal"], missing);
    }

    [Fact]
    public void String_vars_use_the_referenced_title_or_the_runtime_fallback()
    {
        var loc = Loc("eng", ("TEST_CLAW.title", "Claw"), ("TEST_CLAW.description", "Enchant with [purple]{EnchantmentName}[/purple]."),
            ("TEST_GLASS.title", "Glass"), ("TEST_GLASS.description", "See cards from {Character.StringValue:cond:[gold]{}[/gold]|another character}.")) with
        {
            Enchantments = new Dictionary<string, string>(StringComparer.Ordinal) { ["TEST_GOO.title"] = "Goo" },
        };
        var claw = new RelicVars { Id = "RELIC.TEST_CLAW", Strings = new() { ["EnchantmentName"] = "ENCHANTMENT.TEST_GOO.title" } };
        Assert.Equal("Enchant with Goo.", CardTextRenderer.ToPlainText(RelicTextComposer.Compose("RELIC.TEST_CLAW", claw, loc)!.Body));

        var missing = new List<string>();
        var glass = new RelicVars { Id = "RELIC.TEST_GLASS", Unresolved = ["Character"] };
        Assert.Equal("See cards from another character.", CardTextRenderer.ToPlainText(RelicTextComposer.Compose("RELIC.TEST_GLASS", glass, loc, missing)!.Body));
        Assert.Empty(missing);
    }

    [Fact]
    public void Title_only_relic_has_an_empty_body()
    {
        var text = RelicTextComposer.Compose("RELIC.TEST_BOX", null, Loc("eng", ("TEST_BOX.title", "Box")))!;
        Assert.Equal("Box", text.Title);
        Assert.Empty(text.Body);
    }

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

    /// <summary>
    /// Every relic of the committed relic catalog renders in Korean and English with the committed relic-vars file and the
    /// locally extracted localization (<c>data/local</c>, gitignored); skips (passes) without it. Target: no unknown variable,
    /// no template syntax left over.
    /// </summary>
    [Theory]
    [InlineData("kor")]
    [InlineData("eng")]
    public void Every_relic_renders_without_unknowns(string lang)
    {
        var table = Table(Path.Combine(LocDir, lang, "relics.json"));
        if (table.Count == 0)
        {
            output.WriteLine($"skipped: no extracted {lang} localization");
            return;
        }
        var loc = new RelicLocalization(lang, table) { Enchantments = Table(Path.Combine(LocDir, lang, "enchantments.json")) };
        var vars = RelicVarsFile.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "relic-vars.v0.107.1.json")))
            .Relics.ToDictionary(r => r.Id, StringComparer.Ordinal);
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "relics.v0.107.1.json")));

        var failures = new List<string>();
        int rendered = 0, total = 0;
        foreach (var r in catalog.RootElement.GetProperty("relics").EnumerateArray())
        {
            var id = r.GetProperty("id").GetString()!;
            total++;
            var missing = new List<string>();
            var text = RelicTextComposer.Compose(id, vars.GetValueOrDefault(id), loc, missing);
            if (text is null)
            {
                failures.Add($"{id}: no title");
                continue;
            }
            rendered++;
            var plain = CardTextRenderer.ToPlainText(text.Body);
            if (missing.Count > 0) failures.Add($"{id}: unknown {string.Join(",", missing.Distinct())}");
            if (plain.IndexOfAny(['{', '}']) >= 0) failures.Add($"{id}: leftover braces in \"{plain}\"");
            if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"\[/?[a-z]+\]")) failures.Add($"{id}: leftover tag in \"{plain}\"");
        }
        output.WriteLine($"{lang}: rendered {rendered}/{total}, problems {failures.Count}");
        foreach (var f in failures.Take(60)) output.WriteLine(f);
        Assert.Empty(failures);
    }
}
