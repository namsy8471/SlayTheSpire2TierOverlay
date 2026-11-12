using System.Text.RegularExpressions;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>
/// Checks each committed pack's <c>text.eng.json</c> against the pack itself: every Korean tip / combo / archetype text
/// has an English twin, the English has no Hangul, and no key points at a tip/combo/archetype that no longer exists
/// (tier and combo files change on other branches; this catches the drift). One subclass per committed pack.
/// </summary>
public abstract class PackTextTests
{
    protected static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);

    /// <summary>The pack folder under <c>data/packs</c>.</summary>
    protected abstract string Pack { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<DataPack>> s_packs = new(StringComparer.Ordinal);

    protected string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", Pack);

    /// <summary>Loaded once per pack (xUnit makes a new instance per test).</summary>
    protected DataPack Loaded => s_packs.GetOrAdd(Pack, _ => new Lazy<DataPack>(() => DataPackLoader.LoadDirectory(PackDir))).Value;

    private PackText ReadText() => PackText.Parse(File.ReadAllText(Path.Combine(PackDir, PackText.EnglishFile)));

    [Fact]
    public void Every_Korean_string_has_an_English_twin()
    {
        Assert.Empty(PackText.Missing(Loaded));
    }

    [Fact]
    public void English_text_has_no_Hangul()
    {
        var text = ReadText();
        var values = text.Tips.Select(kv => ($"tip {kv.Key}", (string?)kv.Value))
            .Concat(text.Combos.SelectMany(kv => new[] { ($"combo {kv.Key} name", kv.Value.Name), ($"combo {kv.Key} why", kv.Value.Why) }))
            .Concat(text.Archetypes.Select(kv => ($"archetype {kv.Key} desc", kv.Value.Desc)));
        var bad = values.Where(v => v.Item2 is not null && Hangul.IsMatch(v.Item2)).Select(v => v.Item1).ToList();
        Assert.Empty(bad);

        var badNames = Loaded.Archetypes
            .Where(a => a.Names.TryGetValue("eng", out var n) && Hangul.IsMatch(n))
            .Select(a => a.Id).ToList();
        Assert.Empty(badNames);
    }

    [Fact]
    public void Every_English_key_matches_an_existing_Korean_string()
    {
        var loaded = Loaded;
        var text = ReadText();

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
}

/// <summary>A shipped data pack: its text also follows the overlay language for known entries.</summary>
public abstract class RealPackTextTests : PackTextTests
{
    /// <summary>English name of the Noxious Fumes + Outbreak combo (rewritten in v0.111: Outbreak became a Skill).</summary>
    protected abstract string OutbreakComboName { get; }

    [Fact]
    public void Real_pack_text_follows_the_overlay_language()
    {
        var pack = Loaded;
        var tip = pack.Tiers["CHARACTER.IRONCLAD"].Cards["CARD.ARMAMENTS"];
        var combo = pack.Combos.First(c => c.TextKey == "CARD.CORRUPTION+CARD.FEEL_NO_PAIN");
        var outbreak = pack.Combos.First(c => c.TextKey == "CARD.NOXIOUS_FUMES+CARD.OUTBREAK");
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
            Assert.Equal(OutbreakComboName, outbreak.LocalName);
            Assert.DoesNotMatch(Hangul, combo.LocalWhy!);
            Assert.DoesNotMatch(Hangul, archetype.LocalDesc!);
            Assert.Equal("Strength", archetype.DisplayName);
        }
    }
}

public sealed class PackTextTestsV0107 : RealPackTextTests
{
    protected override string Pack => "v0.107.1";
    protected override string OutbreakComboName => "Poison → AoE damage";
}

public sealed class PackTextTestsV0111 : RealPackTextTests
{
    protected override string Pack => "v0.111.0";
    protected override string OutbreakComboName => "AoE Poison → instant tick";
}

/// <summary>The example pack in <c>data/packs/sample</c> (no shipped text expectations).</summary>
public sealed class PackTextTestsSample : PackTextTests
{
    protected override string Pack => "sample";
}
