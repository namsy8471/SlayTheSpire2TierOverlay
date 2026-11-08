using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.Core.Tests.Localization;

public class LocTests
{
    [Fact]
    public void T_picks_the_scoped_language()
    {
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Lacks AoE", Loc.T("광역 부족", "Lacks AoE"));
            using (Loc.Use(Lang.Kor)) Assert.Equal("광역 부족", Loc.T("광역 부족", "Lacks AoE"));
            Assert.Equal(Lang.Eng, Loc.Current);
        }
    }

    [Theory]
    [InlineData("kor", Lang.Kor)]
    [InlineData("KOR", Lang.Kor)]
    [InlineData("eng", Lang.Eng)]
    [InlineData("jpn", Lang.Eng)]
    [InlineData("zhs", Lang.Eng)]
    public void Game_language_codes_map_to_overlay_languages(string code, Lang expected)
        => Assert.Equal(expected, Loc.FromGameCode(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Unknown_game_language_is_null(string? code) => Assert.Null(Loc.FromGameCode(code));

    [Fact]
    public void Pick_falls_back_from_the_current_language_to_english_then_korean()
    {
        var both = new Dictionary<string, string> { ["kor"] = "힘 성장", ["eng"] = "Strength" };
        var korOnly = new Dictionary<string, string> { ["kor"] = "힘 성장" };
        var engOnly = new Dictionary<string, string> { ["eng"] = "Strength" };
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("힘 성장", Loc.Pick(both));
            Assert.Equal("Strength", Loc.Pick(engOnly));
        }
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Strength", Loc.Pick(both));
            Assert.Equal("힘 성장", Loc.Pick(korOnly));
            Assert.Null(Loc.Pick(new Dictionary<string, string>()));
        }
    }

    [Fact]
    public void Archetype_display_name_follows_the_language()
    {
        var a = new Archetype { Id = "x.y", Names = { ["kor"] = "힘 성장", ["eng"] = "Strength" } };
        using (Loc.Use(Lang.Kor)) Assert.Equal("힘 성장", a.DisplayName);
        using (Loc.Use(Lang.Eng)) Assert.Equal("Strength", a.DisplayName);
    }

    [Fact]
    public void Pack_text_fills_english_tips_combos_and_descriptions()
    {
        var tier = new TierPack { Character = "C", Cards = { ["CARD.A"] = new CardTierEntry { Tip = "팁" } } };
        var arch = new Archetype { Id = "c.a", Desc = "설명", Names = { ["kor"] = "가", ["eng"] = "A" } };
        var combo = new Combo { Cards = ["CARD.A", "CARD.B"], Name = "이름", Why = "이유" };
        var text = PackText.Parse("""
            { // comments are fine
              "tips": { "CARD.A": "Tip" },
              "combos": { "CARD.A+CARD.B": { "name": "Name", "why": "Why" } },
              "archetypes": { "c.a": { "desc": "Desc" } },
            }
            """);
        text.ApplyTo([tier], [arch], [combo]);

        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Tip", tier.Cards["CARD.A"].LocalTip);
            Assert.Equal("Name", combo.LocalName);
            Assert.Equal("Why", combo.LocalWhy);
            Assert.Equal("Desc", arch.LocalDesc);
        }
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("팁", tier.Cards["CARD.A"].LocalTip);
            Assert.Equal("이름", combo.LocalName);
        }
        var pack = new DataPack { Tiers = new Dictionary<string, TierPack> { ["C"] = tier }, Archetypes = [arch], Combos = [combo] };
        Assert.Empty(PackText.Missing(pack));
    }
}
