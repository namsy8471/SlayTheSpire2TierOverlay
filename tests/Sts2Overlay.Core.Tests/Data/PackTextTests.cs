using System.Text.RegularExpressions;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>
/// Checks each committed pack's <c>text.eng.json</c> against the pack itself: every Korean tip / combo / archetype text
/// has an English twin, the English has no Hangul, and no key points at a tip/combo/archetype that no longer exists
/// (tier and combo files change on other branches; this catches the drift).
/// </summary>
public class PackTextTests
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);

    public static TheoryData<string> Packs => new() { "v0.107.1", "sample" };

    private static string PackDir(string pack) => Path.Combine(TestPaths.RepoRoot, "data", "packs", pack);

    private static PackText ReadText(string pack)
        => PackText.Parse(File.ReadAllText(Path.Combine(PackDir(pack), PackText.EnglishFile)));

    [Theory]
    [MemberData(nameof(Packs))]
    public void Every_Korean_string_has_an_English_twin(string pack)
    {
        var loaded = DataPackLoader.LoadDirectory(PackDir(pack));
        Assert.Empty(PackText.Missing(loaded));
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void English_text_has_no_Hangul(string pack)
    {
        var text = ReadText(pack);
        var values = text.Tips.Select(kv => ($"tip {kv.Key}", (string?)kv.Value))
            .Concat(text.Combos.SelectMany(kv => new[] { ($"combo {kv.Key} name", kv.Value.Name), ($"combo {kv.Key} why", kv.Value.Why) }))
            .Concat(text.Archetypes.Select(kv => ($"archetype {kv.Key} desc", kv.Value.Desc)));
        var bad = values.Where(v => v.Item2 is not null && Hangul.IsMatch(v.Item2)).Select(v => v.Item1).ToList();
        Assert.Empty(bad);

        var loaded = DataPackLoader.LoadDirectory(PackDir(pack));
        var badNames = loaded.Archetypes
            .Where(a => a.Names.TryGetValue("eng", out var n) && Hangul.IsMatch(n))
            .Select(a => a.Id).ToList();
        Assert.Empty(badNames);
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void Every_English_key_matches_an_existing_Korean_string(string pack)
    {
        var loaded = DataPackLoader.LoadDirectory(PackDir(pack));
        var text = ReadText(pack);

        var tipIds = loaded.Tiers.Values.SelectMany(t => t.Cards)
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Tip)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var comboKeys = loaded.Combos.Select(c => c.TextKey).ToHashSet(StringComparer.Ordinal);
        var archetypeIds = loaded.Archetypes
            .Where(a => !string.IsNullOrWhiteSpace(a.Desc)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        var stale = text.Tips.Keys.Where(k => !tipIds.Contains(k)).Select(k => $"tip {k}")
            .Concat(text.Combos.Keys.Where(k => !comboKeys.Contains(k)).Select(k => $"combo {k}"))
            .Concat(text.Archetypes.Keys.Where(k => !archetypeIds.Contains(k)).Select(k => $"archetype {k}"))
            .ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void Real_pack_text_follows_the_overlay_language()
    {
        var pack = DataPackLoader.LoadDirectory(PackDir("v0.107.1"));
        var tip = pack.Tiers["CHARACTER.IRONCLAD"].Cards["CARD.ARMAMENTS"];
        var combo = pack.Combos.First(c => c.TextKey == "CARD.CORRUPTION+CARD.FEEL_NO_PAIN");
        var archetype = pack.Archetypes.First(a => a.Id == "ironclad.strength");

        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal(tip.Tip, tip.LocalTip);
            Assert.Equal(combo.Name, combo.LocalName);
            Assert.Equal(archetype.Desc, archetype.LocalDesc);
        }
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Upgraded, it upgrades your whole Hand", tip.LocalTip);
            Assert.Equal("Free Skills + Exhaust Block", combo.LocalName);
            Assert.DoesNotMatch(Hangul, combo.LocalWhy!);
            Assert.DoesNotMatch(Hangul, archetype.LocalDesc!);
            Assert.Equal("Strength", archetype.DisplayName);
        }
    }
}
